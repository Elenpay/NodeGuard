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
using NodeGuard.Tests.Jobs;
using Quartz;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Jobs;


[Collection("RoutingEngine")]
public class AutoRebalanceJobTests
{
    private const string NodePubKey = "managedPubKey";

    private readonly Mock<ILogger<AutoRebalanceJob>> _logger = new();

    private readonly Mock<INodeRepository> _nodeRepository = new();
    private readonly Mock<IChannelRepository> _channelRepository = new();
    private readonly Mock<IChannelRoutingStateRepository> _routingStateRepository = new();
    private readonly Mock<IChannelFeeStateRepository> _feeStateRepository = new();
    private readonly Mock<IRebalanceRepository> _rebalanceRepository = new();
    private readonly Mock<IRebalanceService> _rebalanceService = new();
    private readonly Mock<ILightningService> _lightningService = new();
    private readonly Mock<ILightningClientService> _lightningClientService = new();
    private readonly Mock<IAuditService> _auditService = new();

    // The real snapshot service over the mocked repos/LND, so these tests still cover the
    // open-channel + routing-state filtering that feeds the job.
    private IRoutingEngineSnapshotService BuildSnapshotService() =>
        new RoutingEngineSnapshotService(
            _routingStateRepository.Object,
            _feeStateRepository.Object,
            _lightningClientService.Object);

    /// <summary>A NodeGuard channel row the rebalancer will consider, opted in or not.</summary>
    private static Channel Db(int id, ulong chanId, bool optIn) => new()
    {
        Id = id, ChanId = chanId, Status = Channel.ChannelStatus.Open,
        IsAutoRebalanceEnabled = optIn,
        FundingTx = $"tx{id}", FundingTxOutputIndex = 0,
    };

    /// <summary>The matching LND channel, with the balances that drive sizing.</summary>
    private static Lnrpc.Channel Lnd(ulong chanId, long local, long remote, string peer) => new()
    {
        ChanId = chanId, Capacity = 20_000_000, LocalBalance = local, RemoteBalance = remote,
        Active = true, Initiator = true, RemotePubkey = peer,
    };

    /// <summary>
    /// A scope factory whose scope hands back <see cref="_rebalanceService"/>, mirroring how the job
    /// resolves a fresh IRebalanceService per dispatch so the payment doesn't run on the job's own
    /// (soon-disposed) scope.
    /// </summary>
    private AutoRebalanceJob BuildJob() =>
        new(
            _logger.Object,
            _nodeRepository.Object,
            _channelRepository.Object,
            _rebalanceRepository.Object,
            _rebalanceService.Object,
            BuildSnapshotService(),
            _lightningService.Object,
            _auditService.Object);



    /// <summary>
    /// A drainable source (too-local 0.75 vs 0.50, cheap 50 ppm) and a depleted destination on a
    /// different peer (0.10 vs 0.50, dear 2500 ppm), on a node with budget to spend.
    /// <paramref name="sourceOptedIn"/> drives the per-channel rebalance opt-in;
    /// <paramref name="sourceLiquidityFlag"/> drives the unrelated swap-liquidity flag;
    /// <paramref name="rebalanceTimeoutSeconds"/> leaves the node's timeout override unset when null.
    /// </summary>
    private void ArrangeRebalancePair(bool sourceOptedIn, bool sourceLiquidityFlag = false,
        int? rebalanceTimeoutSeconds = null)
    {
        var node = new Node
        {
            Id = 20,
            PubKey = NodePubKey,
            Name = "alice",
            AutoRebalanceEnabled = true,
            RebalanceBudgetSats = 1_000_000,
            MaxRebalancesInFlight = 5,
            MaxRebalanceCostToEarnRatio = 0.5,
            RebalanceTimeoutSeconds = rebalanceTimeoutSeconds,
        };
        _nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(false)).ReturnsAsync(new List<Node> { node });

