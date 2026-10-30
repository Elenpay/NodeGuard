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

using NodeGuard.Data.Models;
using NodeGuard.Helpers;

namespace NodeGuard.Services;

/// <summary>
/// Everything the planner knows about one peer, aggregated over all our open channels to it. LND
/// forwards non-strictly and reports whichever channel it tried last, so the peer is the identity.
/// </summary>
public record PeerSnapshot(
    string PeerPubKey,
    long EarnedFeeSats,
    long SettledOutSats,
    int Refusals,
    long UsableLocalSats,
    long CapacitySats,
    LocalOutboundPolicy? OutboundPolicy,
    bool OnCooldown);

public record ChannelOpenInitiatorTunables(
    long MinEarnedFeeSats,
    int MinRefusals,
    int LowOutboundPct,
    long SizeSats,
    long WalletSpendableSats,
    long RemainingBudgetSats,
    int MaxPlansPerRun)
{
    public static ChannelOpenInitiatorTunables FromConstants(
        Node node,
        long walletSpendableSats,
        long remainingBudgetSats) => new(
        MinEarnedFeeSats: Constants.AUTO_CHANNEL_OPEN_MIN_EARNED_FEE_SATS,
        MinRefusals: Constants.AUTO_CHANNEL_OPEN_MIN_REFUSALS,
        LowOutboundPct: Constants.AUTO_CHANNEL_OPEN_LOW_OUTBOUND_PCT,
        SizeSats: node.AutoChannelOpenSizeSats ?? 0,
        WalletSpendableSats: walletSpendableSats,
        RemainingBudgetSats: remainingBudgetSats,
        MaxPlansPerRun: Constants.AUTO_CHANNEL_OPEN_MAX_PLANS_PER_RUN);
}

public record ChannelOpenRejection(string PeerPubKey, string Reason);

public record ChannelOpenClassification(
    IReadOnlyList<ChannelOpenRecommendation> Plans,
    IReadOnlyList<ChannelOpenRejection> Rejected);

/// <summary>
/// Channel-open planner. Pure, no I/O. A peer gets a fixed-size channel when the route to it has
/// earned us fees, it refused forwards recently for lack of outbound, and it is still short now.
/// </summary>
public static class ChannelOpenInitiatorService
{
    public static ChannelOpenClassification Evaluate(
        IReadOnlyList<PeerSnapshot> peers,
        ChannelOpenInitiatorTunables t)
    {
        var plans = new List<ChannelOpenRecommendation>();
        var rejected = new List<ChannelOpenRejection>();
        var now = DateTimeOffset.UtcNow;

        foreach (var peer in peers.OrderByDescending(x => x.EarnedFeeSats))
        {
            var reason = Reject(peer, t);
            if (reason != null)
            {
                rejected.Add(new ChannelOpenRejection(peer.PeerPubKey, reason));
                continue;
            }

            if (plans.Count >= t.MaxPlansPerRun)
            {
                rejected.Add(new ChannelOpenRejection(peer.PeerPubKey,
                    $"qualifies, but the run already has {t.MaxPlansPerRun} plan(s)"));
                continue;
            }

            plans.Add(new ChannelOpenRecommendation
            {
                PeerPubKey = peer.PeerPubKey,
                EarnedFeeSats = peer.EarnedFeeSats,
                SettledOutSats = peer.SettledOutSats,
                Refusals = peer.Refusals,
                UsableLocalSats = peer.UsableLocalSats,
                PeerCapacitySats = peer.CapacitySats,
                SuggestedCapacitySats = t.SizeSats,
                InheritedFeeRatePpm = peer.OutboundPolicy!.FeeRatePpm,
                InheritedBaseFeeMsat = peer.OutboundPolicy.BaseFeeMsat,
                Status = ChannelOpenRecommendationStatus.Open,
                LastEvidenceAt = now
            });
        }

        return new ChannelOpenClassification(plans, rejected);
    }

    private static string? Reject(PeerSnapshot peer, ChannelOpenInitiatorTunables t)
    {
        if (peer.OnCooldown)
        {
            return "on cooldown after a recent promotion or dismissal";
        }

        // Without it the channel can't be priced: at the global default it would undercut what we
        // already charge this peer.
        if (peer.OutboundPolicy == null)
        {
            return "no outbound fee policy reported for any channel to the peer";
        }

        if (peer.EarnedFeeSats < t.MinEarnedFeeSats)
        {
            return $"earned {peer.EarnedFeeSats} sats, below the {t.MinEarnedFeeSats} sats minimum";
        }

        if (peer.Refusals < t.MinRefusals)
        {
            return $"{peer.Refusals} refused forward(s), below the {t.MinRefusals} minimum";
        }

        // The refusals are history; a rebalance or a settled HTLC may have refilled the peer since.
        if (peer.UsableLocalSats * 100 >= peer.CapacitySats * t.LowOutboundPct)
        {
            return $"{peer.UsableLocalSats} of {peer.CapacitySats} sats usable outbound, not below {t.LowOutboundPct}%";
        }

        if (t.SizeSats <= 0)
        {
            return "the node has no channel size configured";
        }

        if (t.SizeSats > t.WalletSpendableSats)
        {
            return $"the funding wallet can fund at most {t.WalletSpendableSats} sats, below the {t.SizeSats} sats size";
        }

        if (t.SizeSats > t.RemainingBudgetSats)
        {
            return $"{t.RemainingBudgetSats} sats of budget left, below the {t.SizeSats} sats size";
        }

        return null;
    }
}
