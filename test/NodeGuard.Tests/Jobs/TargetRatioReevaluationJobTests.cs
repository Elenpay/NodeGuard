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
using Microsoft.Extensions.Logging;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using NodeGuard.Tests.Helpers;
using Quartz;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Jobs;

/// <summary>
/// Wiring tests for the Phase-1 sensor/classifier. The pure categorization math lives in
/// <see cref="PeerCategorizationServiceTests"/>; here the real <see cref="PeerCategorizationService"/>
/// is used so these prove the JOB feeds it correctly: age gate, ownership/eligibility filter, the
/// push/pull → net-flow sign convention, first-insert EMA seeding, failure handling, and the kill switch.
/// </summary>
public class TargetRatioReevaluationJobTests
{
    private const string NodePubKey = "alicePubKey";
    private const string PeerPubKey = "bobPubKey";
    private const int ChannelDbId = 10;

    // scid with funding block height 100 encoded in bits 63..40; lower bits are the tx/output index.
    private const ulong ChanId = (100UL << 40) | 7UL;
    private const uint FundingHeight = 100;

    private readonly Mock<INodeRepository> _nodeRepository = new();
    private readonly Mock<IChannelRepository> _channelRepository = new();
    private readonly Mock<IChannelRoutingStateRepository> _routingStateRepository = new();
    private readonly Mock<IForwardingHtlcEventRepository> _forwardingHtlcEventRepository = new();
    private readonly Mock<ILightningService> _lightningService = new();
    private readonly Mock<ILightningClientService> _lightningClientService = new();

    private static Node BuildNode() => new()
    {
        Id = 20,
        PubKey = NodePubKey,
        Name = "alice",
        DynamicFeeManagementEnabled = true,
    };

    private TargetRatioReevaluationJob BuildJob() =>
        new(
            new Mock<ILogger<TargetRatioReevaluationJob>>().Object,
            _nodeRepository.Object,
            _channelRepository.Object,
            _routingStateRepository.Object,
            _forwardingHtlcEventRepository.Object,
            new PeerCategorizationService(), // real pure classification math
            _lightningService.Object,
            _lightningClientService.Object);


    private Channel ArrangeSingleChannel(Node node, long localBalance, long remoteBalance,
        long push, long pull, uint chainTip, bool active = true, bool ownedInitiator = true)
    {
        _nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(false)).ReturnsAsync(new List<Node> { node });

        var dbChannel = new Channel
        {
            Id = ChannelDbId,
            ChanId = ChanId,
            Status = Channel.ChannelStatus.Open,
            IsDynamicFeeEnabled = true,
        };
        _channelRepository.Setup(x => x.GetOpenChannels()).ReturnsAsync(new List<Channel> { dbChannel });

        _lightningService.Setup(x => x.GetBlockHeight(It.IsAny<Node>())).ReturnsAsync((uint?)chainTip);

