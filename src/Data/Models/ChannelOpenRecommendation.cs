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

    /// Write the recommendation and promote it at the node's fixed size, under budget.
    Auto = 1
}

public enum ChannelOpenRecommendationStatus
{
    /// Awaiting a decision; refreshed in place while the peer still qualifies.
    Open = 0,

    /// Turned into a <see cref="ChannelOperationRequest"/>.
    Promoted = 1,

    /// Rejected by an operator; suppresses the peer for the cooldown.
    Dismissed = 2,

    /// Not reproduced by the latest run. Re-opened in place if the peer qualifies again.
    Expired = 3,

    /// Promoted, but the channel never opened — the request failed or was cancelled. Unlike a
    /// dismissal this says nothing about the peer, so it leaves no cooldown behind.
    Failed = 4
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

    #region Evidence
    /// Fees earned on settled forwards out to the peer over the lookback window.
    public long EarnedFeeSats { get; set; }

    /// Settled volume out to the peer over the lookback window. Displayed, never gated on.
    public long SettledOutSats { get; set; }

    /// Forwards refused for lack of outbound to the peer over the shortage window.
    public int Refusals { get; set; }

    /// Usable outbound to the peer when the run saw it, reserve excluded.
    public long UsableLocalSats { get; set; }

    /// Total capacity of our open channels to the peer.
    public long PeerCapacitySats { get; set; }

    /// The node's fixed size at the time of the run. An operator may override it before promoting.
    public long SuggestedCapacitySats { get; set; }

    /// The policy the channel opens at: the highest-rate one among our channels to the peer.
    public long? InheritedFeeRatePpm { get; set; }

    public long? InheritedBaseFeeMsat { get; set; }
    #endregion Evidence

    #region Lifecycle
    public ChannelOpenRecommendationStatus Status { get; set; }

    public DateTimeOffset LastEvidenceAt { get; set; }

    public int? ChannelOperationRequestId { get; set; }
    public ChannelOperationRequest? ChannelOperationRequest { get; set; }

    [MaxLength(512)]
    public string? DismissReason { get; set; }
    #endregion Lifecycle
}
