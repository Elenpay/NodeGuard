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

namespace NodeGuard.Services;

public class PeerCategorizationServiceTests
{
    private const int Hysteresis = 3;
    private const double Threshold = 0.25;
    private const long MinVolume = 10_000_000_000;
    private const long AboveMin = 20_000_000_000;

    private CategoryDecision Compute(
        double netFlowRatio,
        long volume,
        PeerFlowCategory current,
        PeerFlowCategory? pending,
        uint streak)
        => PeerCategorizationService.ComputeCategory(netFlowRatio, volume, current, pending, streak, Hysteresis, Threshold, MinVolume);

    [Fact]
    public void ComputeCategory_PushHeavy_CommitsSink_OnThirdConsecutiveCycle()
    {
        // Cycle 1 & 2: pending Sink, not yet flipped.
        var c1 = Compute(0.30, AboveMin, PeerFlowCategory.Uncategorized, null, 0);
        c1.Should().Be(new CategoryDecision(PeerFlowCategory.Uncategorized, PeerFlowCategory.Sink, 1, false));

        var c2 = Compute(0.30, AboveMin, c1.Category, c1.PendingCategory, c1.ConsecutiveCyclesInNewState);
        c2.Should().Be(new CategoryDecision(PeerFlowCategory.Uncategorized, PeerFlowCategory.Sink, 2, false));

        // Cycle 3: flip commits.
        var c3 = Compute(0.30, AboveMin, c2.Category, c2.PendingCategory, c2.ConsecutiveCyclesInNewState);
        c3.Should().Be(new CategoryDecision(PeerFlowCategory.Sink, null, 0, true));
    }

    [Fact]
    public void ComputeCategory_PullHeavy_CommitsSource_OnThirdConsecutiveCycle()
    {
        var current = PeerFlowCategory.Uncategorized;
        PeerFlowCategory? pending = null;
        uint streak = 0;
        CategoryDecision decision = default!;

        for (var i = 0; i < Hysteresis; i++)
        {
            decision = Compute(-0.30, AboveMin, current, pending, streak);
            current = decision.Category;
            pending = decision.PendingCategory;
            streak = decision.ConsecutiveCyclesInNewState;
        }

        decision.Category.Should().Be(PeerFlowCategory.Source);
        decision.Flipped.Should().BeTrue();
    }

    [Fact]
    public void ComputeCategory_BalancedFlow_CommitsBidirectional()
    {
        var current = PeerFlowCategory.Uncategorized;
        PeerFlowCategory? pending = null;
        uint streak = 0;
        CategoryDecision decision = default!;

        for (var i = 0; i < Hysteresis; i++)
        {
            decision = Compute(0.05, AboveMin, current, pending, streak);
            current = decision.Category;
            pending = decision.PendingCategory;
            streak = decision.ConsecutiveCyclesInNewState;
        }

        decision.Category.Should().Be(PeerFlowCategory.Bidirectional);
    }

    [Fact]
    public void ComputeCategory_BelowMinVolume_YieldsUncategorized_RegardlessOfRatio()
    {
        // Strong push ratio but not enough volume — stays Uncategorized in steady state.
        var decision = Compute(0.90, MinVolume - 1, PeerFlowCategory.Uncategorized, null, 0);
        decision.Should().Be(new CategoryDecision(PeerFlowCategory.Uncategorized, null, 0, false));
    }

    [Fact]
    public void ComputeCategory_InterruptedStreak_DoesNotFlip()
    {
        // Sink, Sink, then Bidirectional resets the streak — no flip to either.
        var c1 = Compute(0.30, AboveMin, PeerFlowCategory.Uncategorized, null, 0);
        var c2 = Compute(0.30, AboveMin, c1.Category, c1.PendingCategory, c1.ConsecutiveCyclesInNewState);
        var c3 = Compute(0.05, AboveMin, c2.Category, c2.PendingCategory, c2.ConsecutiveCyclesInNewState);

        c3.Flipped.Should().BeFalse();
        c3.Category.Should().Be(PeerFlowCategory.Uncategorized);
        c3.PendingCategory.Should().Be(PeerFlowCategory.Bidirectional);
        c3.ConsecutiveCyclesInNewState.Should().Be(1);
    }

    [Fact]
    public void ComputeCategory_SteadyState_ReturnsCurrentWithClearedStreak()
    {
        var decision = Compute(0.30, AboveMin, PeerFlowCategory.Sink, null, 0);
        decision.Should().Be(new CategoryDecision(PeerFlowCategory.Sink, null, 0, false));
    }

    [Fact]
    public void ComputeCategory_EstablishedSink_DecaysToUncategorized_AfterHysteresis_WhenVolumeDrops()
    {
        var current = PeerFlowCategory.Sink;
        PeerFlowCategory? pending = null;
        uint streak = 0;
        CategoryDecision decision = default!;

        for (var i = 0; i < Hysteresis; i++)
        {
            decision = Compute(0.30, MinVolume - 1, current, pending, streak); // volume dropped
            current = decision.Category;
            pending = decision.PendingCategory;
            streak = decision.ConsecutiveCyclesInNewState;
        }

        decision.Category.Should().Be(PeerFlowCategory.Uncategorized);
        decision.Flipped.Should().BeTrue();
    }

    [Theory]
    [InlineData(1.0, 0.85)]   // max positive drift (sink)
    [InlineData(0.5, 0.85)]   // 0.7*0.5 = 0.35 = maxDrift
    [InlineData(-1.0, 0.15)]  // max negative drift (source)
    [InlineData(-0.5, 0.15)]
    [InlineData(0.0, 0.5)]
    public void ComputeTargetGoal_ClampsToBand(double netFlowRatio, double expected)
    {
        PeerCategorizationService.ComputeTargetGoal(netFlowRatio, kTarget: 0.70, maxDrift: 0.35)
            .Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void ComputeTargetGoal_SignFollowsFlow()
    {
        PeerCategorizationService.ComputeTargetGoal(0.10, 0.70, 0.35).Should().BeGreaterThan(0.5);  // sink → above
        PeerCategorizationService.ComputeTargetGoal(-0.10, 0.70, 0.35).Should().BeLessThan(0.5);    // source → below
    }

    [Fact]
    public void SmoothEma_IsAlphaWeightedAverage()
    {
        PeerCategorizationService.SmoothEma(currentEma: 0.5, observedRatio: 1.0, alphaRatio: 0.04)
            .Should().BeApproximately(0.52, 1e-9);
    }

    [Fact]
    public void SmoothEma_StepInput_Reaches63PercentIn1OverAlphaSamples()
    {
        const double alpha = 0.1;
        var ema = 0.0; // start; step target = 1.0
        for (var i = 0; i < (int)(1 / alpha); i++)
        {
            ema = PeerCategorizationService.SmoothEma(ema, 1.0, alpha);
        }

        // Classic first-order step response: ~63% of the way to the goal after 1/alpha samples.
        ema.Should().BeApproximately(0.63, 0.05);
    }

    [Fact]
    public void SmoothTarget_ConvergesMonotonicallyTowardGoal()
    {
        const double goal = 0.8;
        var target = 0.5;
        var previousGap = goal - target;

        for (var i = 0; i < 25; i++)
        {
            target = PeerCategorizationService.SmoothTarget(target, goal, alphaTarget: 0.10);
            var gap = goal - target;
            gap.Should().BeLessThan(previousGap); // strictly closing the gap
            gap.Should().BeGreaterThan(0);        // never overshoots
            previousGap = gap;
        }

        target.Should().BeApproximately(goal, 0.05);
    }
}
