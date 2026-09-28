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

using System.ComponentModel.DataAnnotations;

namespace NodeGuard.Data.Models;

public enum AutoChannelOpenMode
{
    /// Write the recommendation and stop. A human promotes it.
    Recommendation = 0,

    /// Write the recommendation, promote it at the suggested capacity, approve under budget.
    Auto = 1
}

public enum ChannelOpenRecommendationStatus
{
    /// Awaiting a decision; refreshed in place while the demand persists.
    Open = 0,

    /// Turned into a <see cref="ChannelOperationRequest"/>.
    Promoted = 1,

    /// Rejected by an operator; suppresses the peer for the cooldown.
    Dismissed = 2,

    /// Not reproduced by the latest run. Re-opened in place if the demand returns.
    Expired = 3
}

    /// How a recommendation's channel size was derived.
public enum ChannelOpenSizingRegime
{
    /// Sized to the largest turned-away payment, because that beat the runway figure.
    Buffer = 0,

    /// Sized to buy days of runway, because the peer drains fast enough to need them.
    Draining = 1
}

/// Which clamp determined the final capacity, for operator review.
public enum ChannelOpenBindingClamp
{
    None = 0,
    WumboCeiling = 1,
    NodeMax = 2,
    WalletBalance = 3,
    Budget = 4
}

/// Kept distinct from <see cref="ChannelOperationRequest"/>: a request is a funding instruction
/// awaiting treasury, a recommendation is evidence an operator may promote or dismiss.
/// <para>
/// One row per (<see cref="NodeId"/>, <see cref="PeerPubKey"/>) — keyed by peer, not channel.
/// </para>
public class ChannelOpenRecommendation : Entity
{
    public int NodeId { get; set; }
    public Node? Node { get; set; }

    [MaxLength(66)]
    public string PeerPubKey { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? PeerAlias { get; set; }

    /// Sample scid, evidence for an operator and not an identity — the refusals are spread across
    /// <see cref="ChannelsInvolved"/> channels.
    public ulong OutgoingChannelId { get; set; }

    public int ChannelsInvolved { get; set; }

    #region Evidence: gate inputs
    public int Bursts { get; set; }

    /// Breadth of demand: how many distinct peers the refused payments arrived from.
    public int DistinctSourcePeers { get; set; }
    public long MissedFeeMsat { get; set; }

    /// Raw failure rows before burst collapsing. Displayed, never gated on.
    public int FailedAttempts { get; set; }
    #endregion Evidence: gate inputs

    #region Evidence: sizing inputs
    /// Settled, not attempted — the drain side of sizing.
    public long OutSats { get; set; }

    /// Settled, not attempted — the natural refill side of sizing.
    public long InSats { get; set; }

    /// Burst-deduped refused volume over the window.
    public long MissedSats { get; set; }

    public long MaxBurstPaymentSats { get; set; }

    public ChannelOpenSizingRegime Regime { get; set; }

    /// What the sizing rule computed. An operator may override it before promoting.
    public long SuggestedCapacitySats { get; set; }

    public ChannelOpenBindingClamp BindingClamp { get; set; }
    #endregion Evidence: sizing inputs

    #region Lifecycle
    public ChannelOpenRecommendationStatus Status { get; set; }

    public DateTimeOffset LastEvidenceAt { get; set; }

    public int? ChannelOperationRequestId { get; set; }
    public ChannelOperationRequest? ChannelOperationRequest { get; set; }

    [MaxLength(512)]
    public string? DismissReason { get; set; }
    #endregion Lifecycle
}
