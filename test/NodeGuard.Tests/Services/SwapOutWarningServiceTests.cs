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
using Lnrpc;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services.Spark;
using NSubstitute;

namespace NodeGuard.Services;

public class SwapOutWarningServiceTests
{
    private const int NodeId = 1;

    private readonly INodeRepository _nodeRepository = Substitute.For<INodeRepository>();
    private readonly ILightningService _lightningService = Substitute.For<ILightningService>();

    private SwapOutWarningService BuildService() =>
        new(_nodeRepository, _lightningService, new SparkSettings { Enabled = true, SwapMinSats = 50_000 });

    /// <summary>
    /// A node the automatic swap-out runs for: 1M sats threshold, Loop only with a 100k sats min swap.
    /// </summary>
    private static Node SwappingNode() => new()
    {
        Id = NodeId,
        Name = "alice",
        AutoLiquidityManagementEnabled = true,
        FundsDestinationWalletId = 1,
        MinimumBalanceThresholdSats = 1_000_000,
        SwapBudgetSats = 100_000,
        LoopSwapWeight = 100,
        LoopSwapMinAmountSats = 100_000,
        LoopSwapMaxAmountSats = 10_000_000,
        LoopMaxSwapsInFlight = 1,
        FortySwapWeight = 0,
        SparkSwapWeight = 0
    };

    private void ArrangeNode(Node node, ulong localSats, ulong pendingOpenSats = 0)
    {
        _nodeRepository.GetById(NodeId, false).Returns(node);
        _lightningService.ChannelBalanceAsync(node).Returns(new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = localSats },
            PendingOpenLocalBalance = new Amount { Sat = pendingOpenSats }
        });
    }

    [Fact]
    public async Task AnOpenThatTakesTheBalanceOverTheThreshold_WarnsWithTheAmounts()
    {
        // 950k + 500k = 1.45M, 450k over the 1M threshold
        ArrangeNode(SwappingNode(), localSats: 950_000);

        var warning = await BuildService().ForChannelOpenAsync(NodeId, 500_000);

        warning.Should().Contain("alice").And.Contain("0.01450000 BTC").And.Contain("0.01000000 BTC").And.Contain("0.00450000 BTC");
    }

    [Fact]
    public async Task ChannelsStillOpening_CountTowardsTheBalance()
    {
        // 500k + 450k still opening + 500k = 1.45M
        ArrangeNode(SwappingNode(), localSats: 500_000, pendingOpenSats: 450_000);

        var warning = await BuildService().ForChannelOpenAsync(NodeId, 500_000);

        warning.Should().Contain("0.01450000 BTC");
    }

    [Fact]
    public async Task AnOpenThatStaysUnderTheThreshold_DoesNotWarn()
    {
        ArrangeNode(SwappingNode(), localSats: 100_000);

        (await BuildService().ForChannelOpenAsync(NodeId, 500_000)).Should().BeNull();
    }

    [Fact]
    public async Task ANodeWithoutAutomaticSwapOut_DoesNotWarnNorReadItsBalance()
    {
        var node = SwappingNode();
        node.AutoLiquidityManagementEnabled = false;
        ArrangeNode(node, localSats: 5_000_000);

        (await BuildService().ForChannelOpenAsync(NodeId, 500_000)).Should().BeNull();
        await _lightningService.DidNotReceive().ChannelBalanceAsync(Arg.Any<Node>());
    }

    [Fact]
    public async Task AnUnknownNode_DoesNotWarn()
    {
        _nodeRepository.GetById(NodeId, false).Returns((Node?)null);

        (await BuildService().ForChannelOpenAsync(NodeId, 500_000)).Should().BeNull();
    }

    [Fact]
    public async Task ABalanceThatCannotBeRead_DoesNotWarn()
    {
        var node = SwappingNode();
        _nodeRepository.GetById(NodeId, false).Returns(node);
        _lightningService.ChannelBalanceAsync(node).Returns((ChannelBalanceResponse?)null);

        (await BuildService().ForChannelOpenAsync(NodeId, 500_000)).Should().BeNull();
    }
}
