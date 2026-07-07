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

namespace NodeGuard.Data.Models;

/// <summary>
/// Flow-based categorization of a channel's peer, derived from settled forwarding history.
/// See the flow-sign convention: push = forwarded OUT (drains our local), pull = forwarded IN
/// (fills our local), NetFlowRatio = (push - pull) / (push + pull).
/// </summary>
public enum PeerFlowCategory
{
    Uncategorized = 0,

    /// <summary>Push-heavy (NetFlowRatio > 0): the peer drains our local balance. Hold more local, higher fees.</summary>
    Sink = 1,

    /// <summary>Pull-heavy (NetFlowRatio < 0): the peer fills our local balance. Hold less local, lower fees.</summary>
    Source = 2,

    /// <summary>Balanced flow: target ratio held near 0.5.</summary>
    Bidirectional = 3
}

/// <summary>
/// Per-channel routing-engine read model (1:1 with <see cref="Channel"/>). Written by
/// TargetRatioReevaluationJob; read by the Phase 2 fee engine and Phase 3 rebalancer.
/// This is the single canonical place target ratio / category / smoothed balance live —
/// actuators must not re-derive them.
/// </summary>
public class ChannelRoutingState : Entity
{
    /// <summary>FK to <see cref="Channel"/> (unique — one routing state per channel).</summary>
    public int ChannelId { get; set; }
    public Channel Channel { get; set; } = null!;

    /// <summary>LND short-channel-id snapshot, refreshed every evaluation (alias -&gt; confirmed scid).</summary>
    public ulong ChanIdLnd { get; set; }

    /// <summary>66-hex pubkey of the managed node that owns routing state for this channel.</summary>
    public string ManagedNodePubKey { get; set; } = null!;

    /// <summary>Dynamic target local-balance ratio, clamped to [0.10, 0.90]. Defaults to 0.5.</summary>
    public double TargetLocalRatio { get; set; } = 0.5;

    public PeerFlowCategory PeerFlowCategory { get; set; } = PeerFlowCategory.Uncategorized;

    /// <summary>
    /// Tentative category currently being counted toward a hysteresis flip; null in steady state.
    /// </summary>
    public PeerFlowCategory? PendingCategory { get; set; }

    /// <summary>Consecutive cycles the <see cref="PendingCategory"/> has been observed; 0 in steady state.</summary>
    public uint ConsecutiveCategoryCyclesInNewState { get; set; }

    /// <summary>Funding block height parsed from the scid (bits 63..40); null for pending/alias/zero-conf.</summary>
    public uint? FundingBlockHeight { get; set; }

    /// <summary>Channel age in blocks (chainTip - FundingBlockHeight); null when height can't be derived.</summary>
    public uint? AgeBlocks { get; set; }

    /// <summary>EMA of local/(local+remote); seeded with the first observed ratio on insert.</summary>
    public double EmaLocalRatio { get; set; }

    /// <summary>Σ settled msat leaving us via this channel over the categorization window (drains local).</summary>
    public long PushMsatWindow { get; set; }

    /// <summary>Σ settled msat arriving at us via this channel over the same window (fills local).</summary>
    public long PullMsatWindow { get; set; }

    /// <summary>(push - pull) / (push + pull); positive = SINK, negative = SOURCE.</summary>
    public double NetFlowRatio { get; set; }

    /// <summary>== !channel.Initiator — true when the peer opened the channel.</summary>
    public bool PeerInitiated { get; set; }

    public long? LastKnownNumUpdates { get; set; }

    /// <summary>Seconds; EXPERIMENTAL per LND.</summary>
    public long? LastKnownLifetime { get; set; }

    /// <summary>Seconds; EXPERIMENTAL per LND, resets on restart.</summary>
    public long? LastKnownUptime { get; set; }

    /// <summary>Set when a category flip commits.</summary>
    public DateTimeOffset? LastCategorizedAt { get; set; }

    public DateTimeOffset LastEvaluatedAt { get; set; }
}
