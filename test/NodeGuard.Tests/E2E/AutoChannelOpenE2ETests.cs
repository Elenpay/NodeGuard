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
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data.Models;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NodeGuard.Helpers;
using NodeGuard.TestHelpers;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// E2E: demand-driven channel opening, from real refused forwards to a confirmed channel. Drains bob's
/// outbound to carol, has alice route payments alice→bob→carol so bob's link refuses them with
/// INSUFFICIENT_BALANCE, then switches bob to <see cref="AutoChannelOpenMode.Auto"/> on the hot wallet and
/// asserts on what <c>AutoChannelOpenJob</c> decided: the recommendation's evidence and sizing, the request
/// it promoted, and the new Bob→Carol channel opening at the price bob already charges carol.
/// Order-agnostic and self-resetting; serial with the other e2e classes via <c>[Collection("E2E")]</c>.
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class AutoChannelOpenE2ETests : RoutingEngineE2EBase
{
    // Every refused payment is this size, so MaxBurstPaymentSats and MissedSats are exact
    private const long RefusedPaymentSats = 1_000_000;
    // The fewest that still reads as repeated demand; Constants reads AUTO_CHANNEL_OPEN_MIN_BURSTS from the
    // e2e-runner env, so this tracks docker/e2e/docker-compose.yml
    private static readonly int Bursts = Constants.AUTO_CHANNEL_OPEN_MIN_BURSTS;

    // Identical retries inside a burst: the planner must collapse them, so FailedAttempts != Bursts
    private const int AttemptsPerBurst = 2;

    // Per channel, well under one refused payment, so bob can't serve any of them on any channel
    private const long ResidualUsableSats = 200_000;
    // Larger than any bob→carol balance, so the drain is one payment unless it has to halve
    private const long DrainChunkSats = 50_000_000;
    private const long MinDrainPaymentSats = 10_000;

    // Mirrors ChannelOpenInitiatorService.ReserveFraction
    private const double ReserveFraction = 0.01;

    // Neutralises the profitability gate: its chain cost comes from the mainnet mempool feed, not regtest
    private const double CostToEarnRatio = 1_000;
    private const long BudgetSats = 50_000_000;

    // Two coins under the plan's ~4M sats, so the wallet clamp is what binds and the open spends both
    private static readonly long[] SmallWalletCoinSats = [1_500_000, 1_300_000];
    private const string SmallWalletName = "E2E auto channel open small wallet";
    // An internal-wallet account no seeded wallet uses, so its addresses never collide with theirs
    private const string SmallWalletAccountId = "77";

    private const int JobWaitAttempts = 150; // 5 min of 2s polls; the job runs every ROUTING_ENGINE_JOB_INTERVAL_SECONDS

    public AutoChannelOpenE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact, Trait("Speed", "Slow")]
    public async Task AutoChannelOpen_RefusedForwardsToDrainedPeer_OpensSecondChannelAtInheritedFee()
    {
        var scenario = await LoadScenarioAsync();
        var (client, headers, rpc) = (scenario.Client, scenario.Headers, scenario.Rpc);
        var (bob, carolNode) = (scenario.Bob, scenario.Carol);
        var (bobNodeId, carolNodeId) = (scenario.BobNodeId, scenario.CarolNodeId);
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        try
        {
            var (carolScids, dearest, expectedMissedFeeMsat) = await ShapeRefusedDemandAsync(scenario);

            // Earlier scenarios leave unconfirmed change in the hot wallet; with no spendable coin the first
            // promotion fails and a later cycle re-promotes the same row onto a new request
            await MineAsync(rpc, 6);

            var recommendation = await EnableAutoAndAwaitPromotionAsync(scenario, walletId);

            // Evidence: each payment tried twice, all refused
            recommendation.Bursts.Should().Be(Bursts);
            recommendation.FailedAttempts.Should().Be(Bursts * AttemptsPerBurst);
            recommendation.MissedSats.Should().Be(Bursts * RefusedPaymentSats, "identical retries collapse into one payment");
            recommendation.MaxBurstPaymentSats.Should().Be(RefusedPaymentSats);
            recommendation.MissedFeeMsat.Should().Be(expectedMissedFeeMsat);
            carolScids.Should().Contain(recommendation.OutgoingChannelId);
            recommendation.InheritedFeeRatePpm.Should().Be(dearest.FeePerMil, "the channel opens at the most bob already charges carol");
            recommendation.InheritedBaseFeeMsat.Should().Be(dearest.BaseFeeMsat, "base fee comes off the same channel as the rate");

            var (expectedCapacity, expectedRegime) = ExpectedSize(recommendation);
            _output.WriteLine($"[sizing] out={recommendation.OutSats} in={recommendation.InSats} missed={recommendation.MissedSats} → {expectedCapacity} sats ({expectedRegime})");
            recommendation.BindingClamp.Should().Be(ChannelOpenBindingClamp.None,
                $"nothing should bind a {expectedCapacity} sat channel — a WalletBalance clamp means hot wallet #{walletId} is short");
            recommendation.Regime.Should().Be(expectedRegime);
            recommendation.SuggestedCapacitySats.Should().Be(expectedCapacity);

            recommendation.ChannelOperationRequestId.Should().NotBeNull("promotion links the request it created");
            var requestId = recommendation.ChannelOperationRequestId!.Value;

            await using (var db = CreateDbContext())
            {
                var audit = await db.AuditLogs.AsNoTracking()
                    .Where(a => a.ActionType == AuditActionType.Create
                                && a.ObjectAffected == AuditObjectType.ChannelOpenRecommendation
                                && a.ObjectId == recommendation.Id.ToString())
                    .OrderByDescending(a => a.Id)
                    .FirstOrDefaultAsync();
                audit.Should().NotBeNull("the job audits every recommendation it records");
                audit!.Details.Should().Contain(carolNode.PubKey);

                var request = await db.ChannelOperationRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
                request.RequestType.Should().Be(OperationRequestType.Open);
                request.SourceNodeId.Should().Be(bobNodeId);
                request.DestNodeId.Should().Be(carolNodeId);
                request.WalletId.Should().Be(walletId);
                request.SatsAmount.Should().Be(recommendation.SuggestedCapacitySats, "Auto promotes at the suggested capacity");
                request.UserId.Should().BeNull("the job promoted it, not an operator");
                request.InitialChannelFeeRatePpm.Should().Be(recommendation.InheritedFeeRatePpm);
                request.InitialChannelBaseFeeMsat.Should().Be(recommendation.InheritedBaseFeeMsat);
            }

            // A hot wallet needs no signatures, so promotion dispatched ChannelOpenJob already
            var channelId = (int)await MineUntilChannelOpenedAsync(client, headers, rpc, requestId);

            ulong newScid;
            await using (var db = CreateDbContext())
            {
                newScid = await db.Channels.AsNoTracking().Where(c => c.Id == channelId).Select(c => c.ChanId).SingleAsync();
            }
            carolScids.Should().NotContain(newScid, "the open buys a second channel, not one we already had");

            var opened = await PollAsync(
                async () => (await bob.ChannelsToAsync(carolNode.PubKey)).FirstOrDefault(c => c.ChanId == newScid),
                c => c is { Active: true },
                attempts: 20, delay: TimeSpan.FromSeconds(3),
                what: $"bob's new channel {newScid} to carol is active");
            opened!.Capacity.Should().Be(recommendation.SuggestedCapacitySats);
            opened.Initiator.Should().BeTrue("bob funded it");

            var newPolicy = await bob.OutboundPolicyAsync(newScid);
            newPolicy.Should().NotBeNull();
            newPolicy!.FeePerMil.Should().Be(recommendation.InheritedFeeRatePpm!.Value, "the new channel opens at the inherited rate");
            newPolicy.BaseFeeMsat.Should().Be(recommendation.InheritedBaseFeeMsat!.Value);
            _output.WriteLine($"[result] channel #{channelId} scid {newScid}: {opened.Capacity} sats at {newPolicy.FeePerMil} ppm / {newPolicy.BaseFeeMsat} msat");
        }
        finally
        {
            await ResetAutoChannelOpenAsync(bobNodeId, dynamicFees: scenario.BobDynamicFees);
        }
    }

    /// <summary>
    /// The wallet clamp's own case: a funding wallet too small for the plan. The channel must be sized to
    /// what the wallet can fund and still open, with every coin spent and change left above dust.
    /// </summary>
    [E2EFact, Trait("Speed", "Slow")]
    public async Task AutoChannelOpen_FundingWalletSmallerThanThePlan_OpensTheBiggestChannelItCanFund()
    {
        var scenario = await LoadScenarioAsync();
        var (client, headers, rpc) = (scenario.Client, scenario.Headers, scenario.Rpc);

        try
        {
            // Funding mines first, so a channel an earlier scenario left pending confirms before the
            // drain sees it rather than after, when it would refill bob's outbound and void the demand
            var (walletId, coins) = await FundSmallWalletAsync(rpc);

            await ShapeRefusedDemandAsync(scenario);
            var walletSats = coins.Sum(c => c.Value);

            var recommendation = await EnableAutoAndAwaitPromotionAsync(scenario, walletId);

            var (unclampedCapacity, _) = ExpectedSize(recommendation);
            unclampedCapacity.Should().BeGreaterThan(walletSats, "the plan has to outgrow the wallet for its clamp to bind");
            recommendation.BindingClamp.Should().Be(ChannelOpenBindingClamp.WalletBalance);
            recommendation.SuggestedCapacitySats.Should().BeLessThan(walletSats - Constants.MINIMUM_UTXO_VALUE_SATS,
                "the clamp holds back the funding fee and a change output");
            _output.WriteLine($"[sizing] wallet {walletSats} sats in {coins.Count} coin(s) → {recommendation.SuggestedCapacitySats} sats (unclamped {unclampedCapacity})");

            var requestId = recommendation.ChannelOperationRequestId!.Value;
            var channelId = (int)await MineUntilChannelOpenedAsync(client, headers, rpc, requestId);

            string fundingTxId;
            await using (var db = CreateDbContext())
            {
                fundingTxId = await db.Channels.AsNoTracking().Where(c => c.Id == channelId).Select(c => c.FundingTx).SingleAsync();
            }

            var fundingTx = await rpc.GetRawTransactionAsync(uint256.Parse(fundingTxId));
            fundingTx.Inputs.Select(i => i.PrevOut).Should().BeEquivalentTo(coins.Select(c => c.Outpoint), "every coin funds the channel");
            fundingTx.Outputs.Should().HaveCount(2, "a normal open keeps a change output next to the funding one");

            var channelOutput = fundingTx.Outputs.Single(o => o.Value.Satoshi == recommendation.SuggestedCapacitySats);
            var change = fundingTx.Outputs.Single(o => o != channelOutput).Value.Satoshi;
            change.Should().BeGreaterThanOrEqualTo(Constants.MINIMUM_UTXO_VALUE_SATS, "a dust change output would not relay");
            _output.WriteLine($"[result] funding tx {fundingTxId}: channel {channelOutput.Value.Satoshi} sats, change {change} sats");
        }
        finally
        {
            await ResetAutoChannelOpenAsync(scenario.BobNodeId, dynamicFees: scenario.BobDynamicFees);
        }
    }

    private sealed record Scenario(
        Nodeguard.NodeGuardService.NodeGuardServiceClient Client,
        Grpc.Core.Metadata Headers,
        NBitcoin.RPC.RPCClient Rpc,
        LndTestClient Alice,
        LndTestClient Bob,
        LndTestClient Carol,
        int BobNodeId,
        int CarolNodeId,
        bool BobDynamicFees);

    private async Task<Scenario> LoadScenarioAsync()
    {
        var client = CreateClient(out var headers);

        var nodes = await WaitForNodesAsync(client, headers);
        var aliceNode = nodes.Single(n => n.Name == "alice");
        var bobNode = nodes.Single(n => n.Name == "bob");
        var carolNode = nodes.Single(n => n.Name == "carol");
        _output.WriteLine($"alice={aliceNode.PubKey} bob={bobNode.PubKey} carol={carolNode.PubKey}");

        await using var db = CreateDbContext();
        var bobRow = await db.Nodes.AsNoTracking().SingleAsync(n => n.PubKey == bobNode.PubKey);
        var carolNodeId = await db.Nodes.AsNoTracking().Where(n => n.PubKey == carolNode.PubKey)
            .Select(n => n.Id).SingleAsync();

        return new Scenario(
            client, headers, CreateBitcoindRpc(),
            LndTestClient.FromEnv("alice", aliceNode.PubKey),
            LndTestClient.FromEnv("bob", bobNode.PubKey),
            LndTestClient.FromEnv("carol", carolNode.PubKey),
            bobRow.Id, carolNodeId, bobRow.DynamicFeeManagementEnabled);
    }

    /// <summary>
    /// Drains bob→carol and has alice send <see cref="Bursts"/> refused payments through it, waiting until
    /// every refusal is recorded. Returns bob's carol channels, his dearest policy toward carol, and the
    /// missed fee the planner should derive from the refusals.
    /// </summary>
    private async Task<(HashSet<ulong> CarolScids, ChannelFeeReport Dearest, long ExpectedMissedFeeMsat)> ShapeRefusedDemandAsync(Scenario scenario)
    {
        var (alice, bob, carol) = (scenario.Alice, scenario.Bob, scenario.Carol);

        await ResetAutoChannelOpenAsync(scenario.BobNodeId, dynamicFees: false);

        var aliceToBobScid = await ResolveAliceToBobAsync(scenario.Client, scenario.Headers, scenario.Rpc, alice, bob.PubKey);
        _output.WriteLine($"[setup] alice→bob first hop scid {aliceToBobScid}");

        await DrainUsableLocalAsync(bob, carol);

        // After the drain, so bob's own settled sends to carol can't read as settled outbound demand
        await Task.Delay(TimeSpan.FromSeconds(3));
        await ResetRoutingEngineStateAsync();

        // The planner prices by the dearest of bob's carol channels, off FeeReport
        var carolScids = (await bob.ChannelsToAsync(carol.PubKey)).Select(c => c.ChanId).ToHashSet();
        var policies = new List<ChannelFeeReport>();
        foreach (var scid in carolScids)
        {
            var policy = await bob.OutboundPolicyAsync(scid);
            if (policy != null) policies.Add(policy);
        }
        var dearest = policies.MaxBy(p => p.FeePerMil);
        dearest.Should().NotBeNull("bob must report an outbound policy toward carol, or nothing can be priced");
        dearest!.FeePerMil.Should().BePositive("a zero-ppm peer earns nothing and is never planned");
        _output.WriteLine($"[setup] bob's dearest carol policy: {dearest.FeePerMil} ppm / {dearest.BaseFeeMsat} msat on scid {dearest.ChanId}");

        await SendRefusedBurstsAsync(alice, bob, carol, aliceToBobScid);

        var failures = await PollAsync(
            async () =>
            {
                await using var db = CreateDbContext();
                return await db.ForwardingHtlcEvents.AsNoTracking()
                    .Where(e => e.ManagedNodePubKey == bob.PubKey
                                && e.EventType == HtlcEventType.Forward
                                && e.EventCase == HtlcEventCase.LinkFailEvent
                                && e.FailureDetail == (int)Routerrpc.FailureDetail.InsufficientBalance)
                    .ToListAsync();
            },
            rows => rows.Count >= Bursts * AttemptsPerBurst,
            attempts: 20, delay: TimeSpan.FromSeconds(3),
            what: "NodeHtlcSubscribeJob recorded bob's refused forwards");
        failures.Should().HaveCount(Bursts * AttemptsPerBurst, "every refused attempt is one LinkFailEvent row");
        failures.Should().OnlyContain(e => e.OutgoingAmountMsat == (ulong)RefusedPaymentSats * 1_000);
        failures.Should().OnlyContain(e => carolScids.Contains(e.OutgoingChannelId),
            "the refusing link is one of bob's channels to carol");

        // The planner prefers the fee alice offered over bob's rate: her gossip view of his policy can lag
        // the fee engine. Same route every attempt, so one offer; ppm only if no row recorded it.
        var offeredFeesMsat = failures.Select(e => e.FeeMsat).Distinct().ToList();
        offeredFeesMsat.Should().ContainSingle("every attempt took the same route at the same price");
        var eventPpm = failures.Max(e => e.RoutingFeePpm) ?? dearest.FeePerMil;
        var feePerPaymentMsat = offeredFeesMsat[0] ?? RefusedPaymentSats * 1_000 * eventPpm / 1_000_000;
        var expectedMissedFeeMsat = Bursts * feePerPaymentMsat;
        expectedMissedFeeMsat.Should().BeGreaterThanOrEqualTo(Constants.AUTO_CHANNEL_OPEN_MIN_MISSED_FEE_MSAT,
            "the shaped demand must clear the fee gate, or there is nothing for the job to plan");

        return (carolScids, dearest, expectedMissedFeeMsat);
    }

    /// <summary>
    /// Only after the demand is in place, so no run can see a partial burst set and plan off it. Waits for
    /// the job to record and promote carol's recommendation onto a request that reached dispatch.
    /// </summary>
    private async Task<ChannelOpenRecommendation> EnableAutoAndAwaitPromotionAsync(Scenario scenario, int walletId)
    {
        await using (var db = CreateDbContext())
        {
            var updated = await db.Nodes
                .Where(n => n.Id == scenario.BobNodeId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(n => n.AutoChannelOpenEnabled, true)
                    .SetProperty(n => n.AutoChannelOpenMode, AutoChannelOpenMode.Auto)
                    .SetProperty(n => n.AutoChannelOpenWalletId, (int?)walletId)
                    .SetProperty(n => n.AutoChannelOpenBudgetSats, (long?)BudgetSats)
                    // Null start ⇒ a fresh period, so opens from other scenarios don't count against it
                    .SetProperty(n => n.AutoChannelOpenBudgetStartDatetime, (DateTimeOffset?)null)
                    .SetProperty(n => n.AutoChannelOpenBudgetRefreshInterval, (TimeSpan?)null)
                    .SetProperty(n => n.AutoChannelOpenMinSizeSats, (long?)null)
                    .SetProperty(n => n.AutoChannelOpenMaxSizeSats, (long?)null)
                    .SetProperty(n => n.MaxChannelOpenCostToEarnRatio, (double?)CostToEarnRatio)
                    .SetProperty(n => n.RoutingEngineDryRun, false));
            updated.Should().Be(1, "bob should be seeded and have automatic channel opening switched on");
        }

        ChannelOpenRecommendation? recommendation = null;
        ChannelOperationRequestStatus? requestStatus = null;
        for (var attempt = 0; attempt < JobWaitAttempts; attempt++)
        {
            await using (var db = CreateDbContext())
            {
                recommendation = await db.ChannelOpenRecommendations.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.NodeId == scenario.BobNodeId && r.PeerPubKey == scenario.Carol.PubKey);
                requestStatus = recommendation?.ChannelOperationRequestId is { } id
                    ? await db.ChannelOperationRequests.AsNoTracking().Where(r => r.Id == id)
                        .Select(r => (ChannelOperationRequestStatus?)r.Status).SingleOrDefaultAsync()
                    : null;
            }

            if (recommendation != null)
                _output.WriteLine(
                    $"[recommendation] #{recommendation.Id} status={recommendation.Status} bursts={recommendation.Bursts} capacity={recommendation.SuggestedCapacitySats} clamp={recommendation.BindingClamp} request={recommendation.ChannelOperationRequestId} requestStatus={requestStatus}");

            // A request that failed before dispatch gets replaced on a later cycle, so wait for a live one
            if (recommendation is { Status: ChannelOpenRecommendationStatus.Promoted }
                && requestStatus is not ChannelOperationRequestStatus.Failed) break;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        recommendation.Should().NotBeNull("AutoChannelOpenJob should have recommended a channel to the drained peer");
        recommendation!.Status.Should().Be(ChannelOpenRecommendationStatus.Promoted, "Auto mode promotes what it recommends");
        requestStatus.Should().NotBe(ChannelOperationRequestStatus.Failed,
            "the promoted request must reach dispatch — check NodeGuard's log for the promotion failure reason");

        return recommendation;
    }

    /// <summary>
    /// A hot wallet of its own, so the dev wallet's 20 BTC stays untouched: reused across runs, topped up
    /// each time with <see cref="SmallWalletCoinSats"/> coins and confirmed. Returns every confirmed coin
    /// it holds, leftover change from an earlier run included, since the clamp counts those too.
    /// </summary>
    private async Task<(int WalletId, List<(NBitcoin.OutPoint Outpoint, long Value)> Coins)> FundSmallWalletAsync(NBitcoin.RPC.RPCClient rpc)
    {
        int walletId;
        DerivationStrategyBase derivationStrategy;
        await using (var db = CreateDbContext())
        {
            var wallet = await db.Wallets.Include(w => w.Keys).FirstOrDefaultAsync(w => w.Name == SmallWalletName);
            if (wallet == null)
            {
                var internalWallet = await db.InternalWallets.AsNoTracking().FirstAsync();
                wallet = CreateWallet.SingleSig(internalWallet, SmallWalletAccountId);
                wallet.Name = SmallWalletName;
                // Already persisted; the ids are enough and re-attaching would insert it again
                wallet.InternalWallet = null;
                db.Wallets.Add(wallet);
                await db.SaveChangesAsync();
            }

            walletId = wallet.Id;
            derivationStrategy = wallet.GetDerivationStrategy()!;
        }

        var explorer = new ExplorerClient(
            new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC"),
            new Uri(Env("NBXPLORER_URI", "http://localhost:32838")));
        await explorer.TrackAsync(derivationStrategy);

        foreach (var sats in SmallWalletCoinSats)
        {
            var address = (await explorer.GetUnusedAsync(derivationStrategy, DerivationFeature.Deposit, reserve: true)).Address;
            await rpc.SendToAddressAsync(address, Money.Satoshis(sats));
        }
        await MineAsync(rpc, 6);

        var minimumSats = SmallWalletCoinSats.Sum();
        var utxos = await PollAsync(
            () => explorer.GetUTXOsAsync(derivationStrategy),
            u => u.Confirmed.UTXOs.Sum(x => ((Money)x.Value).Satoshi) >= minimumSats && u.Unconfirmed.UTXOs.Count == 0,
            attempts: 30, delay: TimeSpan.FromSeconds(2),
            what: $"NBXplorer confirmed the small funding wallet's {SmallWalletCoinSats.Length} new coin(s)");

        var coins = utxos.Confirmed.UTXOs
            .Where(u => ((Money)u.Value).Satoshi > Constants.MINIMUM_UTXO_VALUE_SATS)
            .Select(u => (u.Outpoint, ((Money)u.Value).Satoshi))
            .ToList();
        _output.WriteLine($"[setup] small funding wallet #{walletId}: {coins.Count} coin(s), {coins.Sum(c => c.Item2)} sats");

        return (walletId, coins);
    }

    /// <summary>
    /// Switches bob's channel opener off and clears his recommendations — on the way in so an earlier run's
    /// Promoted row can't put carol on cooldown, and on the way out so nothing opens under the next scenario.
    /// The fee engine stays off during the run so bob's price toward carol can't move under the assertions.
    /// </summary>
    private static async Task ResetAutoChannelOpenAsync(int nodeId, bool dynamicFees)
    {
        await using var db = CreateDbContext();
        await db.Nodes
            .Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.AutoChannelOpenEnabled, false)
                .SetProperty(n => n.AutoChannelOpenMode, AutoChannelOpenMode.Recommendation)
                .SetProperty(n => n.DynamicFeeManagementEnabled, dynamicFees));
        await db.ChannelOpenRecommendations.Where(r => r.NodeId == nodeId).ExecuteDeleteAsync();
    }

    /// <summary>
    /// Alice's first hop must carry every refused payment plus bob's fee, so the refusal lands on bob's
    /// outgoing link and never on alice's. Opens one through NodeGuard if none qualifies.
    /// </summary>
    private async Task<ulong> ResolveAliceToBobAsync(
        Nodeguard.NodeGuardService.NodeGuardServiceClient client, Grpc.Core.Metadata headers,
        NBitcoin.RPC.RPCClient rpc, LndTestClient alice, string bobPubKey)
    {
        var minLocal = (long)(RefusedPaymentSats * 1.05);
        var existing = (await alice.ChannelsToAsync(bobPubKey))
            .Where(c => c.Active && c.ChanId != 0 && Usable(c) >= minLocal)
            .MaxBy(c => c.LocalBalance);
        if (existing != null) return existing.ChanId;

        _output.WriteLine($"[setup] no Alice→Bob with {minLocal} sats usable — opening one through NodeGuard");
        var openedId = (int)await OpenChannelAndConfirmAsync(client, headers, rpc, alice.PubKey, bobPubKey);
        await using var db = CreateDbContext();
        return await db.Channels.AsNoTracking().Where(c => c.Id == openedId).Select(c => c.ChanId).SingleAsync();
    }

    /// <summary>
    /// Pays carol directly from bob until EVERY channel between them is down to
    /// <see cref="ResidualUsableSats"/> usable. Non-strict forwarding tries all of them, so one with
    /// balance left would serve the payment and nothing would be refused.
    /// </summary>
    private async Task DrainUsableLocalAsync(LndTestClient from, LndTestClient to, int maxPayments = 60)
    {
        for (var i = 0; i < maxPayments; i++)
        {
            var channels = (await from.ChannelsToAsync(to.PubKey)).Where(c => c.Active && c.ChanId != 0).ToList();
            channels.Should().NotBeEmpty($"{from.Name} needs a channel to {to.Name} for its link to refuse anything");

            var fullest = channels.MaxBy(Usable)!;
            _output.WriteLine($"[drain] {from.Name}→{to.Name}: {channels.Count} channel(s), fullest scid {fullest.ChanId} usable {Usable(fullest)}");
            if (Usable(fullest) <= ResidualUsableSats) return;

            var amount = Math.Min(Usable(fullest) - ResidualUsableSats / 2, DrainChunkSats);
            while (amount >= MinDrainPaymentSats && !await from.PayViaScidAsync(to, fullest.ChanId, amount))
            {
                // Commitment fee and anchor reserve make the exact headroom fuzzy, so back off and retry
                _output.WriteLine($"[drain] {amount} sat payment over scid {fullest.ChanId} failed — halving");
                amount /= 2;
            }
        }

        throw new InvalidOperationException(
            $"{from.Name}→{to.Name} never drained to {ResidualUsableSats} usable sats within {maxPayments} payments");
    }

    /// <summary>
    /// Bursts of identical attempts over alice→bob→carol, each refused at bob's link. Attempts inside a
    /// burst go back to back, well under the burst gap; bursts sit a margin over it.
    /// </summary>
    private async Task SendRefusedBurstsAsync(LndTestClient alice, LndTestClient bob, LndTestClient carol, ulong firstHopScid)
    {
        var interBurstDelay = TimeSpan.FromSeconds(Constants.AUTO_CHANNEL_OPEN_BURST_GAP_SECONDS + 2);
        for (var burst = 0; burst < Bursts; burst++)
        {
            if (burst > 0) await Task.Delay(interBurstDelay);

            for (var attempt = 0; attempt < AttemptsPerBurst; attempt++)
            {
                var htlc = await alice.SendToRouteAsync(carol, firstHopScid, [bob.PubKey, carol.PubKey], RefusedPaymentSats);
                _output.WriteLine($"[refuse] burst {burst} attempt {attempt}: status={htlc.Status} code={htlc.Failure?.Code} source={htlc.Failure?.FailureSourceIndex}");

                htlc.Status.Should().Be(HTLCAttempt.Types.HTLCStatus.Failed, "bob has no outbound to carol left");
                htlc.Failure.Should().NotBeNull();
                htlc.Failure!.Code.Should().Be(Failure.Types.FailureCode.TemporaryChannelFailure,
                    "an insufficient-balance refusal — FEE_INSUFFICIENT here means alice's gossip view of bob's policy is stale");
                htlc.Failure.FailureSourceIndex.Should().Be(1u, "the refusal comes from bob, the first hop");
            }
        }
    }

    /// <summary>
    /// The sizing rule from docs/auto-channel-open.md §5, evaluated on the row's own evidence. Clamps are
    /// left out on purpose; the test asserts none of them bound.
    /// </summary>
    private static (long Capacity, ChannelOpenSizingRegime Regime) ExpectedSize(ChannelOpenRecommendation r)
    {
        var windowDays = Math.Max(Constants.AUTO_CHANNEL_OPEN_WINDOW_HOURS / 24.0, 1.0 / 24.0);
        var netDrainPerDay = Math.Max(0, (r.OutSats + r.MissedSats) / windowDays - r.InSats / windowDays);
        var runway = (long)(netDrainPerDay * Constants.AUTO_CHANNEL_OPEN_TARGET_RUNWAY_DAYS);
        var floor = (long)(r.MaxBurstPaymentSats * Constants.AUTO_CHANNEL_OPEN_BUFFER_MULTIPLIER);

        var regime = runway > floor ? ChannelOpenSizingRegime.Draining : ChannelOpenSizingRegime.Buffer;
        return ((long)(Math.Max(runway, floor) / (1 - ReserveFraction)), regime);
    }

    // Same quantity as the job's LocalChanReserveSat, off the non-deprecated field
    private static long Usable(Lnrpc.Channel c)
        => Math.Max(0, c.LocalBalance - (long)(c.LocalConstraints?.ChanReserveSat ?? 0));
}
