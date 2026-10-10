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
using NSpark.Exceptions;
using NSpark.Models;
using NSpark.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NodeGuard.Services.Spark;

public class SparkSwapServiceTests
{
    private const string Address = "bcrt1qcg6gkhg76snuvxu6l795yw3fx8dg8088r7zq82u60qauh7tr2kjquxfd2m";
    private const string Hash = "0101010101010101010101010101010101010101010101010101010101010101";
    private const string Invoice = "lnbcrt5m1invoice";
    private const string Identity = SparkSettingsTests.Identity;
    private const string Ssp = SparkSettingsTests.SspIdentity;

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
        _spark.ClaimPendingAsync(Arg.Any<CancellationToken>()).Returns(new PendingTransferClaim([], []));
        _spark.GetTransfersAsync(Arg.Any<TransferDirection>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<SparkTransfer>());
        _swapOuts.GetAllPending().Returns(new List<SwapOut>());
        _swapOuts.GetSparkTransferIdsAsync().Returns(new HashSet<string>());
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

    private static SparkLeaf Leaf(string id, long sats) => new(id, "tree", sats, "AVAILABLE");

    private static SparkTransfer Transfer(string id, long sats, string type = "PreimageSwap", params SparkLeaf[] leaves) =>
        new(id, Ssp, Identity, sats, "Completed", DateTimeOffset.UnixEpoch.AddMinutes(1), type)
        {
            Leaves = leaves.Length > 0 ? leaves : [Leaf($"{id}-leaf", sats)]
        };

    private void SspReports(string? transferId) =>
        _spark.GetReceiveRequestAsync("req-1", Arg.Any<CancellationToken>())
            .Returns(new LightningReceiveRequest("req-1", "TRANSFER_COMPLETED", transferId, null));

