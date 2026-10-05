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
using Lnrpc;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.TestHelpers;
using NSpark;
using NSpark.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NodeGuard.Services.Spark;

public class SparkSwapServiceTests
{
    private const string Address = "bcrt1qcg6gkhg76snuvxu6l795yw3fx8dg8088r7zq82u60qauh7tr2kjquxfd2m";
    private const string Hash = "0101010101010101010101010101010101010101010101010101010101010101";
    private const string Invoice = "lnbcrt5m1invoice";
    private const string Identity = SparkSettingsTests.Identity;

    private static readonly SparkSettings Settings = new()
    {
        Enabled = true,
        Network = SparkNetwork.Regtest,
        MaxExitFeeSats = 20_000
    };

    private readonly ISparkWalletService _spark = Substitute.For<ISparkWalletService>();
    private readonly ILightningService _lightning = Substitute.For<ILightningService>();
    private readonly ISwapOutRepository _swapOuts = Substitute.For<ISwapOutRepository>();
    private readonly IWalletRepository _wallets = Substitute.For<IWalletRepository>();
    private readonly INBXplorerService _nbXplorer = Substitute.For<INBXplorerService>();
    private readonly ManualTime _time = new();
    private readonly Node _node = new() { Id = 1, Name = "alice", PubKey = "02aa", Endpoint = "alice:10009" };
    private readonly Wallet _wallet;

    public SparkSwapServiceTests()
    {
        _spark.EnsureReadyAsync(Arg.Any<CancellationToken>()).Returns(new SparkWalletStatus(SparkWalletState.Ready, Identity));
        _swapOuts.GetAllPending().Returns(new List<SwapOut>());
        _swapOuts.AddAsync(Arg.Any<SwapOut>()).Returns((true, null));
        _swapOuts.Update(Arg.Any<SwapOut>()).Returns((true, null));

        _wallet = CreateWallet.SingleSig(CreateWallet.CreateInternalWallet());
        _wallet.Id = 3;
        _wallets.GetById(3).Returns(_wallet);
        Payouts();
    }

    private SparkSwapService Service() => new(NullLogger<SparkSwapService>.Instance, Settings, _spark, _lightning, _swapOuts,
        _wallets, _nbXplorer, _time);

    private static SwapOutRequest Request(string address = Address) => new() { Amount = 500_000, Address = address, MaxRoutingFeesPercent = 1m };

    private static SwapOut Template() => new() { NodeId = 1, DestinationWalletId = 3, IsManual = true, Provider = SwapProvider.Spark };

    private void InvoiceIssued() =>
        _spark.CreateInvoiceAsync(500_000, Arg.Any<string>(), SparkSwapService.InvoiceExpirySeconds, Arg.Any<CancellationToken>())
            .Returns(new LightningInvoice(Invoice, Hash, 500_000, DateTimeOffset.UnixEpoch.AddMinutes(30), "req-1"));

    private void NodePays(Payment.Types.PaymentStatus status, PaymentFailureReason reason = PaymentFailureReason.FailureReasonNone,
        long feeSat = 0) =>
        _lightning.SendPaymentV2Async(_node, Invoice, Arg.Is(0L), Arg.Any<long>(), Arg.Any<ulong[]?>(), null, Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new Payment { Status = status, FailureReason = reason, FeeSat = feeSat });

    private void NodeReports(Payment? payment) =>
        _lightning.TrackPaymentV2Async(_node, Arg.Is<byte[]>(h => Convert.ToHexString(h).ToLowerInvariant() == Hash),
            Arg.Any<CancellationToken>()).Returns(payment);

    private void Balance(long available, long incoming = 0) =>
        _spark.GetBalanceAsync(Arg.Any<CancellationToken>()).Returns(
            new WalletBalance(new SatsBalance(available, available + incoming, incoming), [], []));

