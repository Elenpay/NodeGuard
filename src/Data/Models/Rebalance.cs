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
    Pending,
    Probing,
    InFlight,
    Succeeded,
    Failed,
    NoRoute,
    Timeout,
    InsufficientBalance,
    ExceededFeeLimit
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
    /// Multiplier applied to the probe amount after each failure. Range: (0, 1) exclusive.
    /// 0.5 = halve (default); 0.8 = next try is 20% smaller (finer-grained, more iterations);
    /// 0.3 = next try is 70% smaller (gives up faster). When null, the runtime falls back
    /// to <c>Constants.REBALANCE_PROBE_BACKOFF_RATIO</c>.
    /// </summary>
    public double? ProbeBackoffRatio { get; set; }

    /// <summary>
    /// Maximum number of attempts (including the first try). When null, falls back to
    /// <c>Constants.REBALANCE_MAX_ATTEMPTS</c>
    /// </summary>
    public int? MaxAttempts { get; set; }


}
