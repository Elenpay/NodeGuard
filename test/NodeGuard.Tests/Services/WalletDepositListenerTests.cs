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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using Nodeguard;
using NSubstitute;
using Key = NodeGuard.Data.Models.Key;
using Wallet = NodeGuard.Data.Models.Wallet;

namespace NodeGuard.Services;

public class WalletDepositListenerTests
{
    private const int WalletId = 21;
    private const int ConfirmationsRequired = 3;

    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddYears(55);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Network _network = CurrentNetworkHelper.GetCurrentNetwork();
    private readonly INBXplorerService _nbXplorerService = Substitute.For<INBXplorerService>();
    private readonly IWalletEventHub _hub = Substitute.For<IWalletEventHub>();
    private readonly MutableTime _time = new();
    private readonly List<WalletEvent> _published = [];
    private readonly DerivationStrategyBase _walletScheme;
    private readonly WalletDepositListener _listener;

    public WalletDepositListenerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase("WalletDepositListenerTests" + Guid.NewGuid())
            .Options;
        var wallet = new Wallet
        {
            Id = WalletId,
            Name = "hot",
            IsHotWallet = true,
            MofN = 1,
            Keys = [new Key { Name = "hot key", XPUB = new ExtKey().Neuter().GetWif(_network).ToString() }]
        };
        _walletScheme = wallet.GetDerivationStrategy()!;
        using (var context = new ApplicationDbContext(options))
        {
            context.Wallets.Add(wallet);
            context.SaveChanges();
        }

        var factory = Substitute.For<IDbContextFactory<ApplicationDbContext>>();
        factory.CreateDbContextAsync(Arg.Any<CancellationToken>()).Returns(_ => new ApplicationDbContext(options));
        _hub.When(x => x.Publish(Arg.Any<WalletEvent>())).Do(c => _published.Add(c.Arg<WalletEvent>()));

