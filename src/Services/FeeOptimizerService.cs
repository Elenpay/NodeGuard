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
/// Result of one <see cref="FeeOptimizerService.ComputeNextPolicy"/> call. For <see cref="FeeAction.Update"/>,
/// <see cref="OutboundPpm"/>/<see cref="InboundPpm"/> are the values to apply; for NoOp they
/// echo the current (unchanged) values so the caller can log a coherent "would-keep" line.
/// </summary>
public record FeePolicyDecision(FeeAction Action, uint OutboundPpm, int InboundPpm, string Reason);

/// <summary>
/// Control-law tunables for <see cref="FeeOptimizerService.ComputeNextPolicy"/>. Passed in explicitly so the
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
        BaselineUncategorizedPpm: Constants.ROUTING_ENGINE_FEE_BASELINE_PPM_UNCATEGORIZED);
}

/// <summary>
/// Pure integral control law for the dynamic fee engine — no I/O, no clock, no DB, no injected state, so
/// it is a static function library rather than a DI-registered service.
/// </summary>
public static class FeeOptimizerService
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
    /// Each step's size scales with the category's baseline fee (p₀). The first time a channel is seen it
    /// starts from that baseline rather than the operator's pre-engine fee, then closes in on target over
    /// the following runs, one clamped step at a time.
    /// </para>
    /// </summary>
    /// <param name="emaLocalRatio">Smoothed local/(local+remote) balance ratio, from ChannelRoutingState.</param>
    /// <param name="targetLocalRatio">The balance ratio we're aiming for, from ChannelRoutingState.</param>
    /// <param name="category">The channel's flow category — picks which baseline fee (p₀) to use.</param>
    /// <param name="lastOutboundPpm">Outbound fee applied last time (ChannelFeeState); null on the first run → starts at p₀.</param>
    /// <param name="lastInboundPpm">Inbound fee applied last time (ChannelFeeState); null on the first run → starts at 0.</param>
    /// <param name="allowPositiveInboundFees">When false, the inbound fee is kept at ≤ 0 (never a surcharge).</param>
    /// <param name="tunables">The gains, clamps, and baselines the fee logic uses.</param>
    public static FeePolicyDecision ComputeNextPolicy(
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
        var pNew = RoundClamp(pLast - tunables.OutboundIntegralGain * d * p0, tunables.MinOutboundPpm, tunables.MaxOutboundPpm);
        var dp = Math.Clamp(pNew - pLast, -tunables.MaxStepPpm, tunables.MaxStepPpm);
        var outbound = Math.Abs(dp) < tunables.MinDeltaPpm ? pLast : (uint)(pLast + dp);

        // Inbound: deepen negative when too remote (attract entry), raise positive when too local (repel entry).
        var iNewRaw = iLast + tunables.InboundIntegralGain * d * p0;
        if (!allowPositiveInboundFees)
        {
            iNewRaw = Math.Min(iNewRaw, 0);
        }
        var iNew = RoundClamp(iNewRaw, tunables.MinInboundPpm, tunables.MaxInboundPpm);
        var di = Math.Clamp(iNew - iLast, -tunables.MaxInboundStepPpm, tunables.MaxInboundStepPpm);
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

    private static long RoundClamp(double value, long min, long max)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        return (long)Math.Clamp(rounded, min, max);
    }
}