    /// <summary>Transactions of the destination wallet: (txid seed, value paid to <see cref="Address"/>, confirmations).</summary>
    private void Payouts(params (byte Seed, long Sats, long Confirmations)[] payouts)
    {
        var script = BitcoinAddress.Create(Address, Network.RegTest).ScriptPubKey;
        TransactionInformation Tx((byte Seed, long Sats, long Confirmations) p) => new()
        {
            TransactionId = new uint256(Enumerable.Repeat(p.Seed, 32).ToArray()),
            Confirmations = p.Confirmations,
            Outputs = [new MatchedOutput { ScriptPubKey = script, Value = Money.Satoshis(p.Sats), Index = 0 }]
        };

        _nbXplorer.GetTransactionsAsync(Arg.Any<NBXplorer.DerivationStrategy.DerivationStrategyBase>()).Returns(new GetTransactionsResponse
        {
            ConfirmedTransactions = new TransactionInformationSet { Transactions = payouts.Where(p => p.Confirmations > 0).Select(Tx).ToList() },
            UnconfirmedTransactions = new TransactionInformationSet { Transactions = payouts.Where(p => p.Confirmations == 0).Select(Tx).ToList() }
        });
    }

    private static SwapOut Pending(long? lightningFee = 12, string? txId = null, string? identity = Identity) => new()
    {
        Id = 7,
        Provider = SwapProvider.Spark,
        ProviderId = "req-1",
        PaymentHash = Hash,
        DestinationAddress = Address,
        DestinationWalletId = 3,
        SparkIdentity = identity,
        SatsAmount = 500_000,
        LightningFeeSats = lightningFee,
        TxId = txId,
        Status = SwapOutStatus.Pending,
        CreationDatetime = DateTimeOffset.UnixEpoch
    };

    // ── Creation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_RecordsTheSwapBeforePayingTheInvoice()
    {
        InvoiceIssued();
        NodePays(Payment.Types.PaymentStatus.Succeeded, feeSat: 9);
        var swapOut = Template();
        SwapOut? recorded = null;
        _swapOuts.AddAsync(Arg.Do<SwapOut>(s => recorded = new SwapOut
        {
            ProviderId = s.ProviderId, PaymentHash = s.PaymentHash, Status = s.Status, LightningFeeSats = s.LightningFeeSats
        })).Returns((true, null));

        var (response, saved, _) = await Service().CreateSwapOutAsync(_node, swapOut, Request());

        saved.Should().BeTrue();
        recorded.Should().BeEquivalentTo(new { ProviderId = "req-1", PaymentHash = Hash, Status = SwapOutStatus.Pending, LightningFeeSats = (long?)null });
        Received.InOrder(() =>
        {
            _swapOuts.AddAsync(swapOut);
            _lightning.SendPaymentV2Async(_node, Invoice, 0, 5_000_000, null, null, Arg.Any<int>(), CancellationToken.None);
            _swapOuts.Update(swapOut);
        });
        swapOut.ProviderId.Should().Be("req-1");
        swapOut.PaymentHash.Should().Be(Hash);
        swapOut.DestinationAddress.Should().Be(Address);
        swapOut.SparkIdentity.Should().Be(Identity);
        swapOut.SatsAmount.Should().Be(500_000);
        swapOut.Status.Should().Be(SwapOutStatus.Pending);
        swapOut.LightningFeeSats.Should().Be(9);
        response.Status.Should().Be(SwapOutStatus.Pending);
        response.OffchainFee.Should().Be(9);
    }

    [Fact]
    public async Task Create_PaysEvenIfTheCallerCancels()
    {
        InvoiceIssued();
        NodePays(Payment.Types.PaymentStatus.Succeeded);
        using var cancellation = new CancellationTokenSource();

        await Service().CreateSwapOutAsync(_node, Template(), Request(), cancellation.Token);

        await _lightning.Received(1).SendPaymentV2Async(_node, Invoice, Arg.Is(0L), Arg.Any<long>(), Arg.Any<ulong[]?>(), null,
            Arg.Any<int>(), CancellationToken.None);
    }

