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
using NodeGuard.Services.Spark;

namespace NodeGuard.Services;

/// <summary>
/// What the automatic swap-out (<c>AutoLiquidityManagementJob</c>) would take from a node's new outbound after a channel
/// open, by the same rule it swaps by.
/// </summary>
public static class SwapOutForecast
{
    /// <summary>
    /// The sats of a <paramref name="channelSats"/> channel opened from <paramref name="node"/> that the automatic
    /// swap-out would take, or null when it would not swap. It swaps when <see cref="SwapsOutAutomatically"/> and the
    /// excess, the node's local balance after the open (<paramref name="localBalanceSats"/>, before the open, plus the
    /// channel) over its threshold, fits a weighted provider the job can pick (see <see cref="CanSwap"/>).
    /// What only delays a swap is left out: swaps in flight, the budget spent this period, a busy or full Spark wallet.
    /// </summary>
    public static long? SatsSwappedAfterOpen(Node node, long localBalanceSats, long channelSats, SparkSettings spark)
    {
        if (!SwapsOutAutomatically(node))
        {
            return null;
        }

        var excess = localBalanceSats + channelSats - node.MinimumBalanceThresholdSats;
        if (excess <= 0 || !SwapProviderLimits.Weighted(node).Any(w => CanSwap(node, w.Provider, excess, spark)))
        {
            return null;
        }

        return Math.Min(channelSats, excess);
    }

    /// <summary>
    /// Whether the automatic swap-out can swap anything for <paramref name="node"/>: enabled, with automatic liquidity
    /// management on, a funds destination wallet (as <c>INodeRepository.GetAllWithAutoLiquidityEnabled</c> picks them)
    /// and a swap budget above 0 (with none, every run stops at an exhausted budget).
    /// </summary>
    public static bool SwapsOutAutomatically(Node node) =>
        !node.IsNodeDisabled && node.AutoLiquidityManagementEnabled && node.FundsDestinationWalletId.HasValue
        && node.SwapBudgetSats > 0;

    /// <summary>
    /// Whether <c>AutoLiquidityManagementJob</c> can swap <paramref name="excess"/> sats through
    /// <paramref name="provider"/>: it runs swaps (max swaps in flight above 0), the most it may take of the excess
    /// (its max swap) reaches its min swap and, for Spark, the node has a Spark wallet.
    /// </summary>
    private static bool CanSwap(Node node, SwapProvider provider, long excess, SparkSettings spark)
    {
        var limits = SwapProviderLimits.For(node, provider, spark);
        return limits.MaxInFlight > 0
               && Math.Min(excess, limits.MaxSats) >= limits.MinSats
               && (provider != SwapProvider.Spark || spark.WalletFor(node) is not null);
    }
}
