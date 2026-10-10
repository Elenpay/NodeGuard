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

using NBitcoin;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;

namespace NodeGuard.Services;

/// <summary>
/// LND records the scid of whichever channel it tried last, so the raw event's channel is not a
/// stable identity — the peer is.
/// </summary>
public record PeerDemandFailure(
    string PeerPubKey,
    ulong OutgoingChannelId,
    DateTimeOffset EventTimestamp,
    ulong? OutgoingAmountMsat,
    long? RoutingFeePpm,
    long? FeeMsat);

public record PeerDemandContext(
    string PeerPubKey,
    string? PeerAlias,
    long UsableLocalSats,
    long SettledOutSats,
    long SettledInSats,
    LocalOutboundPolicy? CurrentOutboundPolicy,
    bool PeerOnCooldown);

public record ChannelOpenInitiatorTunables(
    int WindowHours,
    int BurstGapSeconds,
    int MinBursts,
    long MinMissedFeeMsat,
    double BufferMultiplier,
    int TargetRunwayDays,
    double MaxCostToEarnRatio,
    long NonWumboMaxSats,
    bool AllowWumbo,
    long NodeMinSizeSats,
    long NodeMaxSizeSats,
    long WalletBalanceSats,
    long RemainingBudgetSats,
    long EstimatedChainCostSats,
    int MaxPlansPerRun)
{
    public static ChannelOpenInitiatorTunables FromConstants(
        Node node,
        long walletBalanceSats,
        long remainingBudgetSats,
        long estimatedChainCostSats) => new(
        WindowHours: Constants.AUTO_CHANNEL_OPEN_WINDOW_HOURS,
        BurstGapSeconds: Constants.AUTO_CHANNEL_OPEN_BURST_GAP_SECONDS,
        MinBursts: Constants.AUTO_CHANNEL_OPEN_MIN_BURSTS,
        MinMissedFeeMsat: Constants.AUTO_CHANNEL_OPEN_MIN_MISSED_FEE_MSAT,
        BufferMultiplier: Constants.AUTO_CHANNEL_OPEN_BUFFER_MULTIPLIER,
        TargetRunwayDays: Constants.AUTO_CHANNEL_OPEN_TARGET_RUNWAY_DAYS,
        MaxCostToEarnRatio: node.MaxChannelOpenCostToEarnRatio
                            ?? Constants.AUTO_CHANNEL_OPEN_DEFAULT_COST_TO_EARN_RATIO,
        NonWumboMaxSats: Constants.MAXIMUM_CHANNEL_CAPACITY_SATS_REGTEST,
        AllowWumbo: CurrentNetworkHelper.GetCurrentNetwork() != Network.RegTest, 
        NodeMinSizeSats: node.AutoChannelOpenMinSizeSats ?? 0,
        NodeMaxSizeSats: node.AutoChannelOpenMaxSizeSats ?? long.MaxValue,
        WalletBalanceSats: walletBalanceSats,
        RemainingBudgetSats: remainingBudgetSats,
        EstimatedChainCostSats: estimatedChainCostSats,
        MaxPlansPerRun: Constants.AUTO_CHANNEL_OPEN_MAX_PLANS_PER_RUN);
}

/// <summary>
/// One payment attempt sequence: MPP shards and fast retries of the same payment, collapsed.
/// </summary>
public record DemandBurst(long PaymentSizeSats, long MissedFeeMsat);

public record ChannelOpenRejection(string PeerPubKey, string Reason);

public record ChannelOpenClassification(
    IReadOnlyList<ChannelOpenRecommendation> Plans,
    IReadOnlyList<ChannelOpenRejection> Rejected);

/// <summary>
/// Demand-driven channel-open planner. Pure, no I/O.
/// <para>
/// Keyed by <b>peer</b>, never by channel: LND's non-strict forwarding retries an HTLC across every
/// channel to a peer and reports only the last one tried, so a node would divide its own demand
/// evidence by roughly the number of channels it already has to that peer.
/// </para>
/// </summary>
public static class ChannelOpenInitiatorService
{
    /// <summary>
    /// LND's channel reserve is unusable for routing, so capacity must be inflated by it or the
    /// delivered outbound systematically undershoots the target.
    /// </summary>
    private const double ReserveFraction = 0.01;