    [Fact]
    public async Task Create_WhenThePaymentFails_FailsTheSwap()
    {
        InvoiceIssued();
        NodePays(Payment.Types.PaymentStatus.Failed, PaymentFailureReason.FailureReasonNoRoute);
        var swapOut = Template();

        var (response, _, _) = await Service().CreateSwapOutAsync(_node, swapOut, Request());

        response.Status.Should().Be(SwapOutStatus.Failed);
        swapOut.Status.Should().Be(SwapOutStatus.Failed);
        swapOut.ErrorDetails.Should().Contain("FailureReasonNoRoute");
        _swapOuts.Received(1).Update(swapOut);
    }

    [Fact]
    public async Task Create_WhenThePaymentOutcomeIsUnknown_LeavesItToTheMonitor()
    {
        InvoiceIssued();
        // SendPaymentV2 reports a broken stream as FailureReasonError: the payment may still succeed
        NodePays(Payment.Types.PaymentStatus.Failed, PaymentFailureReason.FailureReasonError);
        var swapOut = Template();

        var (response, _, _) = await Service().CreateSwapOutAsync(_node, swapOut, Request());

        response.Status.Should().Be(SwapOutStatus.Pending);
        swapOut.LightningFeeSats.Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenTheSwapCannotBeRecorded_DoesNotPay()
    {
        InvoiceIssued();
        _swapOuts.AddAsync(Arg.Any<SwapOut>()).Returns((false, "duplicate key value violates unique constraint"));

        var (_, saved, error) = await Service().CreateSwapOutAsync(_node, Template(), Request());

        saved.Should().BeFalse();
        error.Should().Contain("unique constraint");
        await _lightning.DidNotReceiveWithAnyArgs().SendPaymentV2Async(default!, default!, default, default, default, default, default, default);
    }

    [Fact]
    public async Task Create_WhileAnotherSparkSwapIsInFlight_IsRefused()
    {
        _swapOuts.GetAllPending().Returns(new List<SwapOut> { Pending() });

        var act = () => Service().CreateSwapOutAsync(_node, Template(), Request());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*still in flight*");
        await _spark.DidNotReceiveWithAnyArgs().CreateInvoiceAsync(default, default!, default, default);
    }

    [Fact]
    public async Task Create_WhenSparkIsUnavailable_IsRefused()
    {
        _spark.EnsureReadyAsync(Arg.Any<CancellationToken>())
            .Returns(new SparkWalletStatus(SparkWalletState.Unavailable, Reason: "the signer is not the expected one"));

        var act = () => Service().CreateSwapOutAsync(_node, Template(), Request());

        await act.Should().ThrowAsync<SparkUnavailableException>().WithMessage("the signer is not the expected one");
        await _swapOuts.DidNotReceiveWithAnyArgs().AddAsync(default!);
    }

    [Fact]
    public async Task Create_ToAnAddressOfAnotherNetwork_IsRefused()
    {
        var act = () => Service().CreateSwapOutAsync(_node, Template(), Request("bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq"));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*RegTest address*");
    }

    // ── Monitoring ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Advance_OnceThePaymentSettled_ClaimsAndExitsEverythingToTheDestination()
    {
        var swap = Pending(lightningFee: null);
        NodeReports(new Payment { Status = Payment.Types.PaymentStatus.Succeeded, FeeSat = 11 });
        Balance(available: 500_000);
        _spark.WithdrawAllAsync(Address, 20_000, Arg.Any<CancellationToken>())
            .Returns(new WithdrawAllResult("exit-tx", 500_000, 497_000, 0, 0, 0, 0));

        var response = await Service().AdvanceSwapAsync(_node, swap);

        response.Status.Should().Be(SwapOutStatus.Pending);
        response.TxId.Should().Be("exit-tx");
        response.OffchainFee.Should().Be(11);
        response.ServerFee.Should().Be(3_000);
        swap.LightningFeeSats.Should().Be(11);
        swap.TxId.Should().Be("exit-tx");
        await _spark.Received(1).ClaimPendingAsync(Arg.Any<CancellationToken>());
        _swapOuts.Received(2).Update(swap);
    }

    [Fact]
    public async Task Advance_WhenThePaymentFailed_FailsTheSwap()
    {
        NodeReports(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonTimeout });

        var response = await Service().AdvanceSwapAsync(_node, Pending(lightningFee: null));

        response.Status.Should().Be(SwapOutStatus.Failed);
        response.ErrorMessage.Should().Contain("FailureReasonTimeout");
        await _spark.DidNotReceiveWithAnyArgs().WithdrawAllAsync(default!, default, default);
    }

