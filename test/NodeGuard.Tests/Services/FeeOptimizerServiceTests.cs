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

using FluentAssertions;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;

namespace NodeGuard.Services;

public class FeeOptimizerServiceTests
{
    private readonly FeeOptimizerService _sut = new();

    // Vision-doc defaults.
    private static readonly FeeOptimizerTunables Tunables = new(
        OutboundIntegralGain: 0.8,
        InboundIntegralGain: 0.5,
        FeeDeadband: 0.03,
        RebalanceDeadband: 0.15,
        MaxStepPpm: 50,
        MaxInboundStepPpm: 25,
        MinDeltaPpm: 5,
        MinOutboundPpm: 0,
        MaxOutboundPpm: 5000,
        MinInboundPpm: -250,
        MaxInboundPpm: 100,
        BaselineSourcePpm: 50,
        BaselineBidirectionalPpm: 500,
        BaselineSinkPpm: 2500,
        BaselineUncategorizedPpm: 500);

    private FeePolicyDecision Compute(
        double ema,
        double target,
        PeerFlowCategory category,
        uint? lastOutbound,
        int? lastInbound,
        bool allowPositiveInbound)
        => _sut.ComputeNextPolicy(ema, target, category, lastOutbound, lastInbound, allowPositiveInbound, Tunables);

    [Fact]
    public void OutOfRebalanceDeadband_DefersToRebalancer()
    {
        // d = 0.9 - 0.5 = 0.40 > 0.15
        var decision = Compute(0.90, 0.50, PeerFlowCategory.Sink, 2500, 0, allowPositiveInbound: true);

        decision.Action.Should().Be(FeeAction.DeferToRebalancer);
    }

    [Fact]
    public void InsideFeeDeadband_IsNoOp()
    {
        // d = 0.51 - 0.50 = 0.01 <= 0.03
        var decision = Compute(0.51, 0.50, PeerFlowCategory.Sink, 2500, 0, allowPositiveInbound: true);

        decision.Action.Should().Be(FeeAction.NoOp);
    }

    [Fact]
    public void TooLocal_LowersOutbound_AndAppliesPositiveInbound()
    {
        // d = +0.10 (too local, need to drain): outbound down, inbound positive.
        var decision = Compute(0.60, 0.50, PeerFlowCategory.Sink, 2500, 0, allowPositiveInbound: true);

        decision.Action.Should().Be(FeeAction.Update);
        decision.OutboundPpm.Should().Be(2450); // 2500 - clamp(200, ±50) = 2500 - 50
        decision.InboundPpm.Should().Be(25);     // clamp(+125→+100 target, step ±25) from 0
    }

    [Fact]
    public void TooRemote_RaisesOutbound_AndAppliesNegativeInbound()
    {
        // d = -0.10 (too remote, need to fill): outbound up, inbound negative.
        var decision = Compute(0.40, 0.50, PeerFlowCategory.Sink, 2500, 0, allowPositiveInbound: true);

        decision.Action.Should().Be(FeeAction.Update);
        decision.OutboundPpm.Should().Be(2550); // 2500 + clamp(200, ±50)
        decision.InboundPpm.Should().Be(-25);   // -125 target, step-clamped to -25 from 0
    }

    [Fact]
    public void PositiveInboundDisallowed_CollapsesInboundToZero_ButStillMovesOutbound()
    {
        // Same too-local case as above, but the node forbids positive inbound.
        var decision = Compute(0.60, 0.50, PeerFlowCategory.Sink, 2500, 0, allowPositiveInbound: false);

        decision.Action.Should().Be(FeeAction.Update);
        decision.OutboundPpm.Should().Be(2450);
        decision.InboundPpm.Should().Be(0); // +125 → min(.,0)=0 → no inbound change
    }

    [Fact]
    public void ComputedDeltaBelowMinDelta_IsNoOp()
    {
        // Source baseline p0=50, small d just outside the deadband → both deltas < min-delta (5).
        var decision = Compute(0.54, 0.50, PeerFlowCategory.Source, 50, 0, allowPositiveInbound: true);

        decision.Action.Should().Be(FeeAction.NoOp);
        decision.OutboundPpm.Should().Be(50);
        decision.InboundPpm.Should().Be(0);
    }

    [Fact]
    public void FirstEvaluation_SeedsLastValuesFromCategoryBaseline()
    {
        // No last-applied values (first eval). Should behave identically to seeding p_last=p0(sink)=2500, i_last=0.
        var decision = Compute(0.40, 0.50, PeerFlowCategory.Sink, lastOutbound: null, lastInbound: null, allowPositiveInbound: true);

        decision.Action.Should().Be(FeeAction.Update);
        decision.OutboundPpm.Should().Be(2550);
        decision.InboundPpm.Should().Be(-25);
    }

    [Fact]
    public void Outbound_IsClampedToMaxCeiling()
    {
        // Near the ceiling, too-remote pressure would push past MAX_OUTBOUND_PPM (5000).
        // d = -0.14 (inside the rebalance deadband, so still fee-engine territory).
        var decision = Compute(0.36, 0.50, PeerFlowCategory.Sink, 4990, -200, allowPositiveInbound: true);

        decision.Action.Should().Be(FeeAction.Update);
        decision.OutboundPpm.Should().Be(5000); // pNew 5270 clamped to 5000, reached within one +10 step
    }

    [Fact]
    public void Inbound_IsClampedToMaxCeiling()
    {
        // iLast already near +100; a further positive push must not exceed MAX_INBOUND_PPM.
        var decision = Compute(0.60, 0.50, PeerFlowCategory.Sink, 2500, 90, allowPositiveInbound: true);

        decision.Action.Should().Be(FeeAction.Update);
        decision.InboundPpm.Should().Be(100); // 90 + clamp(10, ±25) = 100 (ceiling)
    }

    [Fact]
    public void Inbound_Integrates_DeepeningPastSingleCycleNudge()
    {
        // Sink p0=2500, d=-0.10 → per-cycle nudge = 0.5·(-0.10)·2500 = -125.
        // A proportional controller would settle at -125 and stop; the integrator keeps deepening
        // off the previous value, so from -125 it steps a further -25 toward the floor.
        var decision = Compute(0.40, 0.50, PeerFlowCategory.Sink, 2500, -125, allowPositiveInbound: true);

        decision.Action.Should().Be(FeeAction.Update);
        decision.InboundPpm.Should().Be(-150);
    }

    [Fact]
    public void FromConstants_MapsBaselineTriple()
    {
        var t = FeeOptimizerTunables.FromConstants();

        t.BaselineSourcePpm.Should().Be(Constants.ROUTING_ENGINE_FEE_BASELINE_PPM_SOURCE);
        t.BaselineBidirectionalPpm.Should().Be(Constants.ROUTING_ENGINE_FEE_BASELINE_PPM_BIDIRECTIONAL);
        t.BaselineSinkPpm.Should().Be(Constants.ROUTING_ENGINE_FEE_BASELINE_PPM_SINK);
        t.BaselineUncategorizedPpm.Should().Be((uint)Constants.DEFAULT_CHANNEL_FEE_POLICY_FEE_RATE_PPM);
    }
}