        var sourceDb = new Channel
        {
            Id = 101, ChanId = 1001, Status = Channel.ChannelStatus.Open,
            IsDynamicFeeEnabled = true,
            IsAutoRebalanceEnabled = sourceOptedIn,
            IsAutomatedLiquidityEnabled = sourceLiquidityFlag,
            FundingTx = "txS", FundingTxOutputIndex = 0,
        };
        var destDb = new Channel
        {
            Id = 102, ChanId = 1002, Status = Channel.ChannelStatus.Open,
            IsDynamicFeeEnabled = true, IsAutoRebalanceEnabled = false,
            FundingTx = "txD", FundingTxOutputIndex = 0,
        };
        _channelRepository.Setup(x => x.GetOpenChannels()).ReturnsAsync(new List<Channel> { sourceDb, destDb });

        _routingStateRepository.Setup(x => x.GetByManagedNodePubKey(NodePubKey)).ReturnsAsync(new List<ChannelRoutingState>
        {
            new() { ChannelId = 101, ManagedNodePubKey = NodePubKey, ChanIdLnd = 1001, EmaLocalRatio = 0.75, TargetLocalRatio = 0.50, PeerFlowCategory = PeerFlowCategory.Source },
            new() { ChannelId = 102, ManagedNodePubKey = NodePubKey, ChanIdLnd = 1002, EmaLocalRatio = 0.10, TargetLocalRatio = 0.50, PeerFlowCategory = PeerFlowCategory.Sink },
        });
        _feeStateRepository.Setup(x => x.GetByManagedNodePubKey(NodePubKey)).ReturnsAsync(new List<ChannelFeeState>());

        _rebalanceRepository.Setup(x => x.GetPendingInFlightSourceChannelIds()).ReturnsAsync(new HashSet<int>());
        _rebalanceRepository.Setup(x => x.GetPessimisticConsumedFeesSince(node.Id, It.IsAny<DateTimeOffset>())).ReturnsAsync(0L);
        _rebalanceRepository.Setup(x => x.GetInFlightByNode(node.Id)).ReturnsAsync(0);

