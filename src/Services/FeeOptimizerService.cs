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
/// What the fee optimizer decided to do this cycle for a single channel.
/// </summary>
public enum FeeAction
{
    /// <summary>Inside the fee deadband (or computed delta below the min-delta dead-zone) — leave fees as they are.</summary>
    NoOp = 0,

    /// <summary>Apply the new outbound/inbound ppm.</summary>
    Update = 1,
}

/// <summary>
/// Result of one <see cref="IFeeOptimizerService.ComputeNextPolicy"/> call. For <see cref="FeeAction.Update"/>,
/// <see cref="OutboundPpm"/>/<see cref="InboundPpm"/> are the values to apply; for NoOp they
/// echo the current (unchanged) values so the caller can log a coherent "would-keep" line.
/// </summary>
public record FeePolicyDecision(FeeAction Action, uint OutboundPpm, int InboundPpm, string Reason);

/// <summary>
/// Control-law tunables for <see cref="IFeeOptimizerService.ComputeNextPolicy"/>. Passed in explicitly so the
/// decision logic stays pure and unit-testable; <see cref="FromConstants"/> is the production wiring.
/// </summary>
public record FeeOptimizerTunables(
    double OutboundIntegralGain,
    double InboundIntegralGain,
    double FeeDeadband,
    uint MaxStepPpm,
    uint MaxInboundStepPpm,
    uint MinDeltaPpm,
    uint MinOutboundPpm,
    uint MaxOutboundPpm,
    int MinInboundPpm,
    int MaxInboundPpm,
    uint BaselineSourcePpm,
    uint BaselineBidirectionalPpm,
    uint BaselineSinkPpm,
    uint BaselineUncategorizedPpm)
{
    /// <summary>Builds the tunable set from the ROUTING_ENGINE_FEE_* constants (the production configuration).</summary>
    public static FeeOptimizerTunables FromConstants() => new(
        OutboundIntegralGain: Constants.ROUTING_ENGINE_FEE_OUTBOUND_INTEGRAL_GAIN,
        InboundIntegralGain: Constants.ROUTING_ENGINE_FEE_INBOUND_INTEGRAL_GAIN,
        FeeDeadband: Constants.ROUTING_ENGINE_FEE_DEADBAND,
        MaxStepPpm: Constants.ROUTING_ENGINE_FEE_MAX_STEP_PPM,
        MaxInboundStepPpm: Constants.ROUTING_ENGINE_FEE_MAX_INBOUND_STEP_PPM,
        MinDeltaPpm: Constants.ROUTING_ENGINE_FEE_MIN_DELTA_PPM,
        MinOutboundPpm: Constants.ROUTING_ENGINE_FEE_MIN_OUTBOUND_PPM,
        MaxOutboundPpm: Constants.ROUTING_ENGINE_FEE_MAX_OUTBOUND_PPM,
        MinInboundPpm: Constants.ROUTING_ENGINE_FEE_MIN_INBOUND_PPM,
        MaxInboundPpm: Constants.ROUTING_ENGINE_FEE_MAX_INBOUND_PPM,
        BaselineSourcePpm: Constants.ROUTING_ENGINE_FEE_BASELINE_PPM_SOURCE,
        BaselineBidirectionalPpm: Constants.ROUTING_ENGINE_FEE_BASELINE_PPM_BIDIRECTIONAL,
        BaselineSinkPpm: Constants.ROUTING_ENGINE_FEE_BASELINE_PPM_SINK,
        BaselineUncategorizedPpm: (uint)Constants.DEFAULT_CHANNEL_FEE_POLICY_FEE_RATE_PPM);
}

