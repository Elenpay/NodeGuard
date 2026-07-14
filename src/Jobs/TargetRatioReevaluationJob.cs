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
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Quartz;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Jobs;

/// <summary>
/// For every owned, open channel it refreshes ChannelRoutingState — EMA-smoothed
/// local ratio, net-flow, peer category (with hysteresis), and dynamic target
/// ratio — from settled forwarding history and live ListChannels. Writes only
/// to Postgres; performs zero LND writes.
/// Guarded by the global ROUTING_ENGINE_ENABLED kill switch.
/// </summary>
[DisallowConcurrentExecution]
public class TargetRatioReevaluationJob : IJob
{
    private readonly ILogger<TargetRatioReevaluationJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly IChannelRoutingStateRepository _routingStateRepository;
    private readonly IForwardingHtlcEventRepository _forwardingHtlcEventRepository;
    private readonly IPeerCategorizationService _peerCategorizationService;
    private readonly ILightningService _lightningService;
    private readonly ILightningClientService _lightningClientService;

    public TargetRatioReevaluationJob(
        ILogger<TargetRatioReevaluationJob> logger,
        INodeRepository nodeRepository,
        IChannelRepository channelRepository,
        IChannelRoutingStateRepository routingStateRepository,
        IForwardingHtlcEventRepository forwardingHtlcEventRepository,
        IPeerCategorizationService peerCategorizationService,
        ILightningService lightningService,
        ILightningClientService lightningClientService)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _channelRepository = channelRepository;
        _routingStateRepository = routingStateRepository;
        _forwardingHtlcEventRepository = forwardingHtlcEventRepository;
        _peerCategorizationService = peerCategorizationService;
        _lightningService = lightningService;
        _lightningClientService = lightningClientService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        // Global kill switch — checked before any work, before touching the DB or LND.
        if (!Constants.ROUTING_ENGINE_ENABLED)
        {
            return;
        }

        _logger.LogInformation("Starting {JobName}...", nameof(TargetRatioReevaluationJob));

        try
        {
            var managedNodes = await _nodeRepository.GetAllManagedByNodeGuard(withDisabled: false);

            // One DB round-trip for channels; index the open/confirmed ones by their LND scid.
            var openChannelsByChanId = new Dictionary<ulong, Channel>();
            foreach (var channel in await _channelRepository.GetAll())
            {
                if (channel.Status == Channel.ChannelStatus.Open && channel.ChanId != 0)
                {
                    openChannelsByChanId[channel.ChanId] = channel;
                }
            }

            foreach (var managedNode in managedNodes)
            {
                try
                {
                    await ReevaluateNode(managedNode, managedNodes, openChannelsByChanId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error re-evaluating routing state for node {NodeName} ({NodePubKey})",
                        managedNode.Name, managedNode.PubKey);
                }
            }
        }
        catch (Exception ex)
        {
            // Never surface an exception from Execute — the next cycle is our retry.
            _logger.LogError(ex, "Error in {JobName}", nameof(TargetRatioReevaluationJob));
        }

