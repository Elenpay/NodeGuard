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
using NodeGuard.Tests.Helpers;
using Quartz;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Jobs;


[Collection("RoutingEngine")]
public class ChannelFeeOptimizerJobTests
{
    private const string NodePubKey = "managedPubKey";
    private const ulong ChanId = 123;
    private const int ChannelDbId = 10;

    // The arranged channel is comfortably above ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS (10M).
    // Kept as one constant because the job gates on the DB SatsAmount while the control law reads
    // the LND balances — if the two drift the channel is silently filtered out of every test.
    private const long ChannelSizeSats = 16_000_000;

    private readonly Mock<ILogger<ChannelFeeOptimizerJob>> _logger = new();

    private readonly Mock<INodeRepository> _nodeRepository = new();
    private readonly Mock<IChannelRepository> _channelRepository = new();
    private readonly Mock<IChannelRoutingStateRepository> _routingStateRepository = new();
    private readonly Mock<IChannelFeeStateRepository> _feeStateRepository = new();
    private readonly Mock<IRebalanceRepository> _rebalanceRepository = new();
    private readonly Mock<IRebalanceService> _rebalanceService = new();
    private readonly Mock<ILightningService> _lightningService = new();
    private readonly Mock<ILightningClientService> _lightningClientService = new();

    // The real snapshot service over the mocked repos/LND, so these tests still cover the
    // open-channel + routing-state filtering that feeds the job.
    private IRoutingEngineSnapshotService BuildSnapshotService() =>
        new RoutingEngineSnapshotService(
            _routingStateRepository.Object,
            _feeStateRepository.Object,
            _lightningClientService.Object);

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
            SatsAmount = ChannelSizeSats,
            IsDynamicFeeEnabled = true,
            FundingTx = "txid123",
            FundingTxOutputIndex = 1,
        };
        _channelRepository.Setup(x => x.GetOpenChannels())
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
                    Capacity = ChannelSizeSats,
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
            _logger.Object,
            _nodeRepository.Object,
            _channelRepository.Object,
            _feeStateRepository.Object,
            _rebalanceRepository.Object,
            BuildSnapshotService(),
            _lightningService.Object);

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

        // Sink, d = -0.10, first eval seeds p_last=2500 → outbound 2550, inbound -50.
        _lightningService.Verify(x => x.SetChannelFeePolicy(
            "txid123:1", NodePubKey, 1000, 2550u, 40u, 0, -50, true), Times.Once);
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

    /// <summary>Asserts the engine wrote no fee policy to LND this run.</summary>
    private void VerifyNoFeeWrite() =>
        _lightningService.Verify(x => x.SetChannelFeePolicy(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<uint>(),
            It.IsAny<uint>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>()), Times.Never);

    [Fact]
    public async Task Execute_DryRunNode_RecordsFeeStateButDoesNotWriteToLnd()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        Constants.ROUTING_ENGINE_ENABLED = true;
        try
        {
            var node = BuildNode();
            node.RoutingEngineDryRun = true;
            ArrangeSingleSinkChannel(node, inFlightRebalance: false);

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            // The live policy is still read (for the untouched base fee/timelock), but nothing is written,
            // and the computed values are recorded so the operator can see what WOULD have been applied.
            _lightningService.Verify(x => x.GetChannelFeePolicy(ChanId, It.IsAny<Node>()), Times.Once);
            VerifyNoFeeWrite();
            _feeStateRepository.Verify(x => x.UpsertByChannelAndNode(
                It.Is<ChannelFeeState>(s => s.LastAppliedOutboundPpm == 2550u && s.LastFeeUpdateAt != null)), Times.Once);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
        }
    }

    [Fact]
    public async Task Execute_NoNodeWithDynamicFeesEnabled_SkipsBeforeFetchingChannels()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        Constants.ROUTING_ENGINE_ENABLED = true;
        try
        {
            var node = BuildNode();
            node.DynamicFeeManagementEnabled = false;
            ArrangeSingleSinkChannel(node, inFlightRebalance: false);

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            _channelRepository.Verify(x => x.GetOpenChannels(), Times.Never);
            VerifyNoFeeWrite();
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
        }
    }

    [Fact]
    public async Task Execute_NoRoutingStateForChannel_SkipsChannel()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        Constants.ROUTING_ENGINE_ENABLED = true;
        try
        {
            var node = BuildNode();
            ArrangeSingleSinkChannel(node, inFlightRebalance: false);
            // No signal yet: the sensor hasn't produced a routing state for this channel.
            _routingStateRepository.Setup(x => x.GetByManagedNodePubKey(NodePubKey)).ReturnsAsync(new List<ChannelRoutingState>());

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            _lightningService.Verify(x => x.GetChannelFeePolicy(It.IsAny<ulong>(), It.IsAny<Node>()), Times.Never);
            VerifyNoFeeWrite();
            _feeStateRepository.Verify(x => x.UpsertByChannelAndNode(It.IsAny<ChannelFeeState>()), Times.Never);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
        }
    }

    [Fact]
    public async Task Execute_ChannelBelowMinSize_SkipsChannel()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        var prevMinSize = Constants.ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS;
        Constants.ROUTING_ENGINE_ENABLED = true;
        // One sat above the arranged channel, so this pins the exact boundary of the size gate.
        Constants.ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS = ChannelSizeSats + 1;
        try
        {
            var node = BuildNode();
            ArrangeSingleSinkChannel(node, inFlightRebalance: false);

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            _lightningService.Verify(x => x.GetChannelFeePolicy(It.IsAny<ulong>(), It.IsAny<Node>()), Times.Never);
            VerifyNoFeeWrite();
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
            Constants.ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS = prevMinSize;
        }
    }

    [Fact]
    public async Task Execute_InsideDeadband_PersistsStateButDoesNotTouchLnd()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        Constants.ROUTING_ENGINE_ENABLED = true;
        try
        {
            var node = BuildNode();
            ArrangeSingleSinkChannel(node, inFlightRebalance: false);
            // ema == target ⇒ inside the fee deadband ⇒ NoOp.
            _routingStateRepository.Setup(x => x.GetByManagedNodePubKey(NodePubKey)).ReturnsAsync(new List<ChannelRoutingState>
            {
                new()
                {
                    ChannelId = ChannelDbId,
                    ManagedNodePubKey = NodePubKey,
                    ChanIdLnd = ChanId,
                    EmaLocalRatio = 0.50,
                    TargetLocalRatio = 0.50,
                    PeerFlowCategory = PeerFlowCategory.Sink,
                },
            });

            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            // NoOp channels never cost an LND round-trip, but the observed ratio/target are still persisted.
            _lightningService.Verify(x => x.GetChannelFeePolicy(It.IsAny<ulong>(), It.IsAny<Node>()), Times.Never);
            VerifyNoFeeWrite();
            _feeStateRepository.Verify(x => x.UpsertByChannelAndNode(It.IsAny<ChannelFeeState>()), Times.Once);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
        }
    }

    [Fact]
    public async Task Execute_SetFeePolicyThrows_IsSwallowed_AndStateNotPersisted()
    {
        var prevEnabled = Constants.ROUTING_ENGINE_ENABLED;
        Constants.ROUTING_ENGINE_ENABLED = true;
        try
        {
            var node = BuildNode();
            ArrangeSingleSinkChannel(node, inFlightRebalance: false);
            _lightningService.Setup(x => x.SetChannelFeePolicy(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<uint>(),
                It.IsAny<uint>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>()))
                .ThrowsAsync(new Exception("lnd unreachable"));

            // Must not surface — Execute swallows per-channel errors so the next cycle retries. If it threw,
            // this await would fail the test.
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            _lightningService.Verify(x => x.SetChannelFeePolicy(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<uint>(),
                It.IsAny<uint>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<bool>()), Times.Once);
            // On write failure the fee state is NOT persisted (LastApplied stays null → next cycle re-seeds).
            _feeStateRepository.Verify(x => x.UpsertByChannelAndNode(It.IsAny<ChannelFeeState>()), Times.Never);
        }
        finally
        {
            Constants.ROUTING_ENGINE_ENABLED = prevEnabled;
        }
    }

    [Fact]
    public async Task Execute_ChannelSharedWithAnotherManagedNode_IsStillActuated()
    {
        var node = BuildNode();
        ArrangeSingleSinkChannel(node, inFlightRebalance: false);

        // The peer is also managed by NodeGuard and it opened the channel. The old ownership dedup
        // handed the channel to the initiator alone, so this side was never actuated: it could not
        // see its own depleted channel as a rebalance destination and its outbound policy was
        // frozen. Both sides now carry their own state and both get actuated.
        var peer = new Node { Id = 21, PubKey = "peerPubKey", Name = "bob", DynamicFeeManagementEnabled = true };
        _nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(false)).ReturnsAsync(new List<Node> { node, peer });
        _routingStateRepository.Setup(x => x.GetByManagedNodePubKey(peer.PubKey)).ReturnsAsync(new List<ChannelRoutingState>());
        _feeStateRepository.Setup(x => x.GetByManagedNodePubKey(peer.PubKey)).ReturnsAsync(new List<ChannelFeeState>());

        _lightningClientService
            .Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lnrpc.Lightning.LightningClient>()))
            .ReturnsAsync(new Lnrpc.ListChannelsResponse
            {
                Channels =
                {
                    new Lnrpc.Channel
                    {
                        ChanId = ChanId,
                        Capacity = ChannelSizeSats,
                        LocalBalance = 4_000_000,
                        RemoteBalance = 12_000_000,
                        Active = true,
                        Initiator = false, // the managed peer opened it
                        RemotePubkey = peer.PubKey,
                    },
                },
            });

        await WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        _lightningService.Verify(x => x.SetChannelFeePolicy(
            "txid123:1", NodePubKey, 1000, 2550u, 40u, 0, -50, true), Times.Once);
    }

    [Fact]
    public async Task Execute_ColdStartFeeState_IsStampedWithTheActuatingNode()
    {
        var node = BuildNode();
        ArrangeSingleSinkChannel(node, inFlightRebalance: false);

        await WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        // A fee state created from scratch must name its node, or it lands outside every
        // per-node query and the control loop cold-starts forever.
        _feeStateRepository.Verify(x => x.UpsertByChannelAndNode(
            It.Is<ChannelFeeState>(f => f.ChannelId == ChannelDbId && f.ManagedNodePubKey == NodePubKey)),
            Times.Once);
    }

}
