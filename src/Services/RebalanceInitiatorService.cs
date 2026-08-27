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

namespace NodeGuard.Services;

/// <summary>
/// One of our channels, with the live balances and the smoothed routing-engine signal the
/// rebalancer needs. Built by <see cref="Jobs.AutoRebalanceJob"/> from ListChannels +
/// ChannelRoutingState; the decision logic itself never touches LND or the DB.
/// </summary>
public record ChannelSignal(
    int ChannelId,
    ulong ChanIdLnd,
    string PeerPubKey,
    long CapacitySats,
    long LocalSats,
    long RemoteSats,
    double EmaLocalRatio,
    double TargetLocalRatio,
    bool Active,
    bool SourceOptIn);

/// <summary>
/// A channel we can drain (send OUT of via <c>outgoing_chan_id</c>) — precise, per channel.
/// <para>
/// <see cref="ExcessSats"/> is how much this channel may contribute, not simply how much it holds.
/// For a channel that tripped the trigger it is the excess over its own target. For a fallback
/// source (see <see cref="RebalanceClassification.FallbackSources"/>) it is the room down to the
/// low edge of its deadband, so lending liquidity can never turn it into next cycle's destination.
/// </para>
/// </summary>
public record SourceChannel(
    int ChannelId,
    ulong ChanIdLnd,
    string PeerPubKey,
    long ExcessSats,
    long LocalSats);

/// <summary>One of our channels with a destination peer — carries the earn-rate weight (balance base).</summary>
public record PeerMemberChannel(int ChannelId, ulong ChanIdLnd, long BalanceBaseSats);

/// <summary>
/// A peer we want to refill, aggregated across all our channels with it. LND's
/// <c>last_hop_pubkey</c> only constrains the peer, not the exact incoming channel, so the
/// destination side is modelled at peer granularity.
/// <para>
/// <see cref="DeficitSats"/> is how much this peer may absorb. For a peer that tripped the trigger
/// it is the shortfall against its own target. For a fallback destination (see
/// <see cref="RebalanceClassification.FallbackDestinations"/>) it is the room up to the high edge
/// of its deadband, so being refilled can never turn it into next cycle's source.
/// </para>
/// </summary>
public record DestinationPeer(
    string PeerPubKey,
    double AggregateEmaRatio,
    double AggregateTargetRatio,
    long DeficitSats,
    long RemoteSats,
    IReadOnlyList<PeerMemberChannel> Members);

/// <summary>
/// Output of <see cref="RebalanceInitiatorService.Classify"/>.
/// <para>
/// <see cref="Sources"/> and <see cref="Destinations"/> are the imbalances the engine actually
/// detected. The two fallback pools are everything else that is merely *able* to take part, and
/// exist so a detected imbalance is still acted on when nothing on the opposite side tripped the
/// trigger — a lone too-local source still gets drained somewhere, a lone depleted peer still gets
/// refilled from somewhere.
/// </para>
/// </summary>
public record RebalanceClassification(
    IReadOnlyList<SourceChannel> Sources,
    IReadOnlyList<DestinationPeer> Destinations,
    IReadOnlyList<SourceChannel> FallbackSources,
    IReadOnlyList<DestinationPeer> FallbackDestinations);

/// <summary>
/// A concrete rebalance the job should dispatch: drain <see cref="SourceChanIdLnd"/>, refill via
/// last-hop <see cref="DestinationPeerPubKey"/>, sized and profit-gated.
/// </summary>
public record RebalancePlan(
    int SourceChannelId,
    ulong SourceChanIdLnd,
    string DestinationPeerPubKey,
    long AmountSats,
    double MaxFeePct,
    bool IsFallbackPairing,
    string Reason);