        var listResp = new Lnrpc.ListChannelsResponse
        {
            Channels =
            {
                new Lnrpc.Channel { ChanId = 1001, Capacity = 20_000_000, LocalBalance = 15_000_000, RemoteBalance = 5_000_000, Active = true, Initiator = true, RemotePubkey = "peerS" },
                new Lnrpc.Channel { ChanId = 1002, Capacity = 20_000_000, LocalBalance = 2_000_000, RemoteBalance = 18_000_000, Active = true, Initiator = true, RemotePubkey = "peerD" },
            },
        };
        _lightningClientService
            .Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lnrpc.Lightning.LightningClient>()))
            .ReturnsAsync(listResp);

        _lightningService.Setup(x => x.GetLocalOutboundFeeRatesPpmAsync(It.IsAny<Node>()))
            .ReturnsAsync(new Dictionary<ulong, long> { [1001] = 50, [1002] = 2500 });

        _rebalanceService.Setup(x => x.RebalanceAsync(It.IsAny<RebalanceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Rebalance());
    }


    [Fact]
    public async Task Execute_DispatchesThePlannedRebalance()
    {
        ArrangeRebalancePair(sourceOptedIn: true);

        await RoutingEngineSwitch.WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        // Source 101 -> dest peer "peerD", sized to the excess, gate 0.5 x 2500ppm.
        _rebalanceService.Verify(x => x.RebalanceAsync(
            It.Is<RebalanceRequest>(r => r.SourceChannelId == 101 && r.TargetPubkey == "peerD"
                && r.AmountSats == 5_000_000 && !r.IsManual
                && r.MaxFeePct.HasValue && Math.Abs(r.MaxFeePct.Value - 0.125) < 1e-9),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_UsesTheNodesTimeoutOverride_WhenSet()
    {
        ArrangeRebalancePair(sourceOptedIn: true, rebalanceTimeoutSeconds: 45);

        await RoutingEngineSwitch.WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        _rebalanceService.Verify(x => x.RebalanceAsync(
            It.Is<RebalanceRequest>(r => r.TimeoutSeconds == 45),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_FallsBackToTheConstantTimeout_WhenTheNodeLeavesItUnset()
    {
        ArrangeRebalancePair(sourceOptedIn: true);

        await RoutingEngineSwitch.WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        _rebalanceService.Verify(x => x.RebalanceAsync(
            It.Is<RebalanceRequest>(r => r.TimeoutSeconds == Constants.ROUTING_ENGINE_REBALANCE_TIMEOUT_SECONDS),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Execute_AwaitsEachPayment_BeforeDispatchingTheNext()
    {
        ArrangeTwoRebalancePairs(maxRebalancesInFlight: 5);

        // Two payments we control. Everything else the job awaits is mocked with completed tasks, so
        // Execute runs straight through and hands the task back only once it is parked on the first
        // payment — which is what makes the assertions below deterministic rather than timing-based.
        var first = new TaskCompletionSource<Rebalance>();
        var second = new TaskCompletionSource<Rebalance>();
        var started = 0;
        _rebalanceService.Setup(x => x.RebalanceAsync(It.IsAny<RebalanceRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() => Interlocked.Increment(ref started) == 1 ? first.Task : second.Task);

        await RoutingEngineSwitch.WithEngine(enabled: true, async () =>
        {
            var run = BuildJob().Execute(Mock.Of<IJobExecutionContext>());

            // Detached dispatch would have finished the run; concurrent dispatch would have started
            // both payments. Being parked after exactly one is what "awaits each" means.
            Assert.False(run.IsCompleted);
            Assert.Equal(1, started);

            first.SetResult(new Rebalance());
            second.SetResult(new Rebalance());

            await run;
            Assert.Equal(2, started);
        });
    }

    [Fact]
    public async Task Execute_KillSwitchOff_DoesNothing()
    {
        ArrangeRebalancePair(sourceOptedIn: true);

        await RoutingEngineSwitch.WithEngine(enabled: false, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        _nodeRepository.Verify(x => x.GetAllManagedByNodeGuard(It.IsAny<bool>()), Times.Never);
        _rebalanceService.Verify(x => x.RebalanceAsync(
            It.IsAny<RebalanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Execute_SwapLiquidityFlagAloneDoesNotOptAChannelIn()
    {
        // The rebalancer used to borrow IsAutomatedLiquidityEnabled, which means "opted into
        // swap-based liquidity rules". They are separate opt-ins now: a channel carrying only the
        // swap flag must not be drained.
        ArrangeRebalancePair(sourceOptedIn: false, sourceLiquidityFlag: true);

        await RoutingEngineSwitch.WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        _rebalanceService.Verify(x => x.RebalanceAsync(
            It.IsAny<RebalanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Two drainable sources and two depleted destination peers, so the planner produces two plans,
    /// on a node pinned to one in-flight rebalance so the second plan is dropped. Pairing is
    /// first-fit in classification order, so chan 1001 pairs with peerD1 and chan 1003 with peerD2.
    /// </summary>
    private void ArrangeTwoRebalancePairs(int maxRebalancesInFlight = 1)
    {
        var node = new Node
        {
            Id = 20,
            PubKey = NodePubKey,
            Name = "alice",
            // Fee pass off: this test is only about rebalance dispatch accounting.
            DynamicFeeManagementEnabled = false,
            AutoRebalanceEnabled = true,
            RebalanceBudgetSats = 1_000_000,
            MaxRebalanceCostToEarnRatio = 0.5,
            // Pinned, not inherited, so the cap tests don't depend on
            // ROUTING_ENGINE_REBALANCE_DEFAULT_MAX_IN_FLIGHT.
            MaxRebalancesInFlight = maxRebalancesInFlight,
        };
        _nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(false)).ReturnsAsync(new List<Node> { node });


        _channelRepository.Setup(x => x.GetOpenChannels()).ReturnsAsync(new List<Channel>
        {
            Db(101, 1001, optIn: true),  // source 1
            Db(102, 1002, optIn: false), // destination 1
            Db(103, 1003, optIn: true),  // source 2
            Db(104, 1004, optIn: false), // destination 2
        });

        _routingStateRepository.Setup(x => x.GetByManagedNodePubKey(NodePubKey)).ReturnsAsync(new List<ChannelRoutingState>
        {
            new() { ChannelId = 101, ManagedNodePubKey = NodePubKey, ChanIdLnd = 1001, EmaLocalRatio = 0.75, TargetLocalRatio = 0.50, PeerFlowCategory = PeerFlowCategory.Source },
            new() { ChannelId = 102, ManagedNodePubKey = NodePubKey, ChanIdLnd = 1002, EmaLocalRatio = 0.10, TargetLocalRatio = 0.50, PeerFlowCategory = PeerFlowCategory.Sink },
            new() { ChannelId = 103, ManagedNodePubKey = NodePubKey, ChanIdLnd = 1003, EmaLocalRatio = 0.75, TargetLocalRatio = 0.50, PeerFlowCategory = PeerFlowCategory.Source },
            new() { ChannelId = 104, ManagedNodePubKey = NodePubKey, ChanIdLnd = 1004, EmaLocalRatio = 0.10, TargetLocalRatio = 0.50, PeerFlowCategory = PeerFlowCategory.Sink },
        });
        _feeStateRepository.Setup(x => x.GetByManagedNodePubKey(NodePubKey)).ReturnsAsync(new List<ChannelFeeState>());

        _rebalanceRepository.Setup(x => x.GetPendingInFlightSourceChannelIds()).ReturnsAsync(new HashSet<int>());
        _rebalanceRepository.Setup(x => x.GetPessimisticConsumedFeesSince(node.Id, It.IsAny<DateTimeOffset>())).ReturnsAsync(0L);
        _rebalanceRepository.Setup(x => x.GetInFlightByNode(node.Id)).ReturnsAsync(0);


        _lightningClientService
            .Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lnrpc.Lightning.LightningClient>()))
            .ReturnsAsync(new Lnrpc.ListChannelsResponse
            {
                Channels =
                {
                    Lnd(1001, 15_000_000, 5_000_000, "peerS1"),
                    Lnd(1002, 2_000_000, 18_000_000, "peerD1"),
                    Lnd(1003, 15_000_000, 5_000_000, "peerS2"),
                    Lnd(1004, 2_000_000, 18_000_000, "peerD2"),
                },
            });

        _lightningService.Setup(x => x.GetLocalOutboundFeeRatesPpmAsync(It.IsAny<Node>()))
            .ReturnsAsync(new Dictionary<ulong, long>
            {
                [1001] = 50, [1002] = 2500, [1003] = 60, [1004] = 2400,
            });

        _rebalanceService.Setup(x => x.RebalanceAsync(It.IsAny<RebalanceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Rebalance());
    }

    [Fact]
    public async Task Execute_LogsPlansDroppedByTheInFlightCap()
    {
        ArrangeTwoRebalancePairs();

        await RoutingEngineSwitch.WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        // The cap is 1, so only the first plan is dispatched...
        _rebalanceService.Verify(x => x.RebalanceAsync(
            It.Is<RebalanceRequest>(r => r.SourceChannelId == 101 && r.TargetPubkey == "peerD1"),
            It.IsAny<CancellationToken>()), Times.Once);
        _rebalanceService.Verify(x => x.RebalanceAsync(
            It.IsAny<RebalanceRequest>(), It.IsAny<CancellationToken>()), Times.Once);

        // ...and the plan the cap ate is reported rather than silently discarded.
        _logger.Verify(x => x.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("dropped 1 of 2 planned rebalance(s)")
                                          && v.ToString()!.Contains("in-flight cap reached (1/1")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);

        // The dropped plan's own details, so an operator can see what was left on the table.
        _logger.Verify(x => x.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("dropped plan")
                                          && v.ToString()!.Contains("drain chan 1003")
                                          && v.ToString()!.Contains("refill peer peerD2")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Execute_PricesEveryChannelInOneRoundTrip()
    {
        var node = new Node
        {
            Id = 20,
            PubKey = NodePubKey,
            Name = "alice",
            AutoRebalanceEnabled = true,
            RebalanceBudgetSats = 1_000_000,
            MaxRebalancesInFlight = 5,
            MaxRebalanceCostToEarnRatio = 0.5,
        };
        _nodeRepository.Setup(x => x.GetAllManagedByNodeGuard(false)).ReturnsAsync(new List<Node> { node });

        _channelRepository.Setup(x => x.GetOpenChannels()).ReturnsAsync(new List<Channel>
        {
            Db(101, 1001, optIn: true),   // detected source
            Db(102, 1002, optIn: false),  // detected destination
            Db(103, 1003, optIn: true),   // fallback source only
        });

        _routingStateRepository.Setup(x => x.GetByManagedNodePubKey(NodePubKey)).ReturnsAsync(new List<ChannelRoutingState>
        {
            new() { ChannelId = 101, ManagedNodePubKey = NodePubKey, ChanIdLnd = 1001, EmaLocalRatio = 0.75, TargetLocalRatio = 0.50, PeerFlowCategory = PeerFlowCategory.Source },
            new() { ChannelId = 102, ManagedNodePubKey = NodePubKey, ChanIdLnd = 1002, EmaLocalRatio = 0.10, TargetLocalRatio = 0.50, PeerFlowCategory = PeerFlowCategory.Sink },
            new() { ChannelId = 103, ManagedNodePubKey = NodePubKey, ChanIdLnd = 1003, EmaLocalRatio = 0.60, TargetLocalRatio = 0.50, PeerFlowCategory = PeerFlowCategory.Bidirectional },
        });

        _rebalanceRepository.Setup(x => x.GetPendingInFlightSourceChannelIds()).ReturnsAsync(new HashSet<int>());
        _rebalanceRepository.Setup(x => x.GetPessimisticConsumedFeesSince(node.Id, It.IsAny<DateTimeOffset>())).ReturnsAsync(0L);
        _rebalanceRepository.Setup(x => x.GetInFlightByNode(node.Id)).ReturnsAsync(0);

        _lightningClientService
            .Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lnrpc.Lightning.LightningClient>()))
            .ReturnsAsync(new Lnrpc.ListChannelsResponse
            {
                Channels =
                {
                    new Lnrpc.Channel { ChanId = 1001, Capacity = 20_000_000, LocalBalance = 15_000_000, RemoteBalance = 5_000_000, Active = true, RemotePubkey = "peerS" },
                    new Lnrpc.Channel { ChanId = 1002, Capacity = 20_000_000, LocalBalance = 2_000_000, RemoteBalance = 18_000_000, Active = true, RemotePubkey = "peerD" },
                    new Lnrpc.Channel { ChanId = 1003, Capacity = 20_000_000, LocalBalance = 14_000_000, RemoteBalance = 6_000_000, Active = true, RemotePubkey = "peerF" },
                },
            });

        _lightningService.Setup(x => x.GetLocalOutboundFeeRatesPpmAsync(It.IsAny<Node>()))
            .ReturnsAsync(new Dictionary<ulong, long> { [1001] = 2000, [1002] = 2000, [1003] = 2000 });
        _rebalanceService.Setup(x => x.RebalanceAsync(It.IsAny<RebalanceRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Rebalance());

        await RoutingEngineSwitch.WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        // Three channels across a detected source, a detected destination and a fallback source —
        // all priced by ONE round-trip. The old per-channel path cost a GetChanInfo each and had to
        // reason about which subset the planner would consult; FeeReport removes that decision.
        _lightningService.Verify(x => x.GetLocalOutboundFeeRatesPpmAsync(It.IsAny<Node>()), Times.Once);
        _lightningService.Verify(x => x.GetLocalOutboundFeeRatePpmAsync(
            It.IsAny<Node>(), It.IsAny<ulong>()), Times.Never);
    }

    [Fact]
    public async Task Execute_FeeReportUnavailable_SkipsTheNodeWithoutDispatching()
    {
        ArrangeRebalancePair(sourceOptedIn: true);
        // Null, not an empty map: no rate for any channel means nothing can be profit-gated, and
        // treating that as "everything earns zero" would silently drop every plan as unprofitable.
        _lightningService.Setup(x => x.GetLocalOutboundFeeRatesPpmAsync(It.IsAny<Node>()))
            .ReturnsAsync((Dictionary<ulong, long>?)null);

        await RoutingEngineSwitch.WithEngine(enabled: true, async () =>
        {
            await BuildJob().Execute(Mock.Of<IJobExecutionContext>());
        });

        _rebalanceService.Verify(x => x.RebalanceAsync(
            It.IsAny<RebalanceRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // Skipped deliberately, not by blowing up: without the warning this test would also pass
        // if BuildPlans threw on a null map and the per-node catch swallowed it.
        _logger.Verify(x => x.Log(
            LogLevel.Warning,
            It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("FeeReport unavailable")),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
        _logger.Verify(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Never);
    }
}