    private void ReceivedTransfers(params SparkTransfer[] transfers) =>
        _spark.GetTransfersAsync(TransferDirection.Received, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(transfers.ToList());

    private static SwapOut Pending(long? lightningFee = 12, string? txId = null, string? identity = Identity, int id = 7,
        string? leafIds = null) => new()
    {
        Id = id,
        NodeId = 1,
        Provider = SwapProvider.Spark,
        ProviderId = "req-1",
        PaymentHash = Hash,
        DestinationAddress = Address,
        DestinationWalletId = 3,
        SparkIdentity = identity,
        SatsAmount = 500_000,
        LightningFeeSats = lightningFee,
        TxId = txId,
        SparkTransferId = leafIds is null ? null : $"transfer-{id}",
        SparkLeafIds = leafIds,
        SparkReceivedSats = leafIds is null ? null : 499_000,
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
        NSubstitute.Received.InOrder(() =>
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
        _swapOuts.AddAsync(Arg.Any<SwapOut>()).Returns((false, "the database is unavailable"));

        var (_, saved, error) = await Service().CreateSwapOutAsync(_node, Template(), Request());

        saved.Should().BeFalse();
        error.Should().Contain("unavailable");
        await _lightning.DidNotReceiveWithAnyArgs().SendPaymentV2Async(default!, default!, default, default, default, default, default, default);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task Create_AtTheNodesMaxSwapsInFlight_IsRefused(int maxSwapsInFlight, int inFlight)
    {
        _node.MaxSwapsInFlight = maxSwapsInFlight;
        var pending = Enumerable.Range(10, inFlight).Select(id => Pending(id: id)).ToList();
        pending.Add(new SwapOut { Id = 30, NodeId = 2, Provider = SwapProvider.Spark, Status = SwapOutStatus.Pending });
        pending.Add(new SwapOut { Id = 31, NodeId = 1, Provider = SwapProvider.Loop, Status = SwapOutStatus.Pending });
        _swapOuts.GetAllPending().Returns(pending);

        var act = () => Service().CreateSwapOutAsync(_node, Template(), Request());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"alice already has {inFlight} Spark swaps in flight*");
        await _spark.DidNotReceiveWithAnyArgs().CreateInvoiceAsync(default, default!, default, default);
    }

    [Fact]
    public async Task Create_BelowTheNodesMaxSwapsInFlight_IsAllowed()
    {
        _node.MaxSwapsInFlight = 2;
        _swapOuts.GetAllPending().Returns(new List<SwapOut> { Pending() });
        InvoiceIssued();
        NodePays(Payment.Types.PaymentStatus.Succeeded);

        var (_, saved, _) = await Service().CreateSwapOutAsync(_node, Template(), Request());

        saved.Should().BeTrue();
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

    // ── Payment ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Advance_OnceThePaymentSettled_SavesItsFee_AndLeavesTheExitToTheExitJob()
    {
        var swap = Pending(lightningFee: null);
        NodeReports(new Payment { Status = Payment.Types.PaymentStatus.Succeeded, FeeSat = 11 });

        var response = await Service().AdvanceSwapAsync(_node, swap);

        response.Status.Should().Be(SwapOutStatus.Pending);
        response.OffchainFee.Should().Be(11);
        swap.LightningFeeSats.Should().Be(11);
        _swapOuts.Received(1).Update(swap);
        await _spark.DidNotReceiveWithAnyArgs().ClaimPendingAsync(default);
        await _spark.DidNotReceiveWithAnyArgs().WithdrawLeavesAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Advance_APaidSwap_IsLeftToTheExitJob()
    {
        var response = await Service().AdvanceSwapAsync(_node, Pending());

        response.Status.Should().Be(SwapOutStatus.Pending);
        await _lightning.DidNotReceiveWithAnyArgs().TrackPaymentV2Async(default!, default!, default);
    }

    [Fact]
    public async Task Advance_WhenThePaymentFailed_FailsTheSwap()
    {
        NodeReports(new Payment { Status = Payment.Types.PaymentStatus.Failed, FailureReason = PaymentFailureReason.FailureReasonTimeout });

        var response = await Service().AdvanceSwapAsync(_node, Pending(lightningFee: null));

        response.Status.Should().Be(SwapOutStatus.Failed);
        response.ErrorMessage.Should().Contain("FailureReasonTimeout");
    }

    [Fact]
    public async Task Advance_AnUnknownPayment_WaitsUntilTheInvoiceExpired_ThenFails()
    {
        NodeReports(null);
        _spark.GetReceiveStatusAsync("req-1", Arg.Any<CancellationToken>()).ThrowsAsync(new NotSupportedException("UserRequest"));

        _time.Set(DateTimeOffset.UnixEpoch.AddMinutes(30));
        (await Service().AdvanceSwapAsync(_node, Pending(lightningFee: null))).Status.Should().Be(SwapOutStatus.Pending);

        _time.Set(DateTimeOffset.UnixEpoch.AddMinutes(41));
        var response = await Service().AdvanceSwapAsync(_node, Pending(lightningFee: null));

        response.Status.Should().Be(SwapOutStatus.Failed);
        response.ErrorMessage.Should().Contain("never reached the node").And.Contain("unknown");
    }

    [Fact]
    public async Task Advance_ASwapPaidIntoAnotherSparkIdentity_IsRefused()
    {
        var act = () => Service().AdvanceSwapAsync(_node, Pending(lightningFee: null, identity: Ssp));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*resolved by hand*");
    }

    // ── Attribution ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Attribute_RecordsTheTransferTheSspReports_WithItsLeaves()
    {
        var swap = Pending();
        SspReports("transfer-1");
        _spark.GetTransferAsync("transfer-1", Arg.Any<CancellationToken>())
            .Returns(Transfer("transfer-1", 499_000, leaves: [Leaf("l1", 262_144), Leaf("l2", 236_856)]));

        var attributed = await Service().AttributeAsync(swap);

        attributed.Should().BeTrue();
        swap.SparkTransferId.Should().Be("transfer-1");
        swap.SparkLeafIds.Should().Be("l1,l2");
        swap.SparkReceivedSats.Should().Be(499_000);
        NSubstitute.Received.InOrder(() =>
        {
            _spark.ClaimPendingAsync(Arg.Any<CancellationToken>());
            _spark.GetTransferAsync("transfer-1", Arg.Any<CancellationToken>());
            _swapOuts.Update(swap);
        });
    }

    [Fact]
    public async Task Attribute_WhenTheSspDoesNotReportIt_TakesTheUnattributedLightningTransferClosestToTheAmount()
    {
        var swap = Pending();
        SspReports(null);
        _swapOuts.GetSparkTransferIdsAsync().Returns(new HashSet<string> { "taken" });
        ReceivedTransfers(
            Transfer("taken", 500_000),
            Transfer("plain-transfer", 500_000, type: "Transfer"),
            Transfer("too-much", 500_001),
            Transfer("too-little", 494_000),
            Transfer("farther", 497_000),
            Transfer("closest", 499_500));

        var attributed = await Service().AttributeAsync(swap);

        attributed.Should().BeTrue();
        swap.SparkTransferId.Should().Be("closest");
        swap.SparkReceivedSats.Should().Be(499_500);
    }

    [Fact]
    public async Task Attribute_ATransferAnotherSwapHas_IsNotGivenAgain()
    {
        var swap = Pending();
        SspReports("transfer-1");
        _swapOuts.GetSparkTransferIdsAsync().Returns(new HashSet<string> { "transfer-1" });
        ReceivedTransfers(Transfer("transfer-1", 499_000));

        var attributed = await Service().AttributeAsync(swap);

        attributed.Should().BeFalse();
        swap.SparkTransferId.Should().BeNull();
        _swapOuts.DidNotReceive().Update(swap);
    }

    [Fact]
    public async Task Attribute_BeforeTheTransferArrived_Waits()
    {
        var swap = Pending();
        SspReports(null);

        (await Service().AttributeAsync(swap)).Should().BeFalse();
        swap.SparkTransferId.Should().BeNull();
    }

    [Fact]
    public async Task Attribute_AnAttributedSwap_IsNotAttributedAgain()
    {
        (await Service().AttributeAsync(Pending(leafIds: "l1"))).Should().BeTrue();

        await _spark.DidNotReceiveWithAnyArgs().ClaimPendingAsync(default);
    }

    // ── Exit ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exit_ExitsExactlyTheSwapsLeaves_ToItsOwnAddress()
    {
        var swap = Pending(leafIds: "l1,l2");
        _spark.WithdrawLeavesAsync(Arg.Any<IReadOnlyCollection<string>>(), Address, 20_000, Arg.Any<CancellationToken>())
            .Returns(new WithdrawLeavesResult("exit-tx", 499_000, 497_000));

        var step = await Service().ExitAsync(swap);

        step.Should().Be(SparkExitStep.ExitSent);
        swap.TxId.Should().Be("exit-tx");
        swap.OnChainFeeSats.Should().Be(2_000, "the exit from Spark to L1 cost what the leaves held minus the payout");
        swap.ServiceFeeSats.Should().Be(1_000, "the payment brought in 1,000 sats short of the amount");
        swap.Status.Should().Be(SwapOutStatus.Pending);
        await _spark.Received(1).WithdrawLeavesAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "l1", "l2" })), Address, 20_000, Arg.Any<CancellationToken>());
        await _spark.DidNotReceiveWithAnyArgs().WithdrawAllAsync(default!, default, default);
    }

