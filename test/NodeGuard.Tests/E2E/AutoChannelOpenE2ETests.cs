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
using NodeGuard.Helpers;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// E2E: a route that earned fees runs out of outbound, and <c>AutoChannelOpenJob</c> opens a fixed-size
/// channel to it. Alice routes one payment alice→bob→carol so bob earns a fee toward carol, bob's outbound
/// to carol is drained, and alice's next payments over the same route are refused at bob's link with
/// INSUFFICIENT_BALANCE. Bob is then switched on in Recommendation mode (row recorded, nothing opened) and
/// then in Auto mode (request created, channel opened at the price bob already charges carol).
/// Order-agnostic and self-resetting; serial with the other e2e classes via <c>[Collection("E2E")]</c>.
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class AutoChannelOpenE2ETests : RoutingEngineE2EBase
{
    private const long SizeSats = 5_000_000;
    private const long BudgetSats = 50_000_000;

    private const long EarningPaymentSats = 100_000;
    private const long RefusedPaymentSats = 1_000_000;
    // Constants reads AUTO_CHANNEL_OPEN_MIN_REFUSALS from the e2e-runner env, so this tracks the compose file
    private static readonly int Refusals = Constants.AUTO_CHANNEL_OPEN_MIN_REFUSALS;

    // Per channel, well under one refused payment and under the low-outbound threshold
    private const long ResidualUsableSats = 200_000;
    // Larger than any bob→carol balance, so the drain is one payment unless it has to halve
    private const long DrainChunkSats = 50_000_000;
    private const long MinDrainPaymentSats = 10_000;

    private const int JobWaitAttempts = 150; // 5 min of 2s polls; the job runs every minute in dev

    public AutoChannelOpenE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact, Trait("Speed", "Slow")]
    public async Task AutoChannelOpen_EarningRouteRunsOutOfOutbound_RecommendsThenOpensAFixedSizeChannel()
    {
        var scenario = await LoadScenarioAsync();
        var (client, headers, rpc) = (scenario.Client, scenario.Headers, scenario.Rpc);
        var (alice, bob, carol) = (scenario.Alice, scenario.Bob, scenario.Carol);
        var walletId = int.Parse(Env("E2E_HOT_WALLET_ID", "3"));

        try
        {
            await ResetAutoChannelOpenAsync(scenario.BobNodeId, dynamicFees: false);
            // Before any payment, so this scenario's forwards are the only ones the job can see
            await ResetRoutingEngineStateAsync();

            var aliceToBobScid = await ResolveAliceToBobAsync(client, headers, rpc, alice, bob.PubKey);
            _output.WriteLine($"[setup] alice→bob first hop scid {aliceToBobScid}");

            var earnedFeeSats = await EarnOnBobToCarolAsync(alice, bob, carol, aliceToBobScid);
            await DrainUsableLocalAsync(bob, carol);
            await SendRefusedPaymentsAsync(alice, bob, carol, aliceToBobScid);

            var carolChannels = await bob.ChannelsToAsync(carol.PubKey);
            var carolScids = carolChannels.Select(c => c.ChanId).ToHashSet();
            var dearest = await DearestPolicyAsync(bob, carolScids);

            // Earlier scenarios leave unconfirmed change in the hot wallet; the wallet check only counts
            // confirmed coins
            await MineAsync(rpc, 6);

            // ── Recommendation mode: recorded, nothing opened ────────────────────────────────────
            await EnableAsync(scenario.BobNodeId, walletId, AutoChannelOpenMode.Recommendation);
            var recommendation = await PollRecommendationAsync(scenario, r => r.Status == ChannelOpenRecommendationStatus.Open,
                "AutoChannelOpenJob recorded an open recommendation for carol");

            recommendation.SuggestedCapacitySats.Should().Be(SizeSats, "every open uses the node's fixed size");
            recommendation.EarnedFeeSats.Should().Be(earnedFeeSats);
            recommendation.SettledOutSats.Should().Be(EarningPaymentSats);
            recommendation.Refusals.Should().Be(Refusals);
            recommendation.PeerCapacitySats.Should().Be(carolChannels.Sum(c => c.Capacity));
            recommendation.UsableLocalSats.Should().BeLessThan(recommendation.PeerCapacitySats * Constants.AUTO_CHANNEL_OPEN_LOW_OUTBOUND_PCT / 100);
            recommendation.InheritedFeeRatePpm.Should().Be(dearest.FeePerMil, "the channel opens at the most bob already charges carol");
            recommendation.InheritedBaseFeeMsat.Should().Be(dearest.BaseFeeMsat, "base fee comes off the same channel as the rate");
            recommendation.ChannelOperationRequestId.Should().BeNull("Recommendation mode leaves the decision to a person");

            // ── Auto mode: promoted, request created, channel opened ─────────────────────────────
            await EnableAsync(scenario.BobNodeId, walletId, AutoChannelOpenMode.Auto);
            recommendation = await PollRecommendationAsync(scenario,
                r => r.Status == ChannelOpenRecommendationStatus.Promoted,
                "AutoChannelOpenJob promoted carol's recommendation");
            recommendation.ChannelOperationRequestId.Should().NotBeNull("promotion links the request it created");
            var requestId = recommendation.ChannelOperationRequestId!.Value;

            await using (var db = CreateDbContext())
            {
                var request = await db.ChannelOperationRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
                request.Status.Should().NotBe(ChannelOperationRequestStatus.Failed,
                    "the promoted request must reach dispatch — check NodeGuard's log for the promotion failure reason");
                request.RequestType.Should().Be(OperationRequestType.Open);
                request.SourceNodeId.Should().Be(scenario.BobNodeId);
                request.DestNodeId.Should().Be(scenario.CarolNodeId);
                request.WalletId.Should().Be(walletId);
                request.SatsAmount.Should().Be(SizeSats);
                request.UserId.Should().BeNull("the job promoted it, not an operator");
                request.InitialChannelFeeRatePpm.Should().Be(dearest.FeePerMil);
                request.InitialChannelBaseFeeMsat.Should().Be(dearest.BaseFeeMsat);

                var audit = await db.AuditLogs.AsNoTracking()
                    .Where(a => a.ActionType == AuditActionType.Create
                                && a.ObjectAffected == AuditObjectType.ChannelOpenRecommendation
                                && a.ObjectId == recommendation.Id.ToString())
                    .ToListAsync();
                audit.Should().NotBeEmpty("the job audits every recommendation it records");
            }

            // A hot wallet needs no signatures, so promotion dispatched ChannelOpenJob already
            var channelId = (int)await MineUntilChannelOpenedAsync(client, headers, rpc, requestId);

            ulong newScid;
            await using (var db = CreateDbContext())
            {
                newScid = await db.Channels.AsNoTracking().Where(c => c.Id == channelId).Select(c => c.ChanId).SingleAsync();
            }
            carolScids.Should().NotContain(newScid, "the open buys a new channel, not one we already had");

            var opened = await PollAsync(
                async () => (await bob.ChannelsToAsync(carol.PubKey)).FirstOrDefault(c => c.ChanId == newScid),
                c => c is { Active: true },
                attempts: 20, delay: TimeSpan.FromSeconds(3),
                what: $"bob's new channel {newScid} to carol is active");
            opened!.Capacity.Should().Be(SizeSats);
            opened.Initiator.Should().BeTrue("bob funded it");

            var newPolicy = await bob.OutboundPolicyAsync(newScid);
            newPolicy.Should().NotBeNull();
            newPolicy!.FeePerMil.Should().Be(dearest.FeePerMil, "the new channel opens at the inherited rate");
            newPolicy.BaseFeeMsat.Should().Be(dearest.BaseFeeMsat);
            _output.WriteLine($"[result] channel #{channelId} scid {newScid}: {opened.Capacity} sats at {newPolicy.FeePerMil} ppm / {newPolicy.BaseFeeMsat} msat");
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
    /// One settled alice→bob→carol forward, so the route has earned bob a fee. Refills bob's side from
    /// carol first if an earlier scenario left it too dry to carry the payment. Returns the fee in sats
    /// the job should sum, read back from the recorded row.
    /// </summary>
    private async Task<long> EarnOnBobToCarolAsync(LndTestClient alice, LndTestClient bob, LndTestClient carol, ulong firstHopScid)
    {
        var carolChannels = await bob.ChannelsToAsync(carol.PubKey);
        var carolScids = carolChannels.Select(c => c.ChanId).ToList();
        var fullest = carolChannels.Where(c => c.Active).MaxBy(Usable);
        fullest.Should().NotBeNull("bob needs a channel to carol");
        if (Usable(fullest!) < EarningPaymentSats * 2)
        {
            _output.WriteLine($"[earn] bob→carol scid {fullest!.ChanId} has {Usable(fullest)} usable — refilling from carol");
            var refilled = await carol.PayViaScidAsync(bob, fullest.ChanId, EarningPaymentSats * 4);
            refilled.Should().BeTrue("carol pays bob back over their channel to give bob outbound again");
        }

        var htlc = await alice.SendToRouteAsync(carol, firstHopScid, [bob.PubKey, carol.PubKey], EarningPaymentSats);
        htlc.Status.Should().Be(HTLCAttempt.Types.HTLCStatus.Succeeded, "bob has outbound to carol before the drain");

        var settled = await PollAsync(
            async () =>
            {
                await using var db = CreateDbContext();
                return await db.ForwardingHtlcEvents.AsNoTracking()
                    .Where(e => e.ManagedNodePubKey == bob.PubKey
                                && e.EventType == HtlcEventType.Forward
                                && e.Outcome == ForwardingOutcome.Settled
                                && carolScids.Contains(e.OutgoingChannelId))
                    .ToListAsync();
            },
            rows => rows.Count == 1 && rows[0].FeeMsat != null,
            attempts: 20, delay: TimeSpan.FromSeconds(3),
            what: "NodeHtlcSubscribeJob recorded bob's settled forward with its fee");

        var feeMsat = settled[0].FeeMsat!.Value;
        feeMsat.Should().BePositive("a free route earns nothing and never qualifies");
        _output.WriteLine($"[earn] bob forwarded {EarningPaymentSats} sats to carol for {feeMsat} msat");

        return feeMsat / 1000;
    }

    /// <summary>
    /// Alice's first hop must carry every payment plus bob's fee, so a refusal lands on bob's outgoing
    /// link and never on alice's. Opens one through NodeGuard if none qualifies.
    /// </summary>
    private async Task<ulong> ResolveAliceToBobAsync(
        Nodeguard.NodeGuardService.NodeGuardServiceClient client, Grpc.Core.Metadata headers,
        NBitcoin.RPC.RPCClient rpc, LndTestClient alice, string bobPubKey)
    {
        var minLocal = (long)((RefusedPaymentSats + EarningPaymentSats) * 1.05);
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
    /// balance left would serve the payment and nothing would be refused. Direct payments are sends, not
    /// forwards, so they add nothing to what the route earned.
    /// </summary>
    private async Task DrainUsableLocalAsync(LndTestClient from, LndTestClient to, int maxPayments = 60)
    {
        for (var i = 0; i < maxPayments; i++)
        {
            var channels = (await from.ChannelsToAsync(to.PubKey)).Where(c => c.Active && c.ChanId != 0).ToList();
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
    /// <see cref="Refusals"/> payments over alice→bob→carol, each refused at bob's link, and waits until
    /// every refusal is recorded so no job run can see a partial set.
    /// </summary>
    private async Task SendRefusedPaymentsAsync(LndTestClient alice, LndTestClient bob, LndTestClient carol, ulong firstHopScid)
    {
        for (var i = 0; i < Refusals; i++)
        {
            var htlc = await alice.SendToRouteAsync(carol, firstHopScid, [bob.PubKey, carol.PubKey], RefusedPaymentSats);
            _output.WriteLine($"[refuse] {i}: status={htlc.Status} code={htlc.Failure?.Code} source={htlc.Failure?.FailureSourceIndex}");

            htlc.Status.Should().Be(HTLCAttempt.Types.HTLCStatus.Failed, "bob has no outbound to carol left");
            htlc.Failure!.Code.Should().Be(Failure.Types.FailureCode.TemporaryChannelFailure,
                "an insufficient-balance refusal — FEE_INSUFFICIENT here means alice's gossip view of bob's policy is stale");
            htlc.Failure.FailureSourceIndex.Should().Be(1u, "the refusal comes from bob, the first hop");
        }

        await PollAsync(
            async () =>
            {
                await using var db = CreateDbContext();
                return await db.ForwardingHtlcEvents.AsNoTracking()
                    .CountAsync(e => e.ManagedNodePubKey == bob.PubKey
                                     && e.EventType == HtlcEventType.Forward
                                     && e.EventCase == HtlcEventCase.LinkFailEvent
                                     && e.FailureDetail == (int)Routerrpc.FailureDetail.InsufficientBalance);
            },
            count => count >= Refusals,
            attempts: 20, delay: TimeSpan.FromSeconds(3),
            what: "NodeHtlcSubscribeJob recorded bob's refused forwards");
    }

    // FeeReport is bob's own side, immediately — unlike the gossip graph, which lags.
    private static async Task<ChannelFeeReport> DearestPolicyAsync(LndTestClient bob, IEnumerable<ulong> scids)
    {
        var policies = new List<ChannelFeeReport>();
        foreach (var scid in scids)
        {
            var policy = await bob.OutboundPolicyAsync(scid);
            if (policy != null) policies.Add(policy);
        }

        var dearest = policies.MaxBy(p => p.FeePerMil);
        dearest.Should().NotBeNull("bob must report an outbound policy toward carol, or nothing can be priced");
        return dearest!;
    }

    private static async Task EnableAsync(int nodeId, int walletId, AutoChannelOpenMode mode)
    {
        await using var db = CreateDbContext();
        var updated = await db.Nodes
            .Where(n => n.Id == nodeId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.AutoChannelOpenEnabled, true)
                .SetProperty(n => n.AutoChannelOpenMode, mode)
                .SetProperty(n => n.AutoChannelOpenWalletId, (int?)walletId)
                .SetProperty(n => n.AutoChannelOpenSizeSats, (long?)SizeSats)
                .SetProperty(n => n.AutoChannelOpenBudgetSats, (long?)BudgetSats)
                // Null start ⇒ a fresh period, so opens from other scenarios don't count against it
                .SetProperty(n => n.AutoChannelOpenBudgetStartDatetime, (DateTimeOffset?)null)
                .SetProperty(n => n.AutoChannelOpenBudgetRefreshInterval, (TimeSpan?)null)
                .SetProperty(n => n.RoutingEngineDryRun, false));
        updated.Should().Be(1, "bob should be seeded");
    }

    private async Task<ChannelOpenRecommendation> PollRecommendationAsync(
        Scenario scenario, Func<ChannelOpenRecommendation, bool> done, string what)
    {
        var recommendation = await PollAsync(
            async () =>
            {
                await using var db = CreateDbContext();
                var row = await db.ChannelOpenRecommendations.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.NodeId == scenario.BobNodeId && r.PeerPubKey == scenario.Carol.PubKey);
                if (row != null)
                    _output.WriteLine($"[recommendation] #{row.Id} status={row.Status} earned={row.EarnedFeeSats} refusals={row.Refusals} usable={row.UsableLocalSats}/{row.PeerCapacitySats} request={row.ChannelOperationRequestId}");
                return row;
            },
            row => row != null && done(row),
            attempts: JobWaitAttempts, delay: TimeSpan.FromSeconds(2),
            what: what);

        return recommendation!;
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

    // Same quantity as the job's LocalChanReserveSat, off the non-deprecated field
    private static long Usable(Lnrpc.Channel c)
        => Math.Max(0, c.LocalBalance - (long)(c.LocalConstraints?.ChanReserveSat ?? 0));
}
