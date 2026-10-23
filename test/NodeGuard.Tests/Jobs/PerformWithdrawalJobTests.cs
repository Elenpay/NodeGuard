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
/// When PerformWithdrawalJob cannot sign or broadcast, the request fails and nothing else happens: a request's status
/// only describes its own transaction, so the request an RBF replacement replaced never left pending confirmation and
/// needs no revert. Its inputs are still locked by it, and it is reported as pending again as soon as the replacement
/// is no longer live.
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
        var job = new PerformWithdrawalJob(NullLogger<PerformWithdrawalJob>.Instance, _bitcoin.Object, _requests.Object);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_FailedReplacement_FailsOnlyTheReplacement(bool cancellation)
    {
        var original = Stored(OriginalId, WalletWithdrawalRequestStatus.OnChainConfirmationPending);
        var replacement = Stored(ReplacementId, WalletWithdrawalRequestStatus.FinalizingPSBT, replaces: OriginalId, cancellation);

        await ExecuteAsync(ReplacementId);

        replacement.Status.Should().Be(WalletWithdrawalRequestStatus.Failed);
        original.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending,
            "the replacement never went out, so the original transaction is still the one waiting to confirm");
        _requests.Verify(x => x.Update(original), Times.Never, "a replacement never writes the request it replaces");
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Once);
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
        var original = Stored(OriginalId, WalletWithdrawalRequestStatus.OnChainConfirmationPending);
        Stored(ReplacementId, WalletWithdrawalRequestStatus.FinalizingPSBT, replaces: OriginalId);

        await ExecuteAsync(ReplacementId);

        original.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending,
            "whether it ends up Bumped or settled is for the monitor to decide once a transaction confirms");
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }
}
