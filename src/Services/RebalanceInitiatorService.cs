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

using NodeGuard.Helpers;

namespace NodeGuard.Services;

/// <summary>
/// One of our channels, with the live balances and the smoothed routing-engine signal the
/// rebalancer needs.
/// </summary>
public record ChannelSignal(
    int ChannelId,
    ulong ChanIdLnd,
    string PeerPubKey,
    long LocalSats,
    long RemoteSats,
    double EmaLocalRatio,
    double TargetLocalRatio,
    bool Active,
    bool SourceOptIn);

/// <summary>
/// A channel we can drain (send OUT of via <c>outgoing_chan_id</c>) — precise, per channel.
/// </summary>
public record SourceChannel(
    int ChannelId,
    ulong ChanIdLnd,
    string PeerPubKey,
    long ExcessSats);

/// <summary>One of our channels with a destination peer — carries the earn-rate weight (balance base).</summary>
public record PeerMemberChannel(ulong ChanIdLnd, long BalanceBaseSats);

/// <summary>
/// A peer we want to refill, aggregated across all our channels with it. LND's
/// <c>last_hop_pubkey</c> only constrains the peer, not the exact incoming channel, so the
/// destination side is modelled at peer granularity.
/// </summary>
public record DestinationPeer(
    string PeerPubKey,
    long DeficitSats,
    IReadOnlyList<PeerMemberChannel> Members);

/// <summary>
/// Output of <see cref="RebalanceInitiatorService.Classify"/>.
/// </summary>
public record RebalanceClassification(
    IReadOnlyList<SourceChannel> Sources,
    IReadOnlyList<DestinationPeer> Destinations,
    IReadOnlyList<SourceChannel> FallbackSources,
    IReadOnlyList<DestinationPeer> FallbackDestinations);

/// <summary>
/// A concrete rebalance the job should dispatch: drain <see cref="SourceChannelId"/>, refill via
/// last-hop <see cref="DestinationPeerPubKey"/>, sized and profit-gated.
/// </summary>
public record RebalancePlan(
    int SourceChannelId,
    string DestinationPeerPubKey,
    long AmountSats,
    double MaxFeePct,
    bool IsFallbackPairing,
    string Reason);

/// <summary>
/// Control tunables for <see cref="RebalanceInitiatorService"/>.
/// </summary>
public record RebalanceInitiatorTunables(
    double RebalanceTrigger,
    long MinAmountSats,
    long MaxAmountSats,
    double CostToEarnRatio,
    int MaxInitiations)
{
    /// <summary>
    /// Production wiring: global ROUTING_ENGINE_REBALANCE_* defaults, with the cost-to-earn ratio
    /// overridden per node. Lives on the record — as <see cref="FeeOptimizerTunables.FromConstants"/>
    /// does — so the pure module owns its own configuration and the job never names a constant.
    /// </summary>
    public static RebalanceInitiatorTunables FromConstants(Data.Models.Node node) => new(
        RebalanceTrigger: Constants.ROUTING_ENGINE_REBALANCE_DEADBAND,
        MinAmountSats: Constants.REBALANCE_MIN_AMOUNT_SATS,
        MaxAmountSats: Constants.ROUTING_ENGINE_REBALANCE_MAX_AMOUNT_SATS,
        CostToEarnRatio: node.MaxRebalanceCostToEarnRatio
                         ?? Constants.ROUTING_ENGINE_REBALANCE_DEFAULT_COST_TO_EARN_RATIO,
        MaxInitiations: Constants.ROUTING_ENGINE_REBALANCE_MAX_INITIATIONS_PER_RUN);
}

