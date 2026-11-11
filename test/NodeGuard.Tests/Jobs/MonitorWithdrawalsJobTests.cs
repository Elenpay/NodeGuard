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
using NodeGuard.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quartz;

namespace NodeGuard.Jobs;

public class MonitorWithdrawalsJobTests
{
    private static readonly long ConfirmedDepth = Math.Max(6, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS);

    private readonly IWalletWithdrawalRequestRepository _repository = Substitute.For<IWalletWithdrawalRequestRepository>();
    private readonly INBXplorerService _nbXplorerService = Substitute.For<INBXplorerService>();
    private readonly MonitorWithdrawalsJob _job;

    public MonitorWithdrawalsJobTests()
    {
        _repository.Update(Arg.Any<WalletWithdrawalRequest>()).Returns((true, null));
        _job = new MonitorWithdrawalsJob(NullLogger<MonitorWithdrawalsJob>.Instance, _repository, _nbXplorerService);
    }

    private static WalletWithdrawalRequest PendingWithdrawal(int id) => new()
    {
        Id = id,
        WalletId = 1,
        Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending,
        TxId = RandomUtils.GetUInt256().ToString()
    };

    [Fact]
    public async Task Execute_UnknownTransaction_StillConfirmsTheOtherWithdrawals()
    {
        var evicted = PendingWithdrawal(1);
        var confirmed = PendingWithdrawal(2);
        _repository.GetOnChainPendingWithdrawals().Returns(new List<WalletWithdrawalRequest> { evicted, confirmed });
        _nbXplorerService.GetTransactionAsync(uint256.Parse(evicted.TxId!), Arg.Any<CancellationToken>())
            .Returns((TransactionResult?)null);
        _nbXplorerService.GetTransactionAsync(uint256.Parse(confirmed.TxId!), Arg.Any<CancellationToken>())
            .Returns(new TransactionResult { Confirmations = ConfirmedDepth });

        await _job.Execute(Substitute.For<IJobExecutionContext>());

        evicted.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmationPending);
        confirmed.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed);
        _repository.Received(1).Update(Arg.Is<WalletWithdrawalRequest>(r => r.Id == confirmed.Id));
        _repository.DidNotReceive().Update(Arg.Is<WalletWithdrawalRequest>(r => r.Id == evicted.Id));
    }

    [Fact]
    public async Task Execute_NBXplorerError_StillConfirmsTheOtherWithdrawals()
    {
        var failing = PendingWithdrawal(1);
        var confirmed = PendingWithdrawal(2);
        _repository.GetOnChainPendingWithdrawals().Returns(new List<WalletWithdrawalRequest> { failing, confirmed });
        _nbXplorerService.GetTransactionAsync(uint256.Parse(failing.TxId!), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("nbxplorer down"));
        _nbXplorerService.GetTransactionAsync(uint256.Parse(confirmed.TxId!), Arg.Any<CancellationToken>())
            .Returns(new TransactionResult { Confirmations = ConfirmedDepth });

        var act = () => _job.Execute(Substitute.For<IJobExecutionContext>());

        await act.Should().NotThrowAsync();
        confirmed.Status.Should().Be(WalletWithdrawalRequestStatus.OnChainConfirmed);
        _repository.Received(1).Update(Arg.Is<WalletWithdrawalRequest>(r => r.Id == confirmed.Id));
    }
}