public interface IFeeOptimizerService
{
    /// <summary>
    /// Integral control on the EMA-smoothed local ratio, driving both outbound ppm and (signed)
    /// inbound ppm toward the channel's target. Each cycle both fees are nudged off their previous
    /// value by gain·d·p₀, so a persistent deviation keeps driving them until the channel balances.
    /// Pure — no I/O, no clock, no DB.
    /// <para>
    /// With <c>d = emaLocalRatio - targetLocalRatio</c>:
    /// <list type="bullet">
    /// <item><c>|d| ≤ feeDeadband</c> → <see cref="FeeAction.NoOp"/>.</item>
    /// <item><c>d > 0</c> (too local): lower outbound to attract exits, raise inbound to repel entry.</item>
    /// <item><c>d < 0</c> (too remote): raise outbound to preserve local, deepen negative inbound to attract entry.</item>
    /// </list>
    /// The category baseline (p₀) both scales the per-cycle nudge and seeds the "last" values on the
    /// first evaluation of a channel, so a freshly categorized off-target channel starts from its
    /// category baseline and then integrates toward balance within the per-step clamp.
    /// </para>
    /// </summary>
    /// <param name="emaLocalRatio">Pre-smoothed local/(local+remote) ratio from ChannelRoutingState.</param>
    /// <param name="targetLocalRatio">Dynamic target ratio from ChannelRoutingState.</param>
    /// <param name="category">Peer flow category — selects the outbound baseline p₀.</param>
    /// <param name="lastOutboundPpm">Last-applied outbound ppm (ChannelFeeState); null on first eval → seeded with p₀.</param>
    /// <param name="lastInboundPpm">Last-applied inbound ppm (ChannelFeeState); null on first eval → seeded with 0.</param>
    /// <param name="allowPositiveInboundFees">When false, inbound is collapsed to ≤ 0 regardless of direction.</param>
    /// <param name="tunables">Control-law constants.</param>
    FeePolicyDecision ComputeNextPolicy(
        double emaLocalRatio,
        double targetLocalRatio,
        PeerFlowCategory category,
        uint? lastOutboundPpm,
        int? lastInboundPpm,
        bool allowPositiveInboundFees,
        FeeOptimizerTunables tunables);
}

public class FeeOptimizerService : IFeeOptimizerService
{
    public FeePolicyDecision ComputeNextPolicy(
        double emaLocalRatio,
        double targetLocalRatio,
        PeerFlowCategory category,
        uint? lastOutboundPpm,
        int? lastInboundPpm,
        bool allowPositiveInboundFees,
        FeeOptimizerTunables tunables)
    {
        var p0 = BaselineFor(category, tunables);
        // Seed the initial category "jump": with no last-applied value, start from the category
        // baseline so the first actionable write lands near p₀ rather than crawling from the
        // operator's pre-engine fee.
        var pLast = lastOutboundPpm ?? p0;
        var iLast = lastInboundPpm ?? 0;

        var d = emaLocalRatio - targetLocalRatio;
        var absD = Math.Abs(d);

        // Nothing to correct inside the fee deadband.
        if (absD <= tunables.FeeDeadband)
        {
            return new FeePolicyDecision(FeeAction.NoOp, pLast, iLast,
                $"|d|={absD:0.###} <= feeDeadband={tunables.FeeDeadband:0.###}");
        }

        // Both fees use integral control: each cycle the applied value is nudged by gain·d·p0 off its
        // previous value, so a persistent deviation keeps driving the fee until the channel balances.
        // The [min, max] clamp on the new value is the anti-windup — state can never exceed the rail.

        // Outbound: raise when too remote (d<0), lower when too local (d>0).
        var pNew = RoundClampUint(pLast - tunables.OutboundIntegralGain * d * p0, tunables.MinOutboundPpm, tunables.MaxOutboundPpm);
        var dp = Math.Clamp((long)pNew - pLast, -(long)tunables.MaxStepPpm, tunables.MaxStepPpm);
        var outbound = Math.Abs(dp) < tunables.MinDeltaPpm ? pLast : (uint)(pLast + dp);

        // Inbound: deepen negative when too remote (attract entry), raise positive when too local (repel entry).
        var iNewRaw = iLast + tunables.InboundIntegralGain * d * p0;
        if (!allowPositiveInboundFees)
        {
            iNewRaw = Math.Min(iNewRaw, 0);
        }
        var iNew = RoundClampInt(iNewRaw, tunables.MinInboundPpm, tunables.MaxInboundPpm);
        var di = Math.Clamp((long)iNew - iLast, -(long)tunables.MaxInboundStepPpm, tunables.MaxInboundStepPpm);
        var inbound = Math.Abs(di) < tunables.MinDeltaPpm ? iLast : (int)(iLast + di);

        if (outbound == pLast && inbound == iLast)
        {
            return new FeePolicyDecision(FeeAction.NoOp, pLast, iLast, "computed delta below min-delta dead-zone");
        }

        return new FeePolicyDecision(FeeAction.Update, outbound, inbound,
            $"d={d:0.###} outbound {pLast}->{outbound}ppm inbound {iLast}->{inbound}ppm (p0={p0})");
    }

    private static uint BaselineFor(PeerFlowCategory category, FeeOptimizerTunables t) => category switch
    {
        PeerFlowCategory.Source => t.BaselineSourcePpm,
        PeerFlowCategory.Bidirectional => t.BaselineBidirectionalPpm,
        PeerFlowCategory.Sink => t.BaselineSinkPpm,
        _ => t.BaselineUncategorizedPpm,
    };

    private static uint RoundClampUint(double value, uint min, uint max)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < min) return min;
        if (rounded > max) return max;
        return (uint)rounded;
    }

    private static int RoundClampInt(double value, int min, int max)
    {
        var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, min, max);
    }
}
