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

    /// <summary>HTLC attempt index (0, 1, 2...) — a failed payment may retry over different routes.</summary>
    public int AttemptIndex { get; set; }

    /// <summary>Position of the hop within the route (0 = first hop from the origin).</summary>
    public int HopSequence { get; set; }

    /// <summary>Lightning channel id (uint64). Stored as ulong; LND encodes it as a JS string over the wire.</summary>
    public ulong ChannelId { get; set; }

    public string FromNode { get; set; } = string.Empty;
    public string ToNode { get; set; } = string.Empty;
    public long? AmountMsat { get; set; }

    public PaymentRoute? Payment { get; set; }
}

public enum PaymentRouteStatus
{
    Unknown = 0,
    Success = 1,
    Failed = 2
}