    public static ChannelOpenClassification BuildPlans(
        ILookup<string, PeerDemandFailure> failuresByPeer,
        IReadOnlyDictionary<string, PeerDemandContext> contexts,
        ChannelOpenInitiatorTunables t)
    {
        var plans = new List<ChannelOpenRecommendation>();
        var rejected = new List<ChannelOpenRejection>();

        foreach (var group in failuresByPeer)
        {
            var peerPubKey = group.Key;

            if (!contexts.TryGetValue(peerPubKey, out var context))
            {
                rejected.Add(new ChannelOpenRejection(peerPubKey, "No live context for the peer — no open channels to it"));
                continue;
            }

            if (context.PeerOnCooldown)
            {
                rejected.Add(new ChannelOpenRejection(peerPubKey, "Peer is within the open cooldown"));
                continue;
            }

            if (context.CurrentOutboundPolicy is not { FeeRatePpm: > 0 } outboundPolicy)
            {
                rejected.Add(new ChannelOpenRejection(peerPubKey,
                    "LND reports no outbound policy for this peer, so the capacity can be neither priced nor opened at a sane rate"));
                continue;
            }

            var rows = group.OrderBy(x => x.EventTimestamp).ToList();
            var bursts = CollapseBursts(rows, t.BurstGapSeconds, outboundPolicy.FeeRatePpm);

            var missedFeeMsat = bursts.Sum(b => b.MissedFeeMsat);

            if (bursts.Count < t.MinBursts)
            {
                rejected.Add(new ChannelOpenRejection(peerPubKey, $"Only {bursts.Count} burst(s), need {t.MinBursts} — a payment problem, not a route problem"));
                continue;
            }

            if (missedFeeMsat < t.MinMissedFeeMsat)
            {
                rejected.Add(new ChannelOpenRejection(peerPubKey, $"Missed fee {missedFeeMsat} msat below {t.MinMissedFeeMsat}"));
                continue;
            }

            var missedSats = bursts.Sum(b => b.PaymentSizeSats);
            var maxBurstSats = bursts.Max(b => b.PaymentSizeSats);

            // Historical failures may be stale, so check current balance against the largest refused payment plus a buffer.
            var coverageFloor = (long)(maxBurstSats * t.BufferMultiplier);
            if (context.UsableLocalSats >= coverageFloor)
            {
                rejected.Add(new ChannelOpenRejection(peerPubKey, $"{context.UsableLocalSats} sats usable local already covers the largest refused payment of {maxBurstSats} sats ({t.BufferMultiplier:F1}x = {coverageFloor}) — not currently constrained"));
                continue;
            }

            // The captured rate informs profitability and is saved for promotion, avoiding another LND query.
            var earnPpm = outboundPolicy.FeeRatePpm;

            var (capacity, regime, clamp, sizingError) = Size(
                context.SettledOutSats, context.SettledInSats, missedSats, maxBurstSats, earnPpm, t);

            if (sizingError != null)
            {
                rejected.Add(new ChannelOpenRejection(peerPubKey, sizingError));
                continue;
            }

            plans.Add(new ChannelOpenRecommendation
            {
                PeerPubKey = peerPubKey,
                PeerAlias = context.PeerAlias,
                // Evidence only — whichever channel refused most often.
                OutgoingChannelId = rows.GroupBy(x => x.OutgoingChannelId)
                    .OrderByDescending(g => g.Count()).First().Key,
                ChannelsInvolved = rows.Select(x => x.OutgoingChannelId).Distinct().Count(),
                Bursts = bursts.Count,
                MissedFeeMsat = missedFeeMsat,
                FailedAttempts = rows.Count,
                OutSats = context.SettledOutSats,
                InSats = context.SettledInSats,
                MissedSats = missedSats,
                MaxBurstPaymentSats = maxBurstSats,
                Regime = regime,
                SuggestedCapacitySats = capacity,
                BindingClamp = clamp,
                InheritedFeeRatePpm = earnPpm,
                InheritedBaseFeeMsat = outboundPolicy.BaseFeeMsat,
                Status = ChannelOpenRecommendationStatus.Open,
                LastEvidenceAt = rows[^1].EventTimestamp
            });
        }

        var ordered = plans
            .OrderByDescending(x => x.MissedFeeMsat)
            .Take(Math.Max(0, t.MaxPlansPerRun))
            .ToList();

        return new ChannelOpenClassification(ordered, rejected);
    }

    /// <summary>
    /// The forward HTLC stream carries no payment hash, so MPP shards and retries of one payment can
    /// only be separated by time: a gap wider than <paramref name="gapSeconds"/> starts a new burst.
    /// </summary>
    internal static IReadOnlyList<DemandBurst> CollapseBursts(
        IReadOnlyList<PeerDemandFailure> orderedRows,
        int gapSeconds,
        long? fallbackPpm)
    {
        var groups = new List<List<PeerDemandFailure>>();

        foreach (var row in orderedRows)
        {
            if (groups.Count == 0 ||
                (row.EventTimestamp - groups[^1][^1].EventTimestamp).TotalSeconds > gapSeconds)
            {
                groups.Add(new List<PeerDemandFailure>());
            }

            groups[^1].Add(row);
        }

        return groups.Select(g =>
        {
            // Distinct amounts only: identical retries collapse, genuine MPP shards survive.
            var byAmount = g.GroupBy(x => x.OutgoingAmountMsat ?? 0).ToList();
            var sizeMsat = byAmount.Aggregate(0UL, (acc, x) => acc + x.Key);

            var ppm = (ulong)Math.Max(0, g.Max(x => x.RoutingFeePpm ?? fallbackPpm ?? 0));

            // Prefer the fee the sender actually offered (base + rate + inbound); estimate from ppm only when it's missing.
            // Multiply before dividing: dividing msat by 1e6 first truncates small payments to zero fee.
            var missedFeeMsat = byAmount.Sum(a =>
                a.Max(x => x.FeeMsat) is { } offered
                    ? Math.Max(0, offered)
                    : (long)(a.Key * ppm / 1_000_000));

            return new DemandBurst((long)(sizeMsat / 1000), missedFeeMsat);
        }).ToList();
    }

