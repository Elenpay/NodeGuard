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

using System.ComponentModel.DataAnnotations.Schema;
using NBitcoin;

namespace NodeGuard.Data.Models;

public enum RebalanceStatus
{
    Pending = 0,
    // 1 was Probing — removed (rebalances go straight to SendPaymentV2, no probe). The value is
    // intentionally left as a gap so the other statuses keep their stored int values.
    InFlight = 2,
    Succeeded = 3,
    Failed = 4,
    NoRoute = 5,
    Timeout = 6,
    InsufficientBalance = 7,
    ExceededFeeLimit = 8
}

public class Rebalance : Entity
{
    public int NodeId { get; set; }
    public Node? Node { get; set; }

    /// <summary>
    /// Pubkey of the source node, stored for simpler analytics
    /// </summary>
    public string? SourceNodePubKey { get; set; }

    public RebalanceStatus Status { get; set; }

    public bool IsManual { get; set; }

    public int AttemptNumber { get; set; } = 1;

    /// <summary>
    /// The amount the user originally requested to rebalance.
    /// </summary>
    public long RequestedAmountSats { get; set; }

    /// <summary>
    /// The amount actually used for the payment. May be lower than RequestedAmountSats
    /// when the prober had to reduce the amount to find a viable route.
    /// </summary>
    public long SatsAmount { get; set; }

    [NotMapped]
    public decimal Amount => new Money(SatsAmount, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);

    [NotMapped]
    public decimal RequestedAmount => new Money(RequestedAmountSats, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);

    /// <summary>
    /// Maximum fee as percentage of <see cref="SatsAmount"/> (0.05 = 0.05%).
    /// Escalates on retry per <see cref="RetryMaxFeePct"/> or Constants.
    /// </summary>
    public double MaxFeePct { get; set; }
    /// <summary>
    /// Maximum fee percent done in retries, falling to a user-supplied value or defaulting to Constants a retry is needed.
    /// </summary>
    public double? RetryMaxFeePct { get; set; }
    public long? FeePaidSats { get; set; }

    public long? FeePaidMsat { get; set; }

    /// <summary>
    /// Fee reserved for this in-flight rebalance so its spend counts against the node budget
    /// before <see cref="FeePaidSats"/> settles (Phase 3 in-flight budget accounting).
    /// </summary>
    public long? ReservedFeeSats { get; set; }

    [NotMapped]
    public long? EffectivePpm => FeePaidMsat.HasValue && SatsAmount > 0
        ? FeePaidMsat.Value * 1_000L / SatsAmount
        : (long?)null;

    [NotMapped]
    public Money FeePaid => new Money(FeePaidSats ?? 0, MoneyUnit.Satoshi);

    public int? SourceChannelId { get; set; }
    public Channel? SourceChannel { get; set; }

    /// <summary>
    /// LND chan_id of the source channel at the time of the request.
    /// </summary>
    public ulong? SourceChanIdLnd { get; set; }

    /// <summary>
    /// Last-hop peer pubkey which constrains the receiving PEER of the circular payment — not the receiving channel.
    /// Lightning nodes choose which channel between this node and that peer to use as the last hop, so theres no technical guarantee which channel will be used, but in practice this is sufficient to balance out source channels. 
    /// When null, LND picks any inbound peer that satisfies the cost cap.
    /// </summary>
    public string? TargetPubkey { get; set; }

    /// <summary>
    /// Payment hash of the self-invoice. Persisted as soon as the invoice is created so the
    /// reconciliation job can call <c>Router.TrackPaymentV2</c> after a crash / cancellation
    /// and resolve the true outcome against LND.
    /// </summary>
    public string? PaymentHashHex { get; set; }

    /// <summary>
    /// The (amountless) self-invoice bolt11. Created once on the first attempt and reused on
    /// every retry, so the whole rebalance settles against a single payment hash. The amount
    /// paid is set per attempt on SendPaymentV2, not encoded in the invoice.
    /// </summary>
    public string? PaymentRequest { get; set; }

    /// <summary>
    /// Persisted for forensic / proof-of-payment lookup; intentionally not exposed via gRPC.
    /// </summary>
    public string? PreimageHex { get; set; }

    public string? UserRequestorId { get; set; }
    public ApplicationUser? UserRequestor { get; set; }

    /// <summary>
    /// Pathfinding/payment timeout we passed to SendPaymentV2.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Multiplier applied to the rebalanced amount on each retry attempt. Range: (0, 1].
    /// 1 = never shrink (every attempt retries the full requested amount); 0.8 = each retry is
    /// 20% smaller; 0.5 = halve each time. The amount for attempt n is
    /// RequestedAmountSats × ratio^(n-1), floored at <c>Constants.REBALANCE_MIN_AMOUNT_SATS</c>.
    /// When null, the runtime falls back to <c>Constants.REBALANCE_AMOUNT_BACKOFF_RATIO</c>.
    /// </summary>
    public double? AmountBackoffRatio { get; set; }

    /// <summary>
    /// Maximum number of attempts (including the first try). When null, falls back to
    /// <c>Constants.REBALANCE_MAX_ATTEMPTS</c>
    /// </summary>
    public int? MaxAttempts { get; set; }


}