        _listener = new WalletDepositListener(_nbXplorerService, factory, _hub, _time,
            NullLogger<WalletDepositListener>.Instance, ConfirmationsRequired);
    }

    private NewTransactionEvent Transaction(uint256 txId, bool spendsWalletCoins = false, uint256? blockId = null,
        long confirmations = 0, DerivationStrategyBase? scheme = null, params uint256[] replacing)
    {
        var address = new NBitcoin.Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, _network);
        var e = new NewTransactionEvent
        {
            DerivationStrategy = scheme ?? _walletScheme,
            BlockId = blockId,
            TransactionData = new TransactionResult { TransactionHash = txId, Confirmations = confirmations },
            Outputs =
            [
                new MatchedOutput { Index = 1, Value = Money.Satoshis(150_000), Address = address, ScriptPubKey = address.ScriptPubKey }
            ],
            Replacing = replacing.ToList()
        };
        if (spendsWalletCoins)
            e.Inputs.Add(new MatchedInput { Index = 0, Value = Money.Satoshis(500_000) });
        return e;
    }

    private Task Handle(NewEventBase e) => _listener.HandleEventAsync(e, CancellationToken.None);

    [Fact]
    public async Task IncomingTransaction_IsDetectedOnce()
    {
        var txId = RandomUtils.GetUInt256();

        await Handle(Transaction(txId));
        await Handle(Transaction(txId, blockId: RandomUtils.GetUInt256(), confirmations: 1)); // mined re-notification

        var detected = _published.Should().ContainSingle().Subject;
        detected.Type.Should().Be(WALLET_EVENT_TYPE.WalletEventDepositDetected);
        detected.WalletId.Should().Be(WalletId);
        detected.Deposit.TxId.Should().Be(txId.ToString());
        detected.Deposit.AmountSats.Should().Be(150_000);
        detected.Deposit.Confirmations.Should().Be(0);
        detected.Deposit.Outputs.Should().ContainSingle(o => o.Vout == 1 && o.AmountSats == 150_000 && o.Address != "");
    }

    [Fact]
    public async Task TransactionSpendingTheWalletsCoins_IsNotADeposit()
    {
        await Handle(Transaction(RandomUtils.GetUInt256(), spendsWalletCoins: true));

        _published.Should().BeEmpty();
        _listener.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task UnknownSchemeOrAddressTrackedSource_IsIgnored()
    {
        var otherScheme = new DerivationStrategyFactory(_network).Parse(new ExtKey().Neuter().GetWif(_network).ToString());

        await Handle(Transaction(RandomUtils.GetUInt256(), scheme: otherScheme));
        var addressEvent = Transaction(RandomUtils.GetUInt256());
        addressEvent.DerivationStrategy = null;
        await Handle(addressEvent);

        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task NewBlocks_ConfirmTheDepositAtTheThreshold()
    {
        var txId = RandomUtils.GetUInt256();
        await Handle(Transaction(txId));

        _nbXplorerService.GetTransactionAsync(txId, Arg.Any<CancellationToken>())
            .Returns(new TransactionResult { TransactionHash = txId, Confirmations = ConfirmationsRequired - 1 });
        await Handle(new NewBlockEvent());
        _published.Should().ContainSingle("one block short of the threshold");

        _nbXplorerService.GetTransactionAsync(txId, Arg.Any<CancellationToken>())
            .Returns(new TransactionResult { TransactionHash = txId, Confirmations = ConfirmationsRequired });
        await Handle(new NewBlockEvent());
        await Handle(new NewBlockEvent());

        _published.Select(e => e.Type).Should().Equal(
            WALLET_EVENT_TYPE.WalletEventDepositDetected, WALLET_EVENT_TYPE.WalletEventDepositConfirmed);
        _published[1].Deposit.Confirmations.Should().Be(ConfirmationsRequired);
        _published[1].Deposit.AmountSats.Should().Be(150_000);
        _listener.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task DepositFirstSeenDeepEnough_IsDetectedAndConfirmedAtOnce()
    {
        await Handle(Transaction(RandomUtils.GetUInt256(), blockId: RandomUtils.GetUInt256(), confirmations: ConfirmationsRequired));

        _published.Select(e => e.Type).Should().Equal(
            WALLET_EVENT_TYPE.WalletEventDepositDetected, WALLET_EVENT_TYPE.WalletEventDepositConfirmed);
        _listener.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task ReplacingTransaction_ReplacesThePendingDeposit()
    {
        var original = RandomUtils.GetUInt256();
        var replacement = RandomUtils.GetUInt256();
        await Handle(Transaction(original));

        await Handle(Transaction(replacement, replacing: original));

        _published.Select(e => e.Type).Should().Equal(
            WALLET_EVENT_TYPE.WalletEventDepositDetected,
            WALLET_EVENT_TYPE.WalletEventDepositReplaced,
            WALLET_EVENT_TYPE.WalletEventDepositDetected);
        _published[1].Deposit.TxId.Should().Be(original.ToString());
        _published[1].Deposit.ReplacedByTxId.Should().Be(replacement.ToString());
        _published[2].Deposit.TxId.Should().Be(replacement.ToString());
    }

    [Fact]
    public async Task DepositReplacedSeenOnANewBlock_IsReplaced()
    {
        var txId = RandomUtils.GetUInt256();
        var replacedBy = RandomUtils.GetUInt256();
        await Handle(Transaction(txId));
        _nbXplorerService.GetTransactionAsync(txId, Arg.Any<CancellationToken>())
            .Returns(new TransactionResult { TransactionHash = txId, ReplacedBy = replacedBy });

        await Handle(new NewBlockEvent());

        _published.Last().Type.Should().Be(WALLET_EVENT_TYPE.WalletEventDepositReplaced);
        _published.Last().Deposit.ReplacedByTxId.Should().Be(replacedBy.ToString());
        _listener.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task DepositThatNeverConfirms_IsDroppedAfterItsLifetime()
    {
        var txId = RandomUtils.GetUInt256();
        await Handle(Transaction(txId));
        _nbXplorerService.GetTransactionAsync(txId, Arg.Any<CancellationToken>())
            .Returns(new TransactionResult { TransactionHash = txId, Confirmations = 0 });

        _time.Now += WalletDepositListener.PendingDepositLifetime + TimeSpan.FromMinutes(1);
        await Handle(new NewBlockEvent());

        _listener.PendingCount.Should().Be(0);
        _published.Should().ContainSingle("dropping a stale deposit publishes nothing");
    }
}
