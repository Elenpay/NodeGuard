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
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using Xunit.Abstractions;

namespace NodeGuard.Tests.E2E;

/// <summary>
/// E2E: the AUTOMATIC rebalancer — nothing here asks NodeGuard to rebalance, it decides to. Shapes alice's
/// liquidity into the imbalance the routing engine corrects (Alice→Bob too local, the carol channels too
/// remote), switches the node-level rebalancer on, then asserts on what <c>AutoRebalanceJob</c> produced:
/// the non-manual Rebalance row, its audit trail, and the liquidity having moved alice→bob→carol→alice.
/// Order-agnostic and self-resetting; serial with the other e2e classes via <c>[Collection("E2E")]</c>.
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public class AutoRebalanceE2ETests : RoutingEngineE2EBase
{
    private const long SourceMinCapacitySats = 10_000_000;

    // 0.5 - 0.30 clears the deadband with margin and leaves a deficit above the amount cap
    private const double DestinationDrainRatio = 0.30;
    private const long DrainChunkSats = 2_000_000;
    private const long MinDrainPaymentSats = 10_000;

    // The earn rate the profitability gate prices against, pinned so the plan's fee cap is assertable
    private const uint DestinationEarnPpm = 1_000;

    // 5 x 1000ppm = 5000ppm, well clear of the ~1000ppm the bob (400) + carol (600) hops charge
    private const double CostToEarnRatio = 5.0;
    private const long RebalanceBudgetSats = 1_000_000;

    // Headroom against a stale Pending/InFlight row, not a per-run throttle
    private const int MaxRebalancesInFlight = 3;

    private const int RebalanceWaitAttempts = 90; // 6 min of 4s polls

    public AutoRebalanceE2ETests(ITestOutputHelper output) : base(output)
    {
    }

    [E2EFact, Trait("Speed", "Slow")]
    public async Task AutoRebalancer_DrainsOptedInSource_IntoDepletedPeer_AndSettles()
    {
        var client = CreateClient(out var headers);
        var rpc = CreateBitcoindRpc();

        var nodes = await WaitForNodesAsync(client, headers);
        var aliceNode = nodes.Single(n => n.Name == "alice");
        var bobNode = nodes.Single(n => n.Name == "bob");
        var carolNode = nodes.Single(n => n.Name == "carol");
        _output.WriteLine($"alice={aliceNode.PubKey} bob={bobNode.PubKey} carol={carolNode.PubKey}");

        var alice = LndTestClient.FromEnv("alice", aliceNode.PubKey);
        var carol = LndTestClient.FromEnv("carol", carolNode.PubKey);

        try
        {
            await ResetRebalancerConfigAsync(aliceNode.PubKey);

            var (sourceChannelId, sourceScid) =
                await ResolveSourceChannelAsync(client, headers, rpc, alice, bobNode.PubKey);
            _output.WriteLine($"[setup] source Alice→Bob: NodeGuard channel #{sourceChannelId}, scid {sourceScid}");

            await PinOutboundPolicyAsync(alice, carolNode.PubKey);
            await DrainLocalBalanceAsync(alice, carol, DestinationDrainRatio);

            // Wipe the derived signal AFTER shaping, so EmaLocalRatio re-seeds on the balances we just set
            // instead of an EMA still crawling out of the old ones
            await ResetRoutingEngineStateAsync();

            // Both triggers come from one TargetRatioReevaluationJob pass — it writes every channel of
            // every managed node in a single cycle — so poll them together, and the two sides of the
            // comparison come from the same cycle.
            var deadband = Constants.ROUTING_ENGINE_REBALANCE_DEADBAND;
            var signal = await PollAsync(
                () => ReadSignalAsync(alice, sourceChannelId, sourceScid, carolNode.PubKey),
                sig => sig != null
                       && sig.Source.EmaLocalRatio - sig.Source.TargetLocalRatio > deadband
                       && sig.Destination.AggEma - sig.Destination.AggTarget < -deadband,
                attempts: 20, delay: TimeSpan.FromSeconds(4),
                what: "Alice→Bob reads as too local and alice's carol channels as too remote");

            var sourceState = signal!.Source;
            var destSignal = signal.Destination;
            _output.WriteLine(
                $"[signal] source ema={sourceState.EmaLocalRatio:0.###} target={sourceState.TargetLocalRatio:0.###} | " +
                $"carol aggEma={destSignal.AggEma:0.###} aggTarget={destSignal.AggTarget:0.###} " +
                $"local={destSignal.LocalSats} base={destSignal.BaseSats} deficit={destSignal.DeficitSats}");

            var sourceLocalBefore = signal.SourceLocalSats;
            var sourceExcess = sourceLocalBefore -
                               (long)Math.Round(sourceState.TargetLocalRatio * signal.SourceBaseSats,
                                   MidpointRounding.AwayFromZero);
            var expectedAmount = Math.Min(Math.Min(sourceExcess, destSignal.DeficitSats), Constants.ROUTING_ENGINE_REBALANCE_MAX_AMOUNT_SATS);
            var carolLocalBefore = destSignal.LocalSats;
            _output.WriteLine(
                $"[plan] expected amount {expectedAmount} sats (excess {sourceExcess}, deficit {destSignal.DeficitSats}, cap {Constants.ROUTING_ENGINE_REBALANCE_MAX_AMOUNT_SATS})");
            expectedAmount.Should().BeGreaterThan(Constants.REBALANCE_MIN_AMOUNT_SATS,
                "the shaped imbalance must be worth a rebalance, or there is nothing for the job to plan");

            int baselineRebalanceId, aliceNodeId;
            await using (var db = CreateDbContext())
            {
                baselineRebalanceId = await db.Rebalances.AsNoTracking().MaxAsync(r => (int?)r.Id) ?? 0;
                aliceNodeId = await db.Nodes.AsNoTracking().Where(n => n.PubKey == aliceNode.PubKey)
                    .Select(n => n.Id).SingleAsync();
                var stuck = await db.Rebalances.AsNoTracking()
                    .CountAsync(r => r.Status == RebalanceStatus.Pending || r.Status == RebalanceStatus.InFlight);
                _output.WriteLine($"[baseline] alice=#{aliceNodeId}, last rebalance id {baselineRebalanceId}, {stuck} still in flight");

                // Nothing below asks for a rebalance; the job decides
                var updated = await db.Nodes
                    .Where(n => n.PubKey == aliceNode.PubKey)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(n => n.AutoRebalanceEnabled, true)
                        .SetProperty(n => n.RoutingEngineDryRun, false)
                        .SetProperty(n => n.RebalanceBudgetSats, (long?)RebalanceBudgetSats)
                        // Null start datetime ⇒ a fresh budget period, so fees an earlier scenario spent
                        // don't count against this one
                        .SetProperty(n => n.RebalanceBudgetStartDatetime, (DateTimeOffset?)null)
                        .SetProperty(n => n.MaxRebalancesInFlight, (int?)MaxRebalancesInFlight)
                        .SetProperty(n => n.MaxRebalanceCostToEarnRatio, (double?)CostToEarnRatio));
                updated.Should().Be(1, "alice should be seeded and have the rebalancer switched on");

                var optedIn = await db.Channels
                    .Where(c => c.Id == sourceChannelId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsAutoRebalanceEnabled, true));
                optedIn.Should().Be(1, "the source channel row must exist and be opted in to the rebalancer");
            }

            // A failure status is NOT final — RetryRebalanceJob re-runs the same row on Quartz's schedule —
            // so keep watching until it succeeds or the window closes, then assert on where it ended up
            Rebalance? rebalance = null;
            for (var attempt = 0; attempt < RebalanceWaitAttempts; attempt++)
            {
                await using (var db = CreateDbContext())
                {
                    rebalance = await db.Rebalances.AsNoTracking()
                        .Where(r => r.Id > baselineRebalanceId && !r.IsManual && r.SourceChannelId == sourceChannelId)
                        .OrderBy(r => r.Id)
                        .FirstOrDefaultAsync();
                }

                if (rebalance != null)
                    _output.WriteLine(
                        $"[rebalance] #{rebalance.Id} status={rebalance.Status} attempt={rebalance.AttemptNumber} amount={rebalance.SatsAmount} feeSats={rebalance.FeePaidSats} ppm={rebalance.EffectivePpm}");
                if (rebalance is { Status: RebalanceStatus.Succeeded }) break;

                await Task.Delay(TimeSpan.FromSeconds(4));
            }

            rebalance.Should().NotBeNull(
                "AutoRebalanceJob should have planned and initiated a rebalance out of the opted-in channel");
            rebalance!.Status.Should().Be(RebalanceStatus.Succeeded,
                "the planned circular rebalance alice→bob→carol→alice should settle");
            rebalance.IsManual.Should().BeFalse("the routing engine initiated this, not an operator");
            rebalance.NodeId.Should().Be(aliceNodeId, "alice is the node whose rebalancer is switched on");
            rebalance.SourceNodePubKey.Should().Be(aliceNode.PubKey);
            rebalance.SourceChanIdLnd.Should().Be(sourceScid, "the opted-in channel is the one that gets drained");
            rebalance.TargetPubkey.Should().Be(carolNode.PubKey,
                "the depleted peer is the last hop the rebalance refills through");

            // Tolerance absorbs the sats an in-flight commitment moves between our snapshot and the job's
            // own ListChannels
            rebalance.RequestedAmountSats.Should().BeInRange(
                (long)(expectedAmount * 0.98), (long)(expectedAmount * 1.02),
                "the plan is sized to the smaller of the source's excess and the peer's deficit, capped");
            rebalance.RequestedAmountSats.Should().BeLessThanOrEqualTo(Constants.ROUTING_ENGINE_REBALANCE_MAX_AMOUNT_SATS);

            var expectedMaxFeePct = Math.Round(CostToEarnRatio * DestinationEarnPpm, MidpointRounding.AwayFromZero) / 10_000.0;
            rebalance.MaxFeePct.Should().BeApproximately(expectedMaxFeePct, 1e-9,
                $"the gate prices {CostToEarnRatio}x against the {DestinationEarnPpm}ppm earn rate on the carol channels");
            rebalance.RetryMaxFeePct.Should().Be(rebalance.MaxFeePct, "retries stay inside the profitable ceiling");

            rebalance.FeePaidSats.Should().BeGreaterThan(0, "a real multi-hop route was paid for");
            rebalance.FeePaidSats.Should().BeLessThanOrEqualTo(
                Rebalance.WorstCaseFeeSats(rebalance.SatsAmount, rebalance.MaxFeePct),
                "the fee limit passed to LND is the plan's cap");
            _output.WriteLine($"[result] rebalance #{rebalance.Id}: {rebalance.SatsAmount} sats for {rebalance.FeePaidSats} sats ({rebalance.EffectivePpm} ppm)");

            await using (var db = CreateDbContext())
            {
                var audit = await db.AuditLogs.AsNoTracking()
                    .Where(a => a.ActionType == AuditActionType.RebalanceInitiated
                                && a.ObjectAffected == AuditObjectType.Rebalance
                                && a.ObjectId == sourceChannelId.ToString())
                    .OrderByDescending(a => a.Id)
                    .FirstOrDefaultAsync();
                audit.Should().NotBeNull("the rebalancer audits every rebalance it initiates");
                audit!.Details.Should().Contain(carolNode.PubKey, "the audit records the destination peer it chose");
            }

            // One ListChannels snapshot holds both sides, so the two deltas are consistent with each other
            var moved = await PollAsync(
                async () =>
                {
                    var channels = await alice.ChannelsAsync();
                    var sourceLocal = channels.Where(c => c.ChanId == sourceScid).Sum(c => c.LocalBalance);
                    var carolLocal = channels.Where(c => c.RemotePubkey == carolNode.PubKey).Sum(c => c.LocalBalance);
                    _output.WriteLine($"[balances] source local {sourceLocalBefore} → {sourceLocal}, carol local {carolLocalBefore} → {carolLocal}");
                    return (Source: sourceLocalBefore - sourceLocal, Carol: carolLocal - carolLocalBefore);
                },
                d => d.Source >= rebalance.SatsAmount,
                attempts: 15, delay: TimeSpan.FromSeconds(3),
                what: "the source channel funded the circular payment (its local balance dropped by the amount)");

            moved.Carol.Should().BeGreaterThanOrEqualTo((long)(rebalance.SatsAmount * 0.99),
                "the payment came back in over a channel with the destination peer");
        }
        finally
        {
            await ResetRebalancerConfigAsync(aliceNode.PubKey);
        }
    }

    /// <summary>
    /// Leaves the rebalancer off and no channel opted in — on the way in so an interrupted run can't pick
    /// a source for us, and on the way out so the next scenario isn't rebalanced under.
    /// </summary>
    private static async Task ResetRebalancerConfigAsync(string nodePubKey)
    {
        await using var db = CreateDbContext();
        await db.Nodes
            .Where(n => n.PubKey == nodePubKey)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.AutoRebalanceEnabled, false));
        await db.Channels
            .Where(c => c.IsAutoRebalanceEnabled)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsAutoRebalanceEnabled, false));
    }

    /// <summary>
    /// The channel the rebalancer will drain, opened through NodeGuard's gRPC if absent. Returns both ids:
    /// NodeGuard's row id keys the opt-in and the plan, the LND scid keys the balances.
    /// </summary>
    private async Task<(int ChannelId, ulong Scid)> ResolveSourceChannelAsync(
        Nodeguard.NodeGuardService.NodeGuardServiceClient client, Grpc.Core.Metadata headers,
        NBitcoin.RPC.RPCClient rpc, LndTestClient alice, string bobPubKey)
    {
        var scid = await alice.ScidToAsync(bobPubKey, minCapacitySats: SourceMinCapacitySats);
        if (scid is null)
        {
            _output.WriteLine($"[setup] no Alice→Bob >= {SourceMinCapacitySats} sat — opening one through NodeGuard");
            var openedId = (int)await OpenChannelAndConfirmAsync(client, headers, rpc, alice.PubKey, bobPubKey);

            await using var db = CreateDbContext();
            var opened = await db.Channels.AsNoTracking().FirstAsync(c => c.Id == openedId);
            return (openedId, opened.ChanId);
        }

        // By scid, so it stays unambiguous with a second, smaller Alice→Bob around.
        return (await PollChannelIdByScidAsync(scid.Value), scid.Value);
    }

    /// <summary>
    /// Pins our outbound fee on every channel with the peer. Base fee 0 keeps the whole cost in the ppm the
    /// profitability gate can see.
    /// </summary>
    private async Task PinOutboundPolicyAsync(LndTestClient node, string peerPubKey)
    {
        foreach (var channel in await node.ChannelsToAsync(peerPubKey))
        {
            await node.UpdateOutboundPolicyAsync(channel.ChannelPoint, baseFeeMsat: 0, feeRatePpm: DestinationEarnPpm);
            _output.WriteLine($"[setup] pinned {node.Name}'s outbound policy on scid {channel.ChanId} to {DestinationEarnPpm} ppm");
        }
    }

    /// <summary>
    /// Drains the fullest shared channel each round until EVERY channel between the two holds at most
    /// <paramref name="targetRatio"/> on our side. Direct single-hop payments, so they cost nothing and —
    /// unlike a forward — leave the categorizer's flow windows untouched.
    /// </summary>
    private async Task DrainLocalBalanceAsync(LndTestClient from, LndTestClient to, double targetRatio, int maxPayments = 24)
    {
        for (var i = 0; i < maxPayments; i++)
        {
            var channels = (await from.ChannelsToAsync(to.PubKey))
                .Where(c => c.Active && c.ChanId != 0)
                .ToList();
            channels.Should().NotBeEmpty(
                $"{from.Name} needs at least one active channel with {to.Name} for the rebalance to have a destination");

            var fullest = channels.OrderByDescending(LocalRatio).First();
            var ratio = LocalRatio(fullest);
            _output.WriteLine(
                $"[drain] {from.Name}↔{to.Name}: {channels.Count} channel(s), fullest scid {fullest.ChanId} at ratio {ratio:0.###} (local {fullest.LocalBalance})");
            if (ratio <= targetRatio) return;

            var basis = fullest.LocalBalance + fullest.RemoteBalance;
            var overshoot = fullest.LocalBalance - (long)(targetRatio * basis);
            var amount = Math.Min(overshoot, DrainChunkSats);
            if (amount < MinDrainPaymentSats)
                throw new InvalidOperationException(
                    $"Cannot drain {from.Name}→{to.Name} on scid {fullest.ChanId} to {targetRatio}: " +
                    $"local {fullest.LocalBalance} leaves only {overshoot} sats to move");

            if (!await from.PayViaScidAsync(to, fullest.ChanId, amount))
            {
                _output.WriteLine($"[drain] {amount} sat payment over scid {fullest.ChanId} failed — retrying");
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        throw new InvalidOperationException(
            $"{from.Name}↔{to.Name} never dropped to a local ratio of {targetRatio} within {maxPayments} payments");
    }

    /// <summary>
    /// Everything the planner acts on, from ONE ListChannels snapshot and one routing-state query: the
    /// source channel's own row, and the destination peer aggregated the way
    /// <c>RebalanceInitiatorService.Classify</c> aggregates it (live balances as the weights, the managed
    /// node's own rows as the signal, across every channel with that peer). Null until every channel
    /// involved has a row.
    /// </summary>
    private static async Task<Signal?> ReadSignalAsync(
        LndTestClient node, int sourceChannelId, ulong sourceScid, string peerPubKey)
    {
        var channels = (await node.ChannelsAsync()).Where(c => c.Active && c.ChanId != 0).ToList();
        var source = channels.FirstOrDefault(c => c.ChanId == sourceScid);
        var peerChannels = channels.Where(c => c.RemotePubkey == peerPubKey).ToList();
        if (source == null || peerChannels.Count == 0) return null;

        Dictionary<ulong, (double Ema, double Target)> byScid;
        ChannelRoutingState? sourceState;
        await using (var db = CreateDbContext())
        {
            sourceState = await db.ChannelRoutingStates.AsNoTracking().FirstOrDefaultAsync(
                s => s.ChannelId == sourceChannelId && s.ManagedNodePubKey == node.PubKey);
            // Projected, but the scid subset is filtered client-side: this table holds a handful of rows
            // in the e2e stack, and a server-side Contains over the numeric-mapped scid is not a
            // translation the repositories rely on anywhere.
            byScid = (await db.ChannelRoutingStates.AsNoTracking()
                    .Where(s => s.ManagedNodePubKey == node.PubKey)
                    .Select(s => new { s.ChanIdLnd, s.EmaLocalRatio, s.TargetLocalRatio })
                    .ToListAsync())
                .GroupBy(s => s.ChanIdLnd)
                .ToDictionary(g => g.Key, g => (g.First().EmaLocalRatio, g.First().TargetLocalRatio));
        }
        if (sourceState == null) return null;

        double weightedEma = 0, weightedTarget = 0;
        long local = 0, basis = 0;
        foreach (var channel in peerChannels)
        {
            if (!byScid.TryGetValue(channel.ChanId, out var state)) return null;

            var channelBasis = channel.LocalBalance + channel.RemoteBalance;
            if (channelBasis <= 0) continue;

            local += channel.LocalBalance;
            basis += channelBasis;
            weightedEma += state.Ema * channelBasis;
            weightedTarget += state.Target * channelBasis;
        }

        if (basis <= 0) return null;

        return new Signal(
            Source: sourceState,
            SourceLocalSats: source.LocalBalance,
            SourceBaseSats: source.LocalBalance + source.RemoteBalance,
            Destination: new PeerSignal(weightedEma / basis, weightedTarget / basis, local, basis));
    }

    private static double LocalRatio(Lnrpc.Channel c)
        => (double)c.LocalBalance / Math.Max(1, c.LocalBalance + c.RemoteBalance);

    private record Signal(
        ChannelRoutingState Source, long SourceLocalSats, long SourceBaseSats, PeerSignal Destination);

    private record PeerSignal(double AggEma, double AggTarget, long LocalSats, long BaseSats)
    {
        public long DeficitSats => (long)Math.Round(AggTarget * BaseSats, MidpointRounding.AwayFromZero) - LocalSats;
    }
}
