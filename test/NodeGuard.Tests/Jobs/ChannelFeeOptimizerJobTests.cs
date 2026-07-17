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

using Microsoft.Extensions.Logging;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Quartz;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Jobs;

public class ChannelFeeOptimizerJobTests
{
    private const string NodePubKey = "managedPubKey";
    private const ulong ChanId = 123;
    private const int ChannelDbId = 10;

    private readonly Mock<INodeRepository> _nodeRepository = new();
    private readonly Mock<IChannelRepository> _channelRepository = new();
    private readonly Mock<IChannelRoutingStateRepository> _routingStateRepository = new();
    private readonly Mock<IChannelFeeStateRepository> _feeStateRepository = new();
    private readonly Mock<IForwardingHtlcEventRepository> _forwardingHtlcEventRepository = new();
    private readonly Mock<IRebalanceRepository> _rebalanceRepository = new();
    private readonly Mock<ILightningService> _lightningService = new();
    private readonly Mock<ILightningClientService> _lightningClientService = new();

    private Node BuildNode() => new()
    {
        Id = 20,
        PubKey = NodePubKey,
        Name = "alice",
        DynamicFeeManagementEnabled = true,
        AllowPositiveInboundFees = true,
    };

    private void ArrangeSingleSinkChannel(Node node, bool inFlightRebalance)
    {
        _nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(false)).ReturnsAsync(new List<Node> { node });

        var dbChannel = new Channel
        {
            Id = ChannelDbId,
            ChanId = ChanId,
            Status = Channel.ChannelStatus.Open,
            IsDynamicFeeEnabled = true,
            FundingTx = "txid123",
            FundingTxOutputIndex = 1,
        };
        _channelRepository.Setup(x => x.GetChannelsFeeEngine())
            .ReturnsAsync(new List<Channel> { dbChannel });

        // Too remote (ema 0.40 < target 0.50) + Sink → raise outbound, negative inbound.
        _routingStateRepository.Setup(x => x.GetByManagedNodePubKey(NodePubKey)).ReturnsAsync(new List<ChannelRoutingState>
        {
            new()
            {
                ChannelId = ChannelDbId,
                ManagedNodePubKey = NodePubKey,
                ChanIdLnd = ChanId,
                EmaLocalRatio = 0.40,
                TargetLocalRatio = 0.50,
                PeerFlowCategory = PeerFlowCategory.Sink,
            },
        });
        _feeStateRepository.Setup(x => x.GetByManagedNodePubKey(NodePubKey)).ReturnsAsync(new List<ChannelFeeState>());
        _rebalanceRepository.Setup(x => x.GetPendingInFlightSourceChannelIds())
            .ReturnsAsync(inFlightRebalance ? new HashSet<int> { ChannelDbId } : new HashSet<int>());

        var listResp = new Lnrpc.ListChannelsResponse
        {
            Channels =
            {
                new Lnrpc.Channel
                {
                    ChanId = ChanId,
                    Capacity = 16_000_000,
                    LocalBalance = 4_000_000,
                    RemoteBalance = 12_000_000,
                    Active = true,
                    Initiator = true,
                    RemotePubkey = "peerPubKey",
                },
            },
        };
        _lightningClientService
            .Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lnrpc.Lightning.LightningClient>()))
            .ReturnsAsync(listResp);

        _lightningService
            .Setup(x => x.GetChannelFeePolicy(ChanId, It.IsAny<Node>()))
            .ReturnsAsync((new Lnrpc.RoutingPolicy
            {
                FeeBaseMsat = 1000,
                FeeRateMilliMsat = 500,
                TimeLockDelta = 40,
                InboundFeeBaseMsat = 0,
                InboundFeeRateMilliMsat = 0,
            }, (Lnrpc.RoutingPolicy?)null));
    }

    private ChannelFeeOptimizerJob BuildJob() =>
        new(
            new Mock<ILogger<ChannelFeeOptimizerJob>>().Object,
            _nodeRepository.Object,
            _channelRepository.Object,
            _routingStateRepository.Object,
            _feeStateRepository.Object,
            _forwardingHtlcEventRepository.Object,
            _rebalanceRepository.Object,
            new FeeOptimizerService(), // real pure decision logic
            _lightningService.Object,
            _lightningClientService.Object);

    private static async Task WithEngine(bool enabled, Func<Task> body)
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        Constants.ROUTING_ENGINE_ENABLED = enabled;
        try
        {
            await body();
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
        }
    }

    [Fact]
    public async Task Execute_LiveNode_AppliesComputedPolicy()
    {
        var node = BuildNode();
        ArrangeSingleSinkChannel(node, inFlightRebalance: false);

        await WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        // Sink, d = -0.10, first eval seeds p_last=2500 → outbound 2550, inbound -25.
        _lightningService.Verify(x => x.SetChannelFeePolicy(
            "txid123:1", NodePubKey, 1000, 2550u, 40u, 0, -25, true), Times.Once);
    }

    [Fact]
    public async Task Execute_InFlightRebalance_SkipsChannelEntirely()
    {
        var node = BuildNode();
        ArrangeSingleSinkChannel(node, inFlightRebalance: true);

        await WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        // Authority split: skipped before reading policy or writing.
        _lightningService.Verify(x => x.GetChannelFeePolicy(It.IsAny<ulong>(), It.IsAny<Node>()), Times.Never);
        _lightningService.Verify(x => x.SetChannelFeePolicy(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<uint>(),
            It.IsAny<uint>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task Execute_KillSwitchOff_DoesNothing()
    {
        var node = BuildNode();
        ArrangeSingleSinkChannel(node, inFlightRebalance: false);

        await WithEngine(enabled: false, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        _nodeRepository.Verify(x => x.GetAllManagedByNodeGuard(It.IsAny<bool>()), Times.Never);
    }
}
