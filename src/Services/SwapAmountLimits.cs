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

/// <summary>The smallest and largest swap-out a node makes through a provider.</summary>
public readonly record struct SwapAmountLimits(long MinSats, long MaxSats)
{
    /// <summary>
    /// <paramref name="node"/>'s limits for <paramref name="provider"/>: Loop's and 40swap's are the node's own;
    /// Spark's are the node's own, else SPARK_SWAP_MIN_SATS / SPARK_SWAP_MAX_SATS.
    /// </summary>
    public static SwapAmountLimits For(Node node, SwapProvider provider, SparkSettings spark) => provider switch
    {
        SwapProvider.Loop => new(node.LoopSwapMinAmountSats, node.LoopSwapMaxAmountSats),
        SwapProvider.FortySwap => new(node.FortySwapMinAmountSats, node.FortySwapMaxAmountSats),
        SwapProvider.Spark => new(node.SparkSwapMinAmountSats ?? spark.SwapMinSats, node.SparkSwapMaxAmountSats ?? spark.SwapMaxSats),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown swap provider")
    };
}
