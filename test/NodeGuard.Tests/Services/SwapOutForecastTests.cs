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
using NodeGuard.Services.Spark;

namespace NodeGuard.Services;

public class SwapOutForecastTests
{
    private static readonly SparkSettings Spark = new() { Enabled = true, SwapMinSats = 50_000 };

    /// <summary>A node the automatic swap-out runs for: 1M sats threshold, Loop only with a 100k sats min swap.</summary>
    private static Node SwappingNode() => new()
    {
        Name = "alice",
        AutoLiquidityManagementEnabled = true,
        FundsDestinationWalletId = 1,
        MinimumBalanceThresholdSats = 1_000_000,
        LoopSwapWeight = 100,
        LoopSwapMinAmountSats = 100_000,
        LoopSwapMaxAmountSats = 10_000_000,
        FortySwapWeight = 0,
        FortySwapMinAmountSats = 200_000,
        SparkSwapWeight = 0
    };

    [Fact]
    public void AnOpenThatTakesTheBalanceOverTheThresholdByAMinSwap_IsSwappedOut()
    {
        // 950k + 500k = 1.45M, 450k over the threshold: that much of the channel would be swapped
        SwapOutForecast.SatsSwappedAfterOpen(SwappingNode(), 950_000, 500_000, Spark).Should().Be(450_000);
    }

    [Fact]
    public void ANodeAlreadyOverItsThreshold_LosesTheWholeChannel()
    {
        SwapOutForecast.SatsSwappedAfterOpen(SwappingNode(), 2_000_000, 500_000, Spark).Should().Be(500_000);
    }

    [Theory]
    [InlineData(400_000, 600_000)] // lands on the threshold
    [InlineData(100_000, 500_000)] // stays under it
    [InlineData(1_000_000, 99_999)] // over it by less than the min swap
    public void AnOpenThatLeavesLessThanAMinSwapOverTheThreshold_IsNotSwappedOut(long localBalanceSats, long channelSats)
    {
        SwapOutForecast.SatsSwappedAfterOpen(SwappingNode(), localBalanceSats, channelSats, Spark).Should().BeNull();
    }

    [Fact]
    public void AnOpenOverTheThresholdByExactlyAMinSwap_IsSwappedOut()
    {
        SwapOutForecast.SatsSwappedAfterOpen(SwappingNode(), 1_000_000, 100_000, Spark).Should().Be(100_000);
    }

    [Fact]
    public void ANodeWithoutAutomaticLiquidityManagement_IsNotSwappedOut()
    {
        var node = SwappingNode();
        node.AutoLiquidityManagementEnabled = false;

        SwapOutForecast.SatsSwappedAfterOpen(node, 2_000_000, 500_000, Spark).Should().BeNull();
    }

    [Fact]
    public void ADisabledNode_IsNotSwappedOut()
    {
        var node = SwappingNode();
        node.IsNodeDisabled = true;

        SwapOutForecast.SatsSwappedAfterOpen(node, 2_000_000, 500_000, Spark).Should().BeNull();
    }

    [Fact]
    public void ANodeWithoutAFundsDestinationWallet_IsNotSwappedOut()
    {
        var node = SwappingNode();
        node.FundsDestinationWalletId = null;

        SwapOutForecast.SatsSwappedAfterOpen(node, 2_000_000, 500_000, Spark).Should().BeNull();
    }

    [Fact]
    public void WithEveryWeightAtZero_LoopsMinSwapApplies()
    {
        var node = SwappingNode();
        node.LoopSwapWeight = 0;

        SwapOutForecast.SatsSwappedAfterOpen(node, 1_000_000, 99_999, Spark).Should().BeNull();
        SwapOutForecast.SatsSwappedAfterOpen(node, 1_000_000, 100_000, Spark).Should().Be(100_000);
    }

    [Fact]
    public void TheSmallestMinSwapAmongTheWeightedProviders_Applies()
    {
        var node = SwappingNode();
        node.SparkSwapWeight = 50;
        node.SparkSwapMinAmountSats = 20_000;

        SwapOutForecast.SatsSwappedAfterOpen(node, 1_000_000, 20_000, Spark).Should().Be(20_000);
    }

    [Fact]
    public void AnUnweightedProvidersMinSwap_IsLeftOut()
    {
        var node = SwappingNode();
        node.LoopSwapWeight = 0;
        node.FortySwapWeight = 100;

        // 40swap's 200k min applies, not Loop's 100k
        SwapOutForecast.SatsSwappedAfterOpen(node, 1_000_000, 150_000, Spark).Should().BeNull();
        SwapOutForecast.SatsSwappedAfterOpen(node, 1_000_000, 200_000, Spark).Should().Be(200_000);
    }

    [Fact]
    public void ASparkWeightedNodeWithoutItsOwnMinSwap_UsesTheSparkSettings()
    {
        var node = SwappingNode();
        node.LoopSwapWeight = 0;
        node.SparkSwapWeight = 100;

        SwapOutForecast.SatsSwappedAfterOpen(node, 1_000_000, 49_999, Spark).Should().BeNull();
        SwapOutForecast.SatsSwappedAfterOpen(node, 1_000_000, 50_000, Spark).Should().Be(50_000);
    }
}
