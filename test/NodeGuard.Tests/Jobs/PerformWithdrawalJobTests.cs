/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quartz;

namespace NodeGuard.Jobs;

public class PerformWithdrawalJobTests
{
    private const int RequestId = 12;

    private readonly IBitcoinService _bitcoinService = Substitute.For<IBitcoinService>();
    private readonly IWalletWithdrawalRequestRepository _repository = Substitute.For<IWalletWithdrawalRequestRepository>();
    private readonly IWithdrawalInterceptorGate _gate = Substitute.For<IWithdrawalInterceptorGate>();
    private readonly IWithdrawalRequestService _withdrawalRequestService = Substitute.For<IWithdrawalRequestService>();
    private readonly IAuditService _auditService = Substitute.For<IAuditService>();
    private readonly IJobExecutionContext _context = Substitute.For<IJobExecutionContext>();
    private readonly PerformWithdrawalJob _job;
    private readonly WalletWithdrawalRequest _request = new()
    {
        Id = RequestId,
        WalletId = 4,
        Status = WalletWithdrawalRequestStatus.PSBTSignaturesPending,
        BumpingWalletWithdrawalRequestId = 11
    };

    public PerformWithdrawalJobTests()
    {
        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.JobDataMap.Returns(new JobDataMap { { "withdrawalRequestId", RequestId } });
        _context.JobDetail.Returns(jobDetail);
        _repository.GetById(RequestId).Returns(_request);
        _repository.Update(Arg.Any<WalletWithdrawalRequest>()).Returns((true, null));

        _job = new PerformWithdrawalJob(NullLogger<PerformWithdrawalJob>.Instance, _bitcoinService, _repository, _gate,
            _withdrawalRequestService, _auditService);
    }

    private void GateAnswers(WithdrawalInterceptOutcome outcome) =>
        _gate.RequestDecisionAsync(_request, Arg.Any<CancellationToken>()).Returns(outcome);

    [Fact]
    public async Task Execute_NobodyIntercepting_PerformsTheWithdrawal()
    {
        GateAnswers(WithdrawalInterceptOutcome.Skipped);

        await _job.Execute(_context);

        await _bitcoinService.Received(1).PerformWithdrawal(_request);
        await _auditService.DidNotReceiveWithAnyArgs().LogSystemAsync(default, default, default);
    }

    [Fact]
    public async Task Execute_IntegratorAllows_PerformsTheWithdrawalAndAuditsIt()
    {
        GateAnswers(new WithdrawalInterceptOutcome(WithdrawalInterceptOutcomeKind.Allowed, "d1", ["integrator"]));

        await _job.Execute(_context);

        await _bitcoinService.Received(1).PerformWithdrawal(_request);
        await _auditService.Received(1).LogSystemAsync(AuditActionType.WithdrawalAllowedByIntegrator,
            AuditEventType.Success, AuditObjectType.WalletWithdrawalRequest, RequestId.ToString(), Arg.Any<object>());
    }

    [Fact]
    public async Task Execute_IntegratorBlocks_RejectsWithoutSigningAndRevertsTheOriginal()
    {
        GateAnswers(new WithdrawalInterceptOutcome(WithdrawalInterceptOutcomeKind.Blocked, "d1", ["compliance"],
            "sanctioned address", "compliance"));

        await _job.Execute(_context);

        await _bitcoinService.DidNotReceiveWithAnyArgs().PerformWithdrawal(default!);
        _request.Status.Should().Be(WalletWithdrawalRequestStatus.Rejected);
        _request.RejectCancelDescription.Should().Be("Blocked by integrator 'compliance': sanctioned address");
        _repository.Received(1).Update(_request);
        await _withdrawalRequestService.Received(1).RevertOriginalAsync(_request);
        await _auditService.Received(1).LogSystemAsync(AuditActionType.WithdrawalBlockedByIntegrator,
            AuditEventType.Success, AuditObjectType.WalletWithdrawalRequest, RequestId.ToString(), Arg.Any<object>());
    }

    [Fact]
    public async Task Execute_BlockedButNotPersisted_NeverSignsAndFailsTheRequest()
    {
        GateAnswers(new WithdrawalInterceptOutcome(WithdrawalInterceptOutcomeKind.Blocked, "d1", ["compliance"], "no", "compliance"));
        _repository.Update(Arg.Is<WalletWithdrawalRequest>(r => r.Status == WalletWithdrawalRequestStatus.Rejected))
            .Returns((false, "db down"));

        await _job.Execute(_context);

        await _bitcoinService.DidNotReceiveWithAnyArgs().PerformWithdrawal(default!);
        _request.Status.Should().Be(WalletWithdrawalRequestStatus.Failed);
    }

    [Fact]
    public async Task Execute_RequestNotReadyToSign_IsNotPutToTheIntegrators()
    {
        _request.Status = WalletWithdrawalRequestStatus.Pending;

        await _job.Execute(_context);

        await _gate.DidNotReceiveWithAnyArgs().RequestDecisionAsync(default!, default);
    }

    [Fact]
    public async Task Execute_SigningFails_RevertsTheOriginalAndFailsTheRequest()
    {
        GateAnswers(WithdrawalInterceptOutcome.Skipped);
        _bitcoinService.PerformWithdrawal(_request).ThrowsAsync(new InvalidOperationException("signer down"));

        await _job.Execute(_context);

        await _withdrawalRequestService.Received(1).RevertOriginalAsync(_request);
        _request.Status.Should().Be(WalletWithdrawalRequestStatus.Failed);
    }
}