    [Fact]
    public async Task Advance_AnUnknownPayment_WaitsUntilTheInvoiceExpired_ThenFails()
    {
        NodeReports(null);
        _spark.GetReceiveStatusAsync("req-1", Arg.Any<CancellationToken>()).ThrowsAsync(new NotSupportedException("GetUserRequest"));

        _time.Set(DateTimeOffset.UnixEpoch.AddMinutes(30));
        (await Service().AdvanceSwapAsync(_node, Pending(lightningFee: null))).Status.Should().Be(SwapOutStatus.Pending);

        _time.Set(DateTimeOffset.UnixEpoch.AddMinutes(41));
        var response = await Service().AdvanceSwapAsync(_node, Pending(lightningFee: null));

        response.Status.Should().Be(SwapOutStatus.Failed);
        response.ErrorMessage.Should().Contain("never reached the node").And.Contain("unknown");
    }

    [Fact]
    public async Task Advance_WithNothingClaimableYet_StaysPending()
    {
        Balance(available: 0, incoming: 500_000);

        var response = await Service().AdvanceSwapAsync(_node, Pending());

        response.Status.Should().Be(SwapOutStatus.Pending);
        await _spark.DidNotReceiveWithAnyArgs().WithdrawAllAsync(default!, default, default);
    }

    [Fact]
    public async Task Advance_AConfirmedPayout_CompletesTheSwap_EvenIfTheExitWasFeeBumped()
    {
        Payouts((Seed: 0xbb, Sats: 497_500, Confirmations: 1));

        var response = await Service().AdvanceSwapAsync(_node, Pending(txId: "exit-tx"));

        response.Status.Should().Be(SwapOutStatus.Completed);
        response.TxId.Should().Be(new uint256(Enumerable.Repeat((byte)0xbb, 32).ToArray()).ToString());
        response.ServerFee.Should().Be(2_500);
        response.OffchainFee.Should().Be(12);
        response.OnchainFee.Should().Be(0);
    }

    [Fact]
    public async Task Advance_AnUnconfirmedPayout_StaysPending()
    {
        Payouts((Seed: 0xbb, Sats: 497_500, Confirmations: 0));

        var response = await Service().AdvanceSwapAsync(_node, Pending(txId: "exit-tx"));

        response.Status.Should().Be(SwapOutStatus.Pending);
        response.TxId.Should().Be(new uint256(Enumerable.Repeat((byte)0xbb, 32).ToArray()).ToString());
    }

    [Fact]
    public async Task Advance_APayoutWhoseExitWasNotSaved_IsNotExitedAgain()
    {
        // A crash after the exit, before its txid was saved
        Payouts((Seed: 0xcc, Sats: 497_000, Confirmations: 0));
        Balance(available: 0);

        var response = await Service().AdvanceSwapAsync(_node, Pending(txId: null));

        response.Status.Should().Be(SwapOutStatus.Pending);
        await _spark.DidNotReceiveWithAnyArgs().WithdrawAllAsync(default!, default, default);
    }

    [Fact]
    public async Task Advance_ASwapPaidIntoAnotherSparkIdentity_IsRefused()
    {
        var act = () => Service().AdvanceSwapAsync(_node, Pending(identity: SparkSettingsTests.SspIdentity));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*resolved by hand*");
        await _spark.DidNotReceiveWithAnyArgs().ClaimPendingAsync(default);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Set(DateTimeOffset now) => _now = now;
    }
}
