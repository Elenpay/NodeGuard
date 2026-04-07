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

public class ForwardingHtlcEvent
{
    public string ManagedNodePubKey { get; set; } = string.Empty;

    [MaxLength(256)]
    public string ManagedNodeName { get; set; } = string.Empty;

    public ulong IncomingChannelId { get; set; }
    public ulong OutgoingChannelId { get; set; }
    public ulong IncomingHtlcId { get; set; }
    public ulong OutgoingHtlcId { get; set; }

    public DateTimeOffset CreationDatetime { get; set; }
    public DateTimeOffset UpdateDatetime { get; set; }
    public DateTimeOffset EventTimestamp { get; set; }

    public HtlcEventType EventType { get; set; }
    public HtlcEventCase EventCase { get; set; }
    public ForwardingOutcome Outcome { get; set; }

    public uint? IncomingTimelock { get; set; }
    public uint? OutgoingTimelock { get; set; }
    public ulong? IncomingAmountMsat { get; set; }
    public ulong? OutgoingAmountMsat { get; set; }

    [MaxLength(256)]
    public string? IncomingPeerAlias { get; set; }

    [MaxLength(256)]
    public string? OutgoingPeerAlias { get; set; }

    // Net forwarding fee reflected in the HTLC amounts. This already includes
    // inbound fee discounts/surcharges advertised through channel policy.
    public long? FeeMsat { get; set; }

    // Outbound forwarding fee before any inbound fee adjustment is applied.
    public long? GrossFeeMsat { get; set; }

    // Signed inbound fee contribution: negative for discounts, positive for surcharges.
    public long? InboundFeeMsat { get; set; }

    // Outbound routing fee rate snapshot from the monitored node's outgoing policy.
    public long? RoutingFeePpm { get; set; }

    // Inbound fee rate snapshot from the monitored node's incoming policy.
    public long? InboundFeePpm { get; set; }

    public int? WireFailureCode { get; set; }
    public int? FailureDetail { get; set; }

    [MaxLength(1024)]
    public string? FailureString { get; set; }
}

public enum HtlcEventType
{
    Unknown = 0,
    Send = 1,
    Receive = 2,
    Forward = 3
}

public enum HtlcEventCase
{
    /// <summary>
    /// Unknown/unspecified case.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// HTLC was forwarded onward by this node (in-flight progression).
    /// </summary>
    ForwardEvent = 1,

    /// <summary>
    /// HTLC that was forwarded by this node failed downstream.
    /// Terminal failed outcome.
    /// </summary>
    ForwardFailEvent = 2,

    /// <summary>
    /// HTLC settled (preimage revealed).
    /// Terminal settled outcome.
    /// </summary>
    SettleEvent = 3,

    /// <summary>
    /// HTLC failed on local incoming/outgoing link with explicit failure reason.
    /// Terminal failed outcome.
    /// </summary>
    LinkFailEvent = 4,

    /// <summary>
    /// Initial marker emitted when SubscribeHtlcEvents stream is established.
    /// Not an HTLC lifecycle event.
    /// </summary>
    SubscribedEvent = 5,

    /// <summary>
    /// Final outcome signal with settled/off-chain flags.
    /// Informational only for forwards; SettleEvent and fail events are the
    /// authoritative outcome signals used by HTLC monitoring.
    /// </summary>
    FinalHtlcEvent = 6
}

public enum ForwardingOutcome
{
    Unknown = 0,
    Settled = 1,
    Failed = 2
}