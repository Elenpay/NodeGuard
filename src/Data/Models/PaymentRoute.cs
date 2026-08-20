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

/// <summary>
/// A Lightning payment originated (or attempted) by a managed node, tracked for
/// route visualisation. Port of LightningEye's SQLAlchemy <c>Payment</c> model.
/// A payment may have several HTLC attempts if it failed and was retried over
/// alternative routes; <see cref="Status"/> is the final outcome.
/// </summary>
public class PaymentRoute
{
    /// <summary>payment_hash hex (64 chars), used as the natural primary key.</summary>
    [Key]
    [MaxLength(64)]
    public string PaymentHash { get; set; } = string.Empty;

    /// <summary>Pubkey of the managed node that originated the payment (graph ORIGIN).</summary>
    public string OriginNodePubKey { get; set; } = string.Empty;

    public PaymentRouteStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long? AmountMsat { get; set; }

    /// <summary>Final destination node pubkey.</summary>
    public string? Destination { get; set; }

    public DateTimeOffset CreationDatetime { get; set; }
    public DateTimeOffset UpdateDatetime { get; set; }

    public List<PaymentRouteHop> Hops { get; set; } = new();
}

/// <summary>
/// A single hop within a payment's route. Port of LightningEye's <c>Hop</c> model.
/// One payment may have several attempts (<see cref="AttemptIndex"/>) with distinct routes.
/// </summary>
public class PaymentRouteHop
{
    [Key]
    public int Id { get; set; }

    [MaxLength(64)]
    public string PaymentHash { get; set; } = string.Empty;

    /// <summary>
    /// HTLC attempt ordinal within this payment (0, 1, 2...) — a payment may retry over
    /// different routes. This is the attempt's <b>position</b> in LND's <c>htlcs</c> list,
    /// NOT <c>HTLCAttempt.attempt_id</c>: the latter is a node-global uint64 sequence, so
    /// storing it here would render as "attempt 4021" in the UI's per-attempt trace.
    /// </summary>
    public int AttemptIndex { get; set; }

    /// <summary>Position of the hop within the route (0 = first hop from the origin).</summary>
    public int HopSequence { get; set; }

    /// <summary>Lightning channel id (uint64). Stored as ulong; LND encodes it as a JS string over the wire.</summary>
    public ulong ChannelId { get; set; }

    public string FromNode { get; set; } = string.Empty;
    public string ToNode { get; set; } = string.Empty;
    public long? AmountMsat { get; set; }

    /// <summary>
    /// Outcome of the HTLC attempt this hop belongs to. Denormalised onto every hop of the
    /// attempt (LND reports it per attempt, not per hop) so the graph can colour a failed
    /// attempt of an ultimately-successful payment correctly.
    /// </summary>
    public PaymentRouteAttemptStatus AttemptStatus { get; set; }

    /// <summary>
    /// LND's <c>Failure.failure_source_index</c> for this attempt: the position in the route
    /// of the node that returned the failure, where position 0 is the sender. Null when the
    /// attempt did not fail or LND reported no failure detail.
    /// </summary>
    public int? FailureSourceIndex { get; set; }

    /// <summary>
    /// LND's <c>Failure.code</c> in its wire spelling (e.g. <c>TEMPORARY_CHANNEL_FAILURE</c>).
    /// Surfaced verbatim by the frontend as the failure chip on the attempt trace.
    /// </summary>
    [MaxLength(64)]
    public string? FailureCode { get; set; }

    public PaymentRoute? Payment { get; set; }
}

/// <summary>
/// Outcome of a single HTLC attempt, mirroring LND's <c>HTLCAttempt.HTLCStatus</c>.
/// </summary>
public enum PaymentRouteAttemptStatus
{
    /// <summary>
    /// Applies to rows written before per-attempt data
    /// was persisted; the graph falls back to payment-level status for these, preserving the
    /// rendering they had before. Never produced by the tracker for new rows.
    /// </summary>
    Unknown = 0,
    InFlight = 1,
    Succeeded = 2,
    Failed = 3
}

public enum PaymentRouteStatus
{
    Unknown = 0,
    Success = 1,
    Failed = 2
}