        _logger.LogInformation("{JobName} ended", nameof(TargetRatioReevaluationJob));
    }

    private async Task ReevaluateNode(
        Node managedNode,
        IReadOnlyCollection<Node> managedNodes,
        IReadOnlyDictionary<ulong, Channel> openChannelsByChanId)
    {
        var chainTip = await _lightningService.GetBlockHeight(managedNode);
        if (chainTip == null)
        {
            _logger.LogWarning("Skipping routing re-eval for node {NodeName}: GetInfo/block height unavailable",
                managedNode.Name);
            return;
        }

        var listResp = await _lightningClientService.ListChannels(managedNode);
        if (listResp == null)
        {
            _logger.LogWarning("Skipping routing re-eval for node {NodeName}: ListChannels unavailable",
                managedNode.Name);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var windowStart = now - TimeSpan.FromDays(Constants.ROUTING_ENGINE_FLOW_WINDOW_DAYS);

        foreach (var lndChannel in listResp.Channels)
        {
            try
            {
                // Canonical ownership rule — a channel between two managed nodes is owned by one side.
                if (!ChannelOwnershipHelper.IsOwnedByManagedNode(lndChannel, managedNodes))
                {
                    continue;
                }

                // Act only on a channel we have a confirmed, open DB row for.
                if (!openChannelsByChanId.TryGetValue(lndChannel.ChanId, out var dbChannel))
                {
                    continue;
                }

                await ReevaluateChannel(managedNode, lndChannel, dbChannel, chainTip.Value, windowStart, now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error re-evaluating channel {ChanId} on node {NodeName}",
                    lndChannel.ChanId, managedNode.Name);
            }
        }
    }

    private async Task ReevaluateChannel(
        Node managedNode,
        Lnrpc.Channel lndChannel,
        Channel dbChannel,
        uint chainTip,
        DateTimeOffset windowStart,
        DateTimeOffset now)
    {
        var observedRatio = (double)lndChannel.LocalBalance
                            / Math.Max(1, lndChannel.LocalBalance + lndChannel.RemoteBalance);

        // Seed EmaLocalRatio with the first observation on insert — no 0.5 cold-start bias.
        var state = await _routingStateRepository.GetByChannelId(dbChannel.Id)
                    ?? new ChannelRoutingState
                    {
                        ChannelId = dbChannel.Id,
                        ManagedNodePubKey = managedNode.PubKey,
                        EmaLocalRatio = observedRatio,
                    };

        state.ChanIdLnd = lndChannel.ChanId; // refresh: alias scid -> confirmed scid
        state.FundingBlockHeight = BlockHeightHelper.FundingHeightFromChanId(lndChannel.ChanId, chainTip);
        state.AgeBlocks = BlockHeightHelper.AgeBlocksFromChanId(lndChannel.ChanId, chainTip);
        state.PeerInitiated = !lndChannel.Initiator;
        state.EmaLocalRatio = _peerCategorizationService.SmoothEma(
            state.EmaLocalRatio, observedRatio, Constants.ROUTING_ENGINE_FEE_EMA_ALPHA);

        var push = await _forwardingHtlcEventRepository.GetOutgoingAmountMsat(managedNode.PubKey, lndChannel.ChanId, windowStart);
        var pull = await _forwardingHtlcEventRepository.GetIncomingAmountMsat(managedNode.PubKey, lndChannel.ChanId, windowStart);
        state.PushMsatWindow = push;
        state.PullMsatWindow = pull;
        var total = push + pull;
        state.NetFlowRatio = total == 0 ? 0.0 : (double)(push - pull) / total; // >0 = SINK

        // Age gate (job-side); the volume gate lives inside ComputeCategory. Young / alias
        // channels (AgeBlocks null) stay Uncategorized at target 0.5.
        if (state.AgeBlocks >= Constants.ROUTING_ENGINE_CATEGORIZATION_MIN_AGE_BLOCKS)
        {
            var decision = _peerCategorizationService.ComputeCategory(
                state.NetFlowRatio,
                total,
                state.PeerFlowCategory,
                state.PendingCategory,
                state.ConsecutiveCategoryCyclesInNewState,
                Constants.ROUTING_ENGINE_CATEGORY_FLIP_HYSTERESIS_CYCLES,
                Constants.ROUTING_ENGINE_CATEGORY_NET_FLOW_THRESHOLD,
                Constants.ROUTING_ENGINE_FLOW_MIN_MSAT);

            state.PeerFlowCategory = decision.Category;
            state.PendingCategory = decision.PendingCategory;
            state.ConsecutiveCategoryCyclesInNewState = decision.ConsecutiveCyclesInNewState;
            if (decision.Flipped)
            {
                state.LastCategorizedAt = now;
            }

            var targetGoal = state.PeerFlowCategory == PeerFlowCategory.Uncategorized
                ? 0.5
                : _peerCategorizationService.ComputeTargetGoal(
                    state.NetFlowRatio,
                    Constants.ROUTING_ENGINE_TARGET_K,
                    Constants.ROUTING_ENGINE_TARGET_MAX_DRIFT);

            state.TargetLocalRatio = _peerCategorizationService.SmoothTarget(
                state.TargetLocalRatio, targetGoal, Constants.ROUTING_ENGINE_TARGET_ALPHA);
        }

        state.LastKnownNumUpdates = (long)lndChannel.NumUpdates;
        state.LastKnownLifetime = lndChannel.Lifetime;
        state.LastKnownUptime = lndChannel.Uptime;
        state.LastEvaluatedAt = now;

        await _routingStateRepository.UpsertByChannelId(state);
    }
}
