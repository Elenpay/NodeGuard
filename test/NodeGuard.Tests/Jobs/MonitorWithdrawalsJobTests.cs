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
using NBitcoin;
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// MonitorWithdrawalsJob settles withdrawals whose transaction reached the settlement depth, and asks
/// WithdrawalRequestService whether an RBF replacement lost the race to a transaction it replaced. That question only
/// makes sense while the replacement itself is unconfirmed: once it is mined, nothing it replaced can be.
/// </summary>
public class MonitorWithdrawalsJobTests
{
    private readonly Mock<IWalletWithdrawalRequestRepository> _requests = new();
    private readonly Mock<INBXplorerService> _nbXplorer = new();
    private readonly Mock<IWithdrawalRequestService> _withdrawalRequestService = new();

    public MonitorWithdrawalsJobTests()
    {
        _requests.Setup(x => x.Update(It.IsAny<WalletWithdrawalRequest>())).Returns((true, (string?)null));
    }

    private Task ExecuteAsync(params WalletWithdrawalRequest[] pending)
    {
        _requests.Setup(x => x.GetOnChainPendingWithdrawals()).ReturnsAsync(pending.ToList());

        var job = new MonitorWithdrawalsJob(NullLogger<MonitorWithdrawalsJob>.Instance, _requests.Object,
            _nbXplorer.Object, _withdrawalRequestService.Object);
        return job.Execute(new Mock<IJobExecutionContext>().Object);
    }

    /// <summary>A broadcast request waiting for confirmation whose transaction has <paramref name="confirmations"/>.</summary>
    private WalletWithdrawalRequest Pending(int id, int confirmations, int? replaces = null)
    {
        var txId = new uint256((ulong)id);
        _nbXplorer.Setup(x => x.GetTransactionAsync(txId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionResult { Confirmations = confirmations });

        return new WalletWithdrawalRequest
        {
            Id = id,
            Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending,
            TxId = txId.ToString(),
            BumpingWalletWithdrawalRequestId = replaces,
        };
    }

    [Fact]
    public async Task Execute_WithdrawalAtTheSettlementDepth_IsSettled()
    {
        var request = Pending(1, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS);

        await ExecuteAsync(request);

        request.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed);
        _requests.Verify(x => x.Update(request), Times.Once);
        _withdrawalRequestService.Verify(x => x.SettleMinedOriginalAsync(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    [Fact]
    public async Task Execute_ReplacementAtTheSettlementDepth_IsSettledWithoutLookingAtWhatItReplaced()
    {
        var replacement = Pending(2, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS, replaces: 1);

        await ExecuteAsync(replacement);

        replacement.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed);
        _withdrawalRequestService.Verify(x => x.SettleMinedOriginalAsync(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    [Fact]
    public async Task Execute_UnconfirmedReplacement_ChecksWhetherWhatItReplacedWasMined()
    {
        var replacement = Pending(2, confirmations: 0, replaces: 1);

        await ExecuteAsync(replacement);

        _withdrawalRequestService.Verify(x => x.SettleMinedOriginalAsync(replacement), Times.Once);
        replacement.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending,
            "settling the original and failing the replacement is the service's decision, not the job's");
    }

    [Fact]
    public async Task Execute_ReplacementMinedButNotYetSettled_LeavesWhatItReplacedAlone()
    {
        var replacement = Pending(2, confirmations: 1, replaces: 1);

        await ExecuteAsync(replacement);

        replacement.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending, "it is not deep enough yet");
        _withdrawalRequestService.Verify(x => x.SettleMinedOriginalAsync(It.IsAny<WalletWithdrawalRequest>()), Times.Never,
            "the replacement is mined, so nothing it replaced can be");
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    [Fact]
    public async Task Execute_UnconfirmedWithdrawalThatReplacesNothing_IsLeftPending()
    {
        var request = Pending(1, confirmations: 0);

        await ExecuteAsync(request);

        request.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending);
        _withdrawalRequestService.Verify(x => x.SettleMinedOriginalAsync(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    [Fact]
    public async Task Execute_MixedBatch_HandlesEachRequestOnItsOwn()
    {
        var settling = Pending(1, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS);
        var lostRace = Pending(3, confirmations: 0, replaces: 2);
        var inFlight = Pending(4, confirmations: 0);

        await ExecuteAsync(settling, lostRace, inFlight);

        settling.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed);
        _withdrawalRequestService.Verify(x => x.SettleMinedOriginalAsync(lostRace), Times.Once);
        _withdrawalRequestService.Verify(x => x.SettleMinedOriginalAsync(It.IsAny<WalletWithdrawalRequest>()), Times.Once);
        inFlight.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending);
    }
}