/// <summary>
/// Pure decision logic for the automated rebalancer — no I/O, no clock, no DB, so it is a static
/// function library (mirrors <see cref="FeeOptimizerService"/>).
/// <para>
/// A circular rebalance drains liquidity OUT of a too-local channel (precise: <c>outgoing_chan_id</c>)
/// and refills a too-remote one (fuzzy: <c>last_hop_pubkey</c> pins only the peer, not the channel).
/// </summary>
public static class RebalanceInitiatorService
{
    /// <summary>
    /// Splits <paramref name="channels"/> into drainable sources and refillable destination peers,
    /// using the smoothed EMA ratio for the direction decision and live balances for sizing. Whatever
    /// trips neither trigger lands in the fallback pools instead of being discarded.
    /// </summary>
    public static RebalanceClassification Classify(
        IReadOnlyList<ChannelSignal> channels,
        RebalanceInitiatorTunables t)
    {
        // Sources calculation
        var sources = new List<SourceChannel>();
        var fallbackSources = new List<SourceChannel>();
        foreach (var c in channels)
        {
            if (!c.Active || !c.SourceOptIn) continue;

            var baseSats = c.LocalSats + c.RemoteSats;
            if (baseSats <= 0) continue;

            var excess = c.LocalSats - SatsAt(c.TargetLocalRatio, baseSats);

            // Too-local by the smoothed signal
            if (c.EmaLocalRatio - c.TargetLocalRatio > t.RebalanceTrigger && excess > 0)
            {
                sources.Add(new SourceChannel(c.ChannelId, c.ChanIdLnd, c.PeerPubKey, excess));
                continue;
            }

            // Searching for fallback sources. Avoiding creating a next cycle rebalance by
            // lending liquidity down to the low edge of the deadband only
            var lendable = c.LocalSats - SatsAt(Math.Max(0, c.TargetLocalRatio - t.RebalanceTrigger), baseSats);
            if (lendable > 0 && lendable > t.MinAmountSats)
            {
                fallbackSources.Add(new SourceChannel(c.ChannelId, c.ChanIdLnd, c.PeerPubKey, lendable));
            }
        }

        // Destinations calculation
        var destinations = new List<DestinationPeer>();
        var fallbackDestinations = new List<DestinationPeer>();
        foreach (var group in channels.Where(c => c.Active).GroupBy(c => c.PeerPubKey))
        {
            long peerLocal = 0;
            long peerBase = 0;
            double weightedEma = 0;
            double weightedTarget = 0;
            var members = new List<PeerMemberChannel>();

            foreach (var c in group)
            {
                var baseSats = c.LocalSats + c.RemoteSats;
                if (baseSats <= 0) continue;

                peerLocal += c.LocalSats;
                peerBase += baseSats;
                weightedEma += c.EmaLocalRatio * baseSats;
                weightedTarget += c.TargetLocalRatio * baseSats;
                members.Add(new PeerMemberChannel(c.ChanIdLnd, baseSats));
            }

            if (peerBase <= 0) continue;

            var aggEma = weightedEma / peerBase;
            var aggTarget = weightedTarget / peerBase;

            // Sats of local needed to bring the peer aggregate back to target
            var targetLocalSats = (long)Math.Round(weightedTarget, MidpointRounding.AwayFromZero);
            var deficit = targetLocalSats - peerLocal;

            // Too-remote in aggregate (smoothed): the peer holds too little of our local
            if (aggEma - aggTarget < -t.RebalanceTrigger && deficit > 0)
            {
                destinations.Add(new DestinationPeer(group.Key, deficit, members));
                continue;
            }

            // Searching for fallback destinations. Avoiding creating a next cycle rebalance by
            // lending liquidity up to the high edge of the deadband only
            var absorbable = SatsAt(Math.Min(1.0, aggTarget + t.RebalanceTrigger), peerBase) - peerLocal;
            if (absorbable > 0 && absorbable > t.MinAmountSats)
            {
                fallbackDestinations.Add(new DestinationPeer(group.Key, absorbable, members));
            }
        }

        return new RebalanceClassification(sources, destinations, fallbackSources, fallbackDestinations);
    }

    /// <summary>
    /// Turns the classification into sized, profit-gated <see cref="RebalancePlan"/>s.
    /// <para>
    /// Pass 1 refills every detected destination from the first source still available,
    /// otherwise the fallback pool. Pass 2 then drains any detected source pass 1 didn't
    /// consume into the first fallback destination that fits.
    /// </para>
    /// </summary>
    public static IReadOnlyList<RebalancePlan> BuildPlans(
        RebalanceClassification classification,
        IReadOnlyDictionary<ulong, long> earnPpmByChanIdLnd,
        RebalanceInitiatorTunables t)
    {
        var plans = new List<RebalancePlan>();
        var usedSourceIds = new HashSet<int>();

        var sources = classification.Sources
            .OrderByDescending(s => s.ExcessSats)
            .ToList();
        var fallbackSources = classification.FallbackSources
            .OrderByDescending(s => s.ExcessSats)
            .ToList();
        var destinations = classification.Destinations
            .OrderByDescending(d => d.DeficitSats)
            .ToList();
        var fallbackDestinations = classification.FallbackDestinations
            .OrderByDescending(d => d.DeficitSats)
            .ToList();

        // Pass 1: refill every detected destination
        foreach (var dest in destinations)
        {
            if (plans.Count >= t.MaxInitiations) return plans;

            // No known earn rate ⇒ nothing to profit-gate against ⇒ leave the peer alone.
            var destEarnPpm = WeightedAverageEarnPpm(dest.Members, earnPpmByChanIdLnd);
            if (destEarnPpm == null) continue;

            // A channel that tripped the trigger first; failing that, borrow from the fallback pool
            // so the detected shortfall still gets funded.
            var source = FirstFreeSource(sources, dest, usedSourceIds);
            var isFallback = source == null;
            source ??= FirstFreeSource(fallbackSources, dest, usedSourceIds);
            if (source == null) continue;

            var plan = TryBuildPlan(source, dest, destEarnPpm.Value, earnPpmByChanIdLnd, t, isFallback);
            if (plan == null) continue;

            usedSourceIds.Add(source.ChannelId);
            plans.Add(plan);
        }

        // Pass 2: drain every detected source pass 1 left unused
        var gatedFallbackDestinations = fallbackDestinations
            .Select(d => (Dest: d, EarnPpm: WeightedAverageEarnPpm(d.Members, earnPpmByChanIdLnd)))
            .Where(x => x.EarnPpm.HasValue)
            .ToList();

        var refilledFallbackPeers = new HashSet<string>();

        foreach (var source in sources)
        {
            if (plans.Count >= t.MaxInitiations) return plans;
            if (usedSourceIds.Contains(source.ChannelId)) continue;

            foreach (var (dest, destEarnPpm) in gatedFallbackDestinations)
            {
                if (dest.PeerPubKey == source.PeerPubKey) continue;
                if (refilledFallbackPeers.Contains(dest.PeerPubKey)) continue;

                var plan = TryBuildPlan(source, dest, destEarnPpm!.Value, earnPpmByChanIdLnd, t,
                    isFallbackPairing: true);
                if (plan == null) continue;

                // Both marked only now, so a pairing the profit gate or the min-amount floor
                // rejected leaves the source and the peer available to everything downstream.
                usedSourceIds.Add(source.ChannelId);
                refilledFallbackPeers.Add(dest.PeerPubKey);
                plans.Add(plan);
                break;
            }
        }

        return plans;
    }

