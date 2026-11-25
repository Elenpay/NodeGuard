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
    /// swap-out would take, or null when it would not swap. It swaps when the node has automatic liquidity management on
    /// and a funds destination wallet, and its local balance after the open (<paramref name="localBalanceSats"/>, before
    /// the open, plus the channel) is above its threshold by at least the smallest min swap of its weighted providers.
    /// Swap fee budget, swaps in flight and Spark wallet room are left out: they delay a swap, not prevent it.
    /// </summary>
    public static long? SatsSwappedAfterOpen(Node node, long localBalanceSats, long channelSats, SparkSettings spark)
    {
        if (!SwapsOutAutomatically(node))
        {
            return null;
        }

        var excess = localBalanceSats + channelSats - node.MinimumBalanceThresholdSats;
        var minSwap = SwapProviderLimits.Weighted(node).Min(w => SwapProviderLimits.For(node, w.Provider, spark).MinSats);
        if (excess <= 0 || excess < minSwap)
        {
            return null;
        }

        return Math.Min(channelSats, excess);
    }

    /// <summary>
    /// Whether the automatic swap-out runs for <paramref name="node"/>: enabled, with automatic liquidity management on
    /// and a funds destination wallet (as <c>INodeRepository.GetAllWithAutoLiquidityEnabled</c> picks them).
    /// </summary>
    public static bool SwapsOutAutomatically(Node node) =>
        !node.IsNodeDisabled && node.AutoLiquidityManagementEnabled && node.FundsDestinationWalletId.HasValue;
}