        var listResp = new Lnrpc.ListChannelsResponse
        {
            Channels =
            {
                new Lnrpc.Channel
                {
                    ChanId = ChanId,
                    Capacity = localBalance + remoteBalance,
                    LocalBalance = localBalance,
                    RemoteBalance = remoteBalance,
                    Active = active,
                    Initiator = ownedInitiator,
                    RemotePubkey = PeerPubKey,
                },
            },
        };
        _lightningClientService
            .Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lnrpc.Lightning.LightningClient>()))
            .ReturnsAsync(listResp);

        _routingStateRepository.Setup(x => x.GetByChannelId(ChannelDbId)).ReturnsAsync((ChannelRoutingState?)null);
        _forwardingHtlcEventRepository
            .Setup(x => x.GetOutgoingAmountMsat(NodePubKey, ChanId, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(push);
        _forwardingHtlcEventRepository
            .Setup(x => x.GetIncomingAmountMsat(NodePubKey, ChanId, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(pull);

        return dbChannel;
    }

    private ChannelRoutingState? _captured;

    /// <summary>Captures the state handed to UpsertByChannelId so a test can assert the persisted result.</summary>
    private void CaptureUpsert() =>
        _routingStateRepository
            .Setup(x => x.UpsertByChannelId(It.IsAny<ChannelRoutingState>()))
            .Callback<ChannelRoutingState>(s => _captured = s)
            .Returns(Task.CompletedTask);

    [Fact]
    public async Task Execute_KillSwitchOff_DoesNothing()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        Constants.ROUTING_ENGINE_ENABLED = false;
        try
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            _nodeRepository.Verify(x => x.GetAllManagedByNodeGuard(It.IsAny<bool>()), Times.Never);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
        }
    }

    [Fact]
    public async Task Execute_PushHeavyFlow_CategorizesSink_AndSeedsEma()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        var prevMinAge = Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS;
        var prevMinFlow = Constants.ROUTING_ENGINE_FLOW_MIN_MSAT;
        var prevThreshold = Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD;
        var prevHysteresis = Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES;
        
        Constants.ROUTING_ENGINE_ENABLED = true;
        Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = 10;
        Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = 100_000_000;
        Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = 0.25;
        Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = 1;
        
        try
        {
            var node = BuildNode();
            // Local ratio 0.75 (3M/4M); push-only flow ⇒ net-flow +1 ⇒ SINK. Aged well past the gate.
            ArrangeSingleChannel(node, localBalance: 3_000_000, remoteBalance: 1_000_000,
                push: 1_000_000_000, pull: 0, chainTip: 5000);
            CaptureUpsert();

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            var state = _captured;
            state.Should().NotBeNull();
            state!.PeerFlowCategory.Should().Be(PeerFlowCategory.Sink);
            state.NetFlowRatio.Should().Be(1.0);
            state.PushMsatWindow.Should().Be(1_000_000_000);
            state.PullMsatWindow.Should().Be(0);
            state.TargetLocalRatio.Should().BeGreaterThan(0.5, "a SINK's target drifts upward");
            state.ChanIdLnd.Should().Be(ChanId);
            state.ManagedNodePubKey.Should().Be(NodePubKey);
            state.AgeBlocks.Should().Be(5000 - FundingHeight);
            // First insert seeds EMA with the observed ratio (no 0.5 cold-start bias).
            state.EmaLocalRatio.Should().BeApproximately(0.75, 1e-9);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
            Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = prevMinAge;
            Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = prevMinFlow;
            Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = prevThreshold;
            Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = prevHysteresis;
        }
    }

    [Fact]
    public async Task Execute_PullHeavyFlow_CategorizesSource()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        var prevMinAge = Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS;
        var prevMinFlow = Constants.ROUTING_ENGINE_FLOW_MIN_MSAT;
        var prevThreshold = Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD;
        var prevHysteresis = Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES;
        
        Constants.ROUTING_ENGINE_ENABLED = true;
        Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = 10;
        Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = 100_000_000;
        Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = 0.25;
        Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = 1;
        
        try
        {
            var node = BuildNode();
            // Pull-only flow ⇒ net-flow -1 ⇒ SOURCE, target drifts below 0.5.
            ArrangeSingleChannel(node, localBalance: 1_000_000, remoteBalance: 3_000_000,
                push: 0, pull: 1_000_000_000, chainTip: 5000);
            CaptureUpsert();

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            var state = _captured;
            state.Should().NotBeNull();
            state!.PeerFlowCategory.Should().Be(PeerFlowCategory.Source);
            state.NetFlowRatio.Should().Be(-1.0);
            state.TargetLocalRatio.Should().BeLessThan(0.5, "a SOURCE's target drifts downward");
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
            Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = prevMinAge;
            Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = prevMinFlow;
            Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = prevThreshold;
            Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = prevHysteresis;
        }
    }

    [Fact]
    public async Task Execute_YoungChannel_StaysUncategorized_ButStillSensesFlow()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        var prevMinAge = Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS;
        var prevMinFlow = Constants.ROUTING_ENGINE_FLOW_MIN_MSAT;
        var prevThreshold = Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD;
        var prevHysteresis = Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES;
        
        Constants.ROUTING_ENGINE_ENABLED = true;
        Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = 10;
        Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = 100_000_000;
        Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = 0.25;
        Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = 1;
        
        try
        {
            var node = BuildNode();
            // Same strong push flow, but chainTip only 5 blocks past funding ⇒ below the 10-block age gate.
            ArrangeSingleChannel(node, localBalance: 3_000_000, remoteBalance: 1_000_000,
                push: 1_000_000_000, pull: 0, chainTip: FundingHeight + 5);
            CaptureUpsert();

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            var state = _captured;
            state.Should().NotBeNull();
            state!.AgeBlocks.Should().Be(5);
            state.PeerFlowCategory.Should().Be(PeerFlowCategory.Uncategorized, "the age gate blocks categorization");
            state.TargetLocalRatio.Should().Be(0.5, "target holds at neutral until the channel is old enough");
            // Flow is still measured even while categorization is gated.
            state.NetFlowRatio.Should().Be(1.0);
            state.PushMsatWindow.Should().Be(1_000_000_000);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
            Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = prevMinAge;
            Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = prevMinFlow;
            Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = prevThreshold;
            Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = prevHysteresis;
        }
    }

    [Fact]
    public async Task Execute_PeerInitiatedChannelToManagedPeer_IsSkipped()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        var prevMinAge = Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS;
        var prevMinFlow = Constants.ROUTING_ENGINE_FLOW_MIN_MSAT;
        var prevThreshold = Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD;
        var prevHysteresis = Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES;
        
        Constants.ROUTING_ENGINE_ENABLED = true;
        Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = 10;
        Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = 100_000_000;
        Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = 0.25;
        Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = 1;
        
        try
        {
            var node = BuildNode();
            var peer = new Node { Id = 21, PubKey = PeerPubKey, Name = "bob", DynamicFeeManagementEnabled = true };

            // Both nodes are managed; the channel is peer-initiated ⇒ the dedup rule assigns it to the peer.
            _nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(false)).ReturnsAsync(new List<Node> { node, peer });
            _lightningService.Setup(x => x.GetBlockHeight(It.IsAny<Node>())).ReturnsAsync((uint?)5000);

            var dbChannel = new Channel { Id = ChannelDbId, ChanId = ChanId, Status = Channel.ChannelStatus.Open, IsDynamicFeeEnabled = true };
            _channelRepository.Setup(x => x.GetOpenChannels()).ReturnsAsync(new List<Channel> { dbChannel });

            var aliceList = new Lnrpc.ListChannelsResponse
            {
                Channels = { new Lnrpc.Channel { ChanId = ChanId, Capacity = 4_000_000, LocalBalance = 2_000_000, RemoteBalance = 2_000_000, Active = true, Initiator = false, RemotePubkey = PeerPubKey } },
            };
            _lightningClientService.Setup(x => x.ListChannels(It.Is<Node>(n => n.PubKey == NodePubKey), It.IsAny<Lnrpc.Lightning.LightningClient>())).ReturnsAsync(aliceList);
            _lightningClientService.Setup(x => x.ListChannels(It.Is<Node>(n => n.PubKey == PeerPubKey), It.IsAny<Lnrpc.Lightning.LightningClient>())).ReturnsAsync(new Lnrpc.ListChannelsResponse());

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            _routingStateRepository.Verify(x => x.GetByChannelId(It.IsAny<int>()), Times.Never);
            _routingStateRepository.Verify(x => x.UpsertByChannelId(It.IsAny<ChannelRoutingState>()), Times.Never);
            _forwardingHtlcEventRepository.Verify(x => x.GetOutgoingAmountMsat(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<DateTimeOffset>()), Times.Never);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
            Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = prevMinAge;
            Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = prevMinFlow;
            Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = prevThreshold;
            Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = prevHysteresis;
        }
    }

    [Fact]
    public async Task Execute_InactiveChannel_IsSkipped()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        var prevMinAge = Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS;
        var prevMinFlow = Constants.ROUTING_ENGINE_FLOW_MIN_MSAT;
        var prevThreshold = Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD;
        var prevHysteresis = Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES;
        
        Constants.ROUTING_ENGINE_ENABLED = true;
        Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = 10;
        Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = 100_000_000;
        Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = 0.25;
        Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = 1;
        
        try
        {
            var node = BuildNode();
            ArrangeSingleChannel(node, localBalance: 3_000_000, remoteBalance: 1_000_000,
                push: 1_000_000_000, pull: 0, chainTip: 5000, active: false);

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            _routingStateRepository.Verify(x => x.GetByChannelId(It.IsAny<int>()), Times.Never);
            _routingStateRepository.Verify(x => x.UpsertByChannelId(It.IsAny<ChannelRoutingState>()), Times.Never);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
            Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = prevMinAge;
            Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = prevMinFlow;
            Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = prevThreshold;
            Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = prevHysteresis;
        }
    }

    [Fact]
    public async Task Execute_BlockHeightUnavailable_SkipsNodeWithoutThrowing()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        var prevMinAge = Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS;
        var prevMinFlow = Constants.ROUTING_ENGINE_FLOW_MIN_MSAT;
        var prevThreshold = Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD;
        var prevHysteresis = Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES;
        
        Constants.ROUTING_ENGINE_ENABLED = true;
        Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = 10;
        Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = 100_000_000;
        Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = 0.25;
        Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = 1;
        
        try
        {
            var node = BuildNode();
            _nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(false)).ReturnsAsync(new List<Node> { node });
            _channelRepository.Setup(x => x.GetOpenChannels()).ReturnsAsync(new List<Channel>());
            _lightningService.Setup(x => x.GetBlockHeight(It.IsAny<Node>())).ReturnsAsync((uint?)null);

            var act = async () => await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            await act.Should().NotThrowAsync();
            _lightningClientService.Verify(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lnrpc.Lightning.LightningClient>()), Times.Never);
            _routingStateRepository.Verify(x => x.UpsertByChannelId(It.IsAny<ChannelRoutingState>()), Times.Never);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
            Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = prevMinAge;
            Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = prevMinFlow;
            Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = prevThreshold;
            Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = prevHysteresis;
        }
    }

    [Fact]
    public async Task Execute_ListChannelsUnavailable_SkipsNodeWithoutThrowing()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        var prevMinAge = Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS;
        var prevMinFlow = Constants.ROUTING_ENGINE_FLOW_MIN_MSAT;
        var prevThreshold = Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD;
        var prevHysteresis = Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES;
        
        Constants.ROUTING_ENGINE_ENABLED = true;
        Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = 10;
        Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = 100_000_000;
        Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = 0.25;
        Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = 1;
        
        try
        {
            var node = BuildNode();
            _nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(false)).ReturnsAsync(new List<Node> { node });
            _channelRepository.Setup(x => x.GetOpenChannels()).ReturnsAsync(new List<Channel>());
            _lightningService.Setup(x => x.GetBlockHeight(It.IsAny<Node>())).ReturnsAsync((uint?)5000);
            _lightningClientService
                .Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lnrpc.Lightning.LightningClient>()))
                .ReturnsAsync((Lnrpc.ListChannelsResponse?)null);

            var act = async () => await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            await act.Should().NotThrowAsync();
            _forwardingHtlcEventRepository.Verify(x => x.GetOutgoingAmountMsat(It.IsAny<string>(), It.IsAny<ulong>(), It.IsAny<DateTimeOffset>()), Times.Never);
            _routingStateRepository.Verify(x => x.UpsertByChannelId(It.IsAny<ChannelRoutingState>()), Times.Never);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
            Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS = prevMinAge;
            Constants.ROUTING_ENGINE_FLOW_MIN_MSAT = prevMinFlow;
            Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD = prevThreshold;
            Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES = prevHysteresis;
        }
    }
}
