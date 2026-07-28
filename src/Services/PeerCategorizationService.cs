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

namespace NodeGuard.Services;

/// <summary>
/// Outcome of one categorization cycle. Carries the full hysteresis state so the caller can
/// persist it without re-deriving anything.
/// </summary>
/// <param name="Category">Committed category after this cycle.</param>
/// <param name="PendingCategory">Tentative category being counted toward a flip; null in steady state.</param>
/// <param name="ConsecutiveCyclesInNewState">Updated streak counter (0 in steady state).</param>
/// <param name="Flipped">True when the committed category changed this cycle.</param>
public record CategoryDecision(
    PeerFlowCategory Category,
    PeerFlowCategory? PendingCategory,
    uint ConsecutiveCyclesInNewState,
    bool Flipped);

public static class PeerCategorizationService
{
    /// <summary>
    /// Applies the volume gate + net-flow thresholds to derive a tentative category, then runs
    /// the N-cycle anti-flap hysteresis against the current/pending state. Pure — no I/O.
    /// </summary>
    public static CategoryDecision ComputeCategory(
        double netFlowRatio,
        long windowVolumeMsat,
        PeerFlowCategory currentCategory,
        PeerFlowCategory? pendingCategory,
        uint consecutiveCyclesInNewState,
        int flipHysteresisCycles,
        double netFlowThreshold,
        long minVolumeMsat)
    {
        var tentative = ComputeTentative(netFlowRatio, windowVolumeMsat, netFlowThreshold, minVolumeMsat);

        // Steady state: what we observe matches the committed category — clear any pending streak.
        if (tentative == currentCategory)
        {
            return new CategoryDecision(currentCategory, null, 0, false);
        }

        // Diverging from the committed category: extend the streak if it's the same tentative we
        // were already counting, otherwise restart the streak at 1 for this new tentative.
        var newStreak = pendingCategory == tentative
            ? consecutiveCyclesInNewState + 1
            : 1u;

        // Commit the flip once the streak reaches the hysteresis threshold.
        if (newStreak >= (uint)Math.Max(1, flipHysteresisCycles))
        {
            return new CategoryDecision(tentative, null, 0, true);
        }

        return new CategoryDecision(currentCategory, tentative, newStreak, false);
    }

    private static PeerFlowCategory ComputeTentative(
        double netFlowRatio, long windowVolumeMsat, double netFlowThreshold, long minVolumeMsat)
    {
        if (windowVolumeMsat < minVolumeMsat) return PeerFlowCategory.Uncategorized;
        if (netFlowRatio >= netFlowThreshold) return PeerFlowCategory.Sink;
        if (netFlowRatio <= -netFlowThreshold) return PeerFlowCategory.Source;
        return PeerFlowCategory.Bidirectional;
    }

    /// <summary>
    /// target_goal = clamp(0.5 + clamp(kTarget · netFlowRatio, -maxDrift, +maxDrift), 0.10, 0.90).
    /// Positive net-flow (SINK) pulls the target above 0.5; negative (SOURCE) below.
    /// </summary>
    public static double ComputeTargetGoal(double netFlowRatio, double kTarget, double maxDrift)
    {
        var drift = Math.Clamp(kTarget * netFlowRatio, -maxDrift, maxDrift);
        return Math.Clamp(0.5 + drift, 0.10, 0.90);
    }

    /// <summary>EWMA: alphaTarget · targetGoal + (1 - alphaTarget) · currentTarget.</summary>
    public static double SmoothTarget(double currentTarget, double targetGoal, double alphaTarget)
        => alphaTarget * targetGoal + (1 - alphaTarget) * currentTarget;

    /// <summary>EWMA: alphaRatio · observedRatio + (1 - alphaRatio) · currentEma.</summary>
    public static double SmoothEma(double currentEma, double observedRatio, double alphaRatio)
        => alphaRatio * observedRatio + (1 - alphaRatio) * currentEma;
}
