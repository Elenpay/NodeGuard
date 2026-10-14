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
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// When PerformWithdrawalJob cannot sign or broadcast, the request fails and, if it was an RBF replacement, the request
/// it replaced gets back its pending-confirmation status. That revert goes through WithdrawalRequestService, so it only
/// undoes what the replacement did: an original that settled meanwhile (the cold-wallet case, where approvals take
/// hours) is left settled, and a reverted cancellation loses its "Cancelled by RBF" reason.
/// </summary>
public class PerformWithdrawalJobTests
{
    private const int OriginalId = 10;
    private const int ReplacementId = 11;

    private readonly Mock<IWalletWithdrawalRequestRepository> _requests = new();
    private readonly Mock<IBitcoinService> _bitcoin = new();

    public PerformWithdrawalJobTests()
    {
        _requests.Setup(x => x.Update(It.IsAny<WalletWithdrawalRequest>())).Returns((true, (string?)null));
        _bitcoin.Setup(x => x.PerformWithdrawal(It.IsAny<WalletWithdrawalRequest>()))
            .ThrowsAsync(new InvalidOperationException("Failed tx broadcast"));
    }

    private Task ExecuteAsync(int requestId)
    {
        // The real service: what happens to the replaced request is RevertOriginalAsync's behaviour.
        var withdrawalRequestService = new WithdrawalRequestService(NullLogger<WithdrawalRequestService>.Instance,
            _requests.Object, Mock.Of<IFMUTXORepository>(), Mock.Of<ICoinSelectionService>(), Mock.Of<INBXplorerService>(),
            _bitcoin.Object, Mock.Of<ISchedulerFactory>());
        var job = new PerformWithdrawalJob(NullLogger<PerformWithdrawalJob>.Instance, _bitcoin.Object, _requests.Object,
            withdrawalRequestService);

        var context = new Mock<IJobExecutionContext>();
        context.Setup(x => x.JobDetail).Returns(JobBuilder.Create<PerformWithdrawalJob>()
            .UsingJobData("withdrawalRequestId", requestId)
            .Build());
        return job.Execute(context.Object);
    }

    private WalletWithdrawalRequest Stored(int id, WalletWithdrawalRequestStatus status, int? replaces = null,
        bool cancellation = false)
    {
        var request = new WalletWithdrawalRequest
        {
            Id = id,
            Description = "perform withdrawal test",
            Status = status,
            BumpingWalletWithdrawalRequestId = replaces,
            IsRbfCancellation = cancellation,
        };
        _requests.Setup(x => x.GetById(id)).ReturnsAsync(request);
        return request;
    }

    [Fact]
    public async Task Execute_FailedBump_RevertsTheBumpedOriginalAndFailsTheBump()
    {
        var original = Stored(OriginalId, WalletWithdrawalRequestStatus.Bumped);
        var bump = Stored(ReplacementId, WalletWithdrawalRequestStatus.FinalizingPSBT, replaces: OriginalId);

        await ExecuteAsync(ReplacementId);

        bump.Status.Should().Be(WalletWithdrawalRequestStatus.Failed);
        original.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending,
            "the bump never went out, so the original transaction is still the one waiting to confirm");
    }

    [Fact]
    public async Task Execute_FailedCancellation_RestoresTheOriginalAndClearsTheCancellationReason()
    {
        var original = Stored(OriginalId, WalletWithdrawalRequestStatus.Cancelled);
        original.RejectCancelDescription = $"Cancelled by RBF replacement request {ReplacementId}";
        var cancel = Stored(ReplacementId, WalletWithdrawalRequestStatus.FinalizingPSBT, replaces: OriginalId, cancellation: true);

        await ExecuteAsync(ReplacementId);

        cancel.Status.Should().Be(WalletWithdrawalRequestStatus.Failed);
        original.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending);
        original.RejectCancelDescription.Should().BeNull("the cancellation never went out");
    }

    [Fact]
    public async Task Execute_FailedReplacementOfAnOriginalThatAlreadySettled_LeavesItSettled()
    {
        var original = Stored(OriginalId, WalletWithdrawalRequestStatus.OnChainConfirmed);
        var bump = Stored(ReplacementId, WalletWithdrawalRequestStatus.FinalizingPSBT, replaces: OriginalId);

        await ExecuteAsync(ReplacementId);

        bump.Status.Should().Be(WalletWithdrawalRequestStatus.Failed);
        original.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed,
            "it was mined while the replacement waited for its signatures; that is why the broadcast failed");
        _requests.Verify(x => x.Update(original), Times.Never);
    }

    [Fact]
    public async Task Execute_FailedWithdrawalThatReplacesNothing_IsMarkedFailed()
    {
        var request = Stored(OriginalId, WalletWithdrawalRequestStatus.FinalizingPSBT);

        await ExecuteAsync(OriginalId);

        request.Status.Should().Be(WalletWithdrawalRequestStatus.Failed);
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Once);
    }

    [Fact]
    public async Task Execute_RequestGoneByTheTimeItFails_DoesNotThrow()
    {
        var act = () => ExecuteAsync(OriginalId);

        await act.Should().NotThrowAsync();
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    [Fact]
    public async Task Execute_SuccessfulBroadcast_LeavesTheReplacedRequestAsItWas()
    {
        _bitcoin.Setup(x => x.PerformWithdrawal(It.IsAny<WalletWithdrawalRequest>())).Returns(Task.CompletedTask);
        var original = Stored(OriginalId, WalletWithdrawalRequestStatus.Bumped);
        Stored(ReplacementId, WalletWithdrawalRequestStatus.FinalizingPSBT, replaces: OriginalId);

        await ExecuteAsync(ReplacementId);

        original.Status.Should().Be(WalletWithdrawalRequestStatus.Bumped);
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }
}