/// <summary>
/// Control tunables for <see cref="RebalanceInitiatorService"/>. Passed in explicitly so the
/// decision logic stays pure and unit-testable; the job builds these from the node config +
/// ROUTING_ENGINE_REBALANCE_* constants.
/// </summary>
public record RebalanceInitiatorTunables(
    double RebalanceTrigger,
    long MinAmountSats,
    long MaxAmountSats,
    double CostToEarnRatio,
    int MaxInitiations);

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

            var targetLocal = (long)Math.Round(c.TargetLocalRatio * baseSats, MidpointRounding.AwayFromZero);
            var excess = c.LocalSats - targetLocal;

            // Too-local by the smoothed signal
            if (c.EmaLocalRatio - c.TargetLocalRatio > t.RebalanceTrigger && excess > 0)
            {
                sources.Add(new SourceChannel(c.ChannelId, c.ChanIdLnd, c.PeerPubKey, excess, c.LocalSats));
                continue;
            }

            // Searching for fallback sources. Avoiding creating a next cycle rebalance by
            // lending liquidity down to the low edge of the deadband only
            var floorRatio = Math.Max(0, c.TargetLocalRatio - t.RebalanceTrigger);
            var floorLocal = (long)Math.Round(floorRatio * baseSats, MidpointRounding.AwayFromZero);
            var lendable = c.LocalSats - floorLocal;
            if (lendable > 0)
            {
                fallbackSources.Add(new SourceChannel(c.ChannelId, c.ChanIdLnd, c.PeerPubKey, lendable, c.LocalSats));
            }
        }

        // Destinations calculation
        var destinations = new List<DestinationPeer>();
        var fallbackDestinations = new List<DestinationPeer>();
        foreach (var group in channels.Where(c => c.Active).GroupBy(c => c.PeerPubKey))
        {
            long peerLocal = 0;
            long peerRemote = 0;
            long peerBase = 0;
            double weightedEma = 0;
            double weightedTarget = 0;
            var members = new List<PeerMemberChannel>();

            foreach (var c in group)
            {
                var baseSats = c.LocalSats + c.RemoteSats;
                if (baseSats <= 0) continue;

                peerLocal += c.LocalSats;
                peerRemote += c.RemoteSats;
                peerBase += baseSats;
                weightedEma += c.EmaLocalRatio * baseSats;
                weightedTarget += c.TargetLocalRatio * baseSats;
                members.Add(new PeerMemberChannel(c.ChannelId, c.ChanIdLnd, baseSats));
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
                destinations.Add(new DestinationPeer(group.Key, aggEma, aggTarget, deficit, peerRemote, members));
                continue;
            }

            // Searching for fallback destinations. Avoiding creating a next cycle rebalance by
            // lending liquidity up to the high edge of the deadband only
            var ceilingRatio = Math.Min(1.0, aggTarget + t.RebalanceTrigger);
            var ceilingLocal = (long)Math.Round(ceilingRatio * peerBase, MidpointRounding.AwayFromZero);
            var absorbable = ceilingLocal - peerLocal;
            if (absorbable > 0)
            {
                fallbackDestinations.Add(
                    new DestinationPeer(group.Key, aggEma, aggTarget, absorbable, peerRemote, members));
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
        IReadOnlyDictionary<int, long> outboundEarnPpmByChannelId,
        RebalanceInitiatorTunables t)
    {
        var plans = new List<RebalancePlan>();
        var usedSourceIds = new HashSet<int>();

        // Only counterparties big enough to be worth a hop
        var sources = classification.Sources
            .Where(s => s.ExcessSats >= t.MinAmountSats)
            .ToList();
        var fallbackSources = classification.FallbackSources
            .Where(s => s.ExcessSats >= t.MinAmountSats)
            .ToList();

        // Pass 1: refill every detected destination
        foreach (var dest in classification.Destinations)
        {
            if (plans.Count >= t.MaxInitiations) return plans;

            // No known earn rate ⇒ nothing to profit-gate against ⇒ leave the peer alone.
            var destEarnPpm = WeightedAverageEarnPpm(dest.Members, outboundEarnPpmByChannelId);
            if (destEarnPpm == null) continue;

            // A channel that tripped the trigger first; failing that, borrow from the fallback pool
            // so the detected shortfall still gets funded.
            var source = FirstFreeSource(sources, dest, usedSourceIds);
            var isFallback = source == null;
            source ??= FirstFreeSource(fallbackSources, dest, usedSourceIds);
            if (source == null) continue;

            var plan = TryBuildPlan(source, dest, destEarnPpm.Value, outboundEarnPpmByChannelId, t, isFallback);
            if (plan == null) continue;

            usedSourceIds.Add(source.ChannelId);
            plans.Add(plan);
        }

        // Pass 2: drain every detected source pass 1 left unused
        var gatedFallbackDestinations = classification.FallbackDestinations
            .Select(d => (Dest: d, EarnPpm: WeightedAverageEarnPpm(d.Members, outboundEarnPpmByChannelId)))
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

                var plan = TryBuildPlan(source, dest, destEarnPpm!.Value, outboundEarnPpmByChannelId, t,
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
        IReadOnlyDictionary<int, long> outboundEarnPpmByChannelId,
        RebalanceInitiatorTunables t,
        bool isFallbackPairing)
    {
        // Profit gate: cost is capped at ratio × the earn rate of the destination we actually chose
        var maxCostPpm = (long)Math.Round(t.CostToEarnRatio * destEarnPpm, MidpointRounding.AwayFromZero);
        if (maxCostPpm < 1) return null;
        var maxFeePct = maxCostPpm / 10_000.0;

        // What the source can give, bounded by what the destination can take
        var raw = Math.Min(source.ExcessSats, dest.DeficitSats);
        if (raw < t.MinAmountSats) return null;
        var amount = Math.Min(raw, t.MaxAmountSats);

        var sourceEarn = outboundEarnPpmByChannelId.TryGetValue(source.ChannelId, out var se) ? se : (long?)null;
        var kind = isFallbackPairing ? "fallback " : string.Empty;
        return new RebalancePlan(
            source.ChannelId,
            source.ChanIdLnd,
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
        IReadOnlyDictionary<int, long> outboundEarnPpmByChannelId)
    {
        double weightedSum = 0;
        long totalWeight = 0;
        foreach (var m in members)
        {
            if (!outboundEarnPpmByChannelId.TryGetValue(m.ChannelId, out var ppm)) continue;
            weightedSum += (double)ppm * m.BalanceBaseSats;
            totalWeight += m.BalanceBaseSats;
        }

        if (totalWeight <= 0) return null;
        return (long)Math.Round(weightedSum / totalWeight, MidpointRounding.AwayFromZero);
    }
}
