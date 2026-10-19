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
/// MonitorWithdrawalsJob settles withdrawals whose transaction reached the settlement depth, one row at a time so a
/// bad row never stops the batch, and then hands over to WithdrawalRequestService.ReconcileReplacementChainsAsync,
/// which gives RBF-linked requests their final status from the database. The order matters: a request settled in this
/// run must drive its chain in this run.
/// </summary>
public class MonitorWithdrawalsJobTests
{
    private readonly Mock<IWalletWithdrawalRequestRepository> _requests = new();
    private readonly Mock<INBXplorerService> _nbXplorer = new();
    private readonly Mock<IWithdrawalRequestService> _withdrawalRequestService = new();
    private readonly List<string> _events = new();

    public MonitorWithdrawalsJobTests()
    {
        _requests.Setup(x => x.Update(It.IsAny<WalletWithdrawalRequest>()))
            .Callback<WalletWithdrawalRequest>(r => _events.Add($"settle:{r.Id}"))
            .Returns((true, (string?)null));
        _withdrawalRequestService.Setup(x => x.ReconcileReplacementChainsAsync())
            .Callback(() => _events.Add("reconcile"))
            .Returns(Task.CompletedTask);
    }

    private Task ExecuteAsync(params WalletWithdrawalRequest[] pending)
    {
        _requests.Setup(x => x.GetOnChainPendingWithdrawals()).ReturnsAsync(pending.ToList());

        var job = new MonitorWithdrawalsJob(NullLogger<MonitorWithdrawalsJob>.Instance, _requests.Object,
            _nbXplorer.Object, _withdrawalRequestService.Object);
        return job.Execute(new Mock<IJobExecutionContext>().Object);
    }

    /// <summary>A broadcast request waiting for confirmation; NBXplorer answers <paramref name="transaction"/> for it.</summary>
    private WalletWithdrawalRequest Pending(int id, Func<TransactionResult?> transaction)
    {
        var request = new WalletWithdrawalRequest
        {
            Id = id,
            Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending,
            TxId = new uint256((ulong)id).ToString(),
        };
        _nbXplorer.Setup(x => x.GetTransactionAsync(uint256.Parse(request.TxId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(transaction);
        return request;
    }

    private WalletWithdrawalRequest Pending(int id, int confirmations)
        => Pending(id, () => new TransactionResult { Confirmations = confirmations });

    [Fact]
    public async Task Execute_WithdrawalAtTheSettlementDepth_IsSettled()
    {
        var request = Pending(1, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS);

        await ExecuteAsync(request);

        request.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed);
        _requests.Verify(x => x.Update(request), Times.Once);
    }

    [Fact]
    public async Task Execute_ReplacementAtTheSettlementDepth_IsSettled()
    {
        var replacement = Pending(2, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS);
        replacement.BumpingWalletWithdrawalRequestId = 1;

        await ExecuteAsync(replacement);

        replacement.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed,
            "what that means for the request it replaced is the reconciliation's job");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Execute_WithdrawalBelowTheSettlementDepth_IsLeftPending(int confirmations)
    {
        var request = Pending(1, confirmations);

        await ExecuteAsync(request);

        request.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending);
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    [Fact]
    public async Task Execute_TransactionUnknownToNbxplorer_LeavesTheRequestPendingAndContinues()
    {
        var unknown = Pending(1, () => null);
        var settling = Pending(2, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS);

        await ExecuteAsync(unknown, settling);

        unknown.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending);
        settling.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed);
    }

    [Fact]
    public async Task Execute_NbxplorerFailureOnOneRequest_DoesNotAbortTheBatch()
    {
        var failing = Pending(1, () => throw new HttpRequestException("nbxplorer down"));
        var settling = Pending(2, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS);

        var act = () => ExecuteAsync(failing, settling);

        await act.Should().NotThrowAsync();
        settling.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed);
        _events.Should().Contain("reconcile");
    }

    [Fact]
    public async Task Execute_RequestWithoutTransaction_IsSkipped()
    {
        var request = Pending(1, confirmations: 0);
        request.TxId = null;

        await ExecuteAsync(request);

        _nbXplorer.Verify(x => x.GetTransactionAsync(It.IsAny<uint256>(), It.IsAny<CancellationToken>()), Times.Never);
        _requests.Verify(x => x.Update(It.IsAny<WalletWithdrawalRequest>()), Times.Never);
    }

    [Fact]
    public async Task Execute_ReconcilesTheRbfChainsAfterSettling()
    {
        var settling = Pending(1, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS);
        var inFlight = Pending(2, confirmations: 0);
        inFlight.BumpingWalletWithdrawalRequestId = 1;

        await ExecuteAsync(settling, inFlight);

        _events.Should().Equal(new[] { "settle:1", "reconcile" },
            "a request settled in this run must drive its RBF chain in this run");
        inFlight.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending,
            "failing it is the reconciliation's decision, not the job's");
    }

    [Fact]
    public async Task Execute_NothingPending_StillReconciles()
    {
        await ExecuteAsync();

        _withdrawalRequestService.Verify(x => x.ReconcileReplacementChainsAsync(), Times.Once,
            "a cold replacement awaiting approval is not pending confirmation, yet its original may have confirmed");
    }

    [Fact]
    public async Task Execute_ReconciliationThrowing_IsLoggedNotRethrown()
    {
        _withdrawalRequestService.Setup(x => x.ReconcileReplacementChainsAsync())
            .ThrowsAsync(new InvalidOperationException("db down"));

        var act = () => ExecuteAsync();

        await act.Should().NotThrowAsync();
    }
}