    [Fact]
    public async Task Exit_AnUnattributedSwap_IsAttributedFirst()
    {
        var swap = Pending();
        SspReports("transfer-1");
        _spark.GetTransferAsync("transfer-1", Arg.Any<CancellationToken>()).Returns(Transfer("transfer-1", 499_000));
        _spark.WithdrawLeavesAsync(Arg.Any<IReadOnlyCollection<string>>(), Address, 20_000, Arg.Any<CancellationToken>())
            .Returns(new WithdrawLeavesResult("exit-tx", 499_000, 497_000));

        (await Service().ExitAsync(swap)).Should().Be(SparkExitStep.ExitSent);

        swap.SparkLeafIds.Should().Be("transfer-1-leaf");
        swap.TxId.Should().Be("exit-tx");
    }

    [Fact]
    public async Task Exit_BeforeTheTransferArrived_Waits()
    {
        SspReports(null);

        (await Service().ExitAsync(Pending())).Should().Be(SparkExitStep.Waiting);

        await _spark.DidNotReceiveWithAnyArgs().WithdrawLeavesAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Exit_RenewedLeaves_ExitLeavesOfTheSameValue_ThatNoOtherSwapHas()
    {
        var swap = Pending(leafIds: "l1,l2");
        _swapOuts.GetAllPending().Returns(new List<SwapOut> { swap, Pending(id: 8, leafIds: "other") });
        _spark.WithdrawLeavesAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("l1")), Address, 20_000, Arg.Any<CancellationToken>())
            .ThrowsAsync(new SparkLeavesNotSpendableException("withdraw_leaves", ["l1", "l2"]));
        _spark.GetSpendableLeavesAsync(Arg.Any<CancellationToken>()).Returns(new List<SparkLeaf>
        {
            Leaf("other", 499_000), Leaf("n1", 262_144), Leaf("n2", 236_856), Leaf("stray", 1_000)
        });
        _spark.WithdrawLeavesAsync(Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("n1")), Address, 20_000, Arg.Any<CancellationToken>())
            .Returns(new WithdrawLeavesResult("exit-tx", 499_000, 497_000));

        (await Service().ExitAsync(swap)).Should().Be(SparkExitStep.ExitSent);

        swap.SparkLeafIds.Should().Be("n1,n2");
        swap.TxId.Should().Be("exit-tx");
    }

    [Fact]
    public async Task Exit_LeavesThatAlreadyExited_AreNotExitedAgain()
    {
        // A crash after the exit, before its txid was saved and before its payout was seen
        var swap = Pending(leafIds: "l1,l2");
        _spark.WithdrawLeavesAsync(Arg.Any<IReadOnlyCollection<string>>(), Address, 20_000, Arg.Any<CancellationToken>())
            .ThrowsAsync(new SparkLeavesNotSpendableException("withdraw_leaves", ["l1", "l2"]));
        _spark.GetTransfersAsync(TransferDirection.Sent, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<SparkTransfer> { Transfer("exit", 499_000, "CooperativeExit", Leaf("l2", 236_856)) });
        _spark.GetSpendableLeavesAsync(Arg.Any<CancellationToken>()).Returns(new List<SparkLeaf> { Leaf("n1", 499_000) });

        (await Service().ExitAsync(swap)).Should().Be(SparkExitStep.ExitSent);

        await _spark.Received(1).WithdrawLeavesAsync(Arg.Any<IReadOnlyCollection<string>>(), Address, 20_000, Arg.Any<CancellationToken>());
        swap.SparkLeafIds.Should().Be("l1,l2");
    }

    [Fact]
    public async Task Exit_WithNoLeavesOfTheSameValue_Waits()
    {
        var swap = Pending(leafIds: "l1,l2");
        _spark.WithdrawLeavesAsync(Arg.Any<IReadOnlyCollection<string>>(), Address, 20_000, Arg.Any<CancellationToken>())
            .ThrowsAsync(new SparkLeavesNotSpendableException("withdraw_leaves", ["l1"]));
        _spark.GetSpendableLeavesAsync(Arg.Any<CancellationToken>()).Returns(new List<SparkLeaf> { Leaf("n1", 262_144) });

        (await Service().ExitAsync(swap)).Should().Be(SparkExitStep.Waiting);

        swap.TxId.Should().BeNull();
    }

    [Fact]
    public async Task Exit_AConfirmedPayout_CompletesTheSwap_WithWhatLandedOnChain()
    {
        Payouts((Seed: 0xbb, Sats: 497_500, Confirmations: 1));
        var swap = Pending(txId: "exit-tx", leafIds: "l1");
        swap.SparkReceivedSats = 500_000; // receiving into Spark is free: Spark's fee was on the Lightning route

        (await Service().ExitAsync(swap)).Should().Be(SparkExitStep.Completed);

        swap.Status.Should().Be(SwapOutStatus.Completed);
        swap.TxId.Should().Be(new uint256(Enumerable.Repeat((byte)0xbb, 32).ToArray()).ToString(), "the SSP may fee-bump the exit");
        swap.PayoutSats.Should().Be(497_500);
        swap.OnChainFeeSats.Should().Be(2_500, "the move from Spark to L1 is the on-chain fee");
        swap.ServiceFeeSats.Should().Be(0, "Spark's fee is part of the routing fee");
        swap.LightningFeeSats.Should().Be(12);
        swap.TotalFeesSats.Should().Be(2_512);
        _swapOuts.Received(1).Update(swap);
    }

    [Fact]
    public async Task Exit_AnUnconfirmedPayout_WaitsForItsConfirmation()
    {
        Payouts((Seed: 0xbb, Sats: 497_500, Confirmations: 0));
        var swap = Pending(txId: "exit-tx", leafIds: "l1");

        (await Service().ExitAsync(swap)).Should().Be(SparkExitStep.ExitSent);

        swap.Status.Should().Be(SwapOutStatus.Pending);
        swap.TxId.Should().Be(new uint256(Enumerable.Repeat((byte)0xbb, 32).ToArray()).ToString());
        swap.PayoutSats.Should().BeNull();
    }

    [Fact]
    public async Task Exit_APayoutWhoseExitWasNotSaved_IsNotExitedAgain()
    {
        Payouts((Seed: 0xcc, Sats: 497_000, Confirmations: 0));

        (await Service().ExitAsync(Pending(txId: null, leafIds: "l1"))).Should().Be(SparkExitStep.ExitSent);

        await _spark.DidNotReceiveWithAnyArgs().WithdrawLeavesAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Exit_ASwapPaidIntoAnotherSparkIdentity_IsRefused()
    {
        var act = () => Service().ExitAsync(Pending(identity: Ssp, leafIds: "l1"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*resolved by hand*");
        await _spark.DidNotReceiveWithAnyArgs().WithdrawLeavesAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Exit_AnUnpaidSwap_IsRefused()
    {
        var act = () => Service().ExitAsync(Pending(lightningFee: null));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not paid yet*");
    }

    [Fact]
    public void ExactLeafSet_FindsLeavesAddingUpToExactlyTheAmount()
    {
        var leaves = new[] { Leaf("a", 8), Leaf("b", 4), Leaf("c", 2), Leaf("d", 1), Leaf("e", 5) };

        SparkSwapService.ExactLeafSet(leaves, 11).Should().Equal("a", "c", "d");
        SparkSwapService.ExactLeafSet(leaves, 9).Should().Equal("a", "d");
        SparkSwapService.ExactLeafSet(leaves, 20).Should().HaveCount(5);
        SparkSwapService.ExactLeafSet(leaves, 21).Should().BeNull();
        SparkSwapService.ExactLeafSet([], 1).Should().BeNull();
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Set(DateTimeOffset now) => _now = now;
    }
}