    /// <summary>
    /// Sats corresponding to <paramref name="ratio"/> of <paramref name="baseSats"/>.
    /// </summary>
    private static long SatsAt(double ratio, long baseSats)
        => (long)Math.Round(ratio * baseSats, MidpointRounding.AwayFromZero);

    private static SourceChannel? FirstFreeSource(
        IReadOnlyList<SourceChannel> pool,
        DestinationPeer dest,
        HashSet<int> usedSourceIds)
        => pool.FirstOrDefault(s => !usedSourceIds.Contains(s.ChannelId) && s.PeerPubKey != dest.PeerPubKey);

    /// <summary>
    /// Sizes and profit-gates one source→destination pairing. Returns null when the pairing can't
    /// pay for itself or is too small to be worth a hop.
    /// </summary>
    private static RebalancePlan? TryBuildPlan(
        SourceChannel source,
        DestinationPeer dest,
        long destEarnPpm,
        IReadOnlyDictionary<ulong, long> earnPpmByChanIdLnd,
        RebalanceInitiatorTunables t,
        bool isFallbackPairing)
    {
        // Profit gate: cost is capped at ratio × the earn rate of the destination we actually chose
        var maxCostPpm = (long)Math.Round(t.CostToEarnRatio * destEarnPpm, MidpointRounding.AwayFromZero);
        if (maxCostPpm < 1) return null;
        var maxFeePct = maxCostPpm / 10_000.0;

        // What the source can give, bounded by what the destination can take
        var raw = Math.Min(source.ExcessSats, dest.DeficitSats);
        var amount = Math.Min(raw, t.MaxAmountSats);

        var sourceEarn = earnPpmByChanIdLnd.TryGetValue(source.ChanIdLnd, out var se) ? se : (long?)null;
        var kind = isFallbackPairing ? "fallback " : string.Empty;
        return new RebalancePlan(
            source.ChannelId,
            dest.PeerPubKey,
            amount,
            maxFeePct,
            isFallbackPairing,
            $"{kind}drain chan {source.ChanIdLnd} (earn {sourceEarn?.ToString() ?? "?"}ppm) → refill peer {dest.PeerPubKey} " +
            $"(earn {destEarnPpm}ppm, capacity {dest.DeficitSats} sats); amount {amount} sats, maxCost {maxCostPpm}ppm ({maxFeePct:0.####}%)");
    }

    /// <summary>
    /// Balance-weighted average of the peer's channels' outbound ppm (weight = balance base),
    /// over the members whose earn rate is known. Null when none are known.
    /// </summary>
    private static long? WeightedAverageEarnPpm(
        IReadOnlyList<PeerMemberChannel> members,
        IReadOnlyDictionary<ulong, long> earnPpmByChanIdLnd)
    {
        double weightedSum = 0;
        long totalWeight = 0;
        foreach (var m in members)
        {
            if (!earnPpmByChanIdLnd.TryGetValue(m.ChanIdLnd, out var ppm)) continue;
            weightedSum += (double)ppm * m.BalanceBaseSats;
            totalWeight += m.BalanceBaseSats;
        }

        if (totalWeight <= 0) return null;
        return (long)Math.Round(weightedSum / totalWeight, MidpointRounding.AwayFromZero);
    }
}