    /// <summary>
    /// Two regimes: a buffer for the largest payment we turned away, or runway for a peer that drains
    /// structurally. The larger wins and names the regime. The last clamp to lower the number is
    /// reported so an operator can see what bound it.
    /// </summary>
    /// <param name="earnPpm">The rate the channel will charge once open — the most we already charge this peer.</param>
    internal static (long Capacity, ChannelOpenSizingRegime Regime, ChannelOpenBindingClamp Clamp, string? Error) Size(
        long outSats,
        long inSats,
        long missedSats,
        long maxBurstPaymentSats,
        long earnPpm,
        ChannelOpenInitiatorTunables t)
    {
        var windowDays = Math.Max(t.WindowHours / 24.0, 1.0 / 24.0);
        var drainPerDay = (outSats + missedSats) / windowDays;
        var refillPerDay = inSats / windowDays;
        var netDrainPerDay = Math.Max(0, drainPerDay - refillPerDay);

        var paymentBufferFloorSats = (long)(maxBurstPaymentSats * t.BufferMultiplier);
        var runway = (long)(netDrainPerDay * t.TargetRunwayDays);

        var target = Math.Max(runway, paymentBufferFloorSats);
        var regime = runway > paymentBufferFloorSats
            ? ChannelOpenSizingRegime.Draining
            : ChannelOpenSizingRegime.Buffer;

        // Reserve is unusable for routing, so inflate rather than clamp — this is not a binding limit.
        target = (long)(target / (1 - ReserveFraction));

        var clamp = ChannelOpenBindingClamp.None;

        if (!t.AllowWumbo && target > t.NonWumboMaxSats)
        {
            target = t.NonWumboMaxSats;
            clamp = ChannelOpenBindingClamp.WumboCeiling;
        }

        if (target > t.NodeMaxSizeSats)
        {
            target = t.NodeMaxSizeSats;
            clamp = ChannelOpenBindingClamp.NodeMax;
        }

        if (target > t.WalletBalanceSats)
        {
            target = t.WalletBalanceSats;
            clamp = ChannelOpenBindingClamp.WalletBalance;
        }

        if (target > t.RemainingBudgetSats)
        {
            target = t.RemainingBudgetSats;
            clamp = ChannelOpenBindingClamp.Budget;
        }

        // One full drain is the natural unit of work: the channel earns its rate on everything it
        // forwards before running dry. Chain cost is one-off, so this asks what that first cycle buys.
        var drainableSats = (long)(target * (1 - ReserveFraction));
        var profitPerDrainSats = drainableSats * earnPpm / 1_000_000;

        if (profitPerDrainSats <= 0)
        {
            return (0, regime, clamp, $"A {target} sats channel at {earnPpm} ppm earns nothing over a full drain, so no on-chain cost can be justified");
        }

        var costToEarn = (double)t.EstimatedChainCostSats / profitPerDrainSats;
        if (costToEarn > t.MaxCostToEarnRatio)
        {
            // Blaming the feerate would send an operator to the mempool when the real bound is their
            // own wallet or budget, which is the thing they can act on.
            var cause = clamp == ChannelOpenBindingClamp.None
                ? "the channel is too small to earn back what it costs to open"
                : $"the {clamp} clamp cut it below what the on-chain cost justifies";

            return (0, regime, clamp, $"Draining {target} sats at {earnPpm} ppm earns {profitPerDrainSats} sats against {t.EstimatedChainCostSats} sats of chain cost, a cost-to-earn of {costToEarn:F2} over the {t.MaxCostToEarnRatio:F2} limit — {cause}");
        }

        if (target < t.NodeMinSizeSats)
        {
            return (0, regime, clamp, $"Size {target} sats below the node minimum of {t.NodeMinSizeSats} sats");
        }

        if (target <= 0)
        {
            return (0, regime, clamp, "No capacity available after clamps");
        }

        return (target, regime, clamp, null);
    }
}
