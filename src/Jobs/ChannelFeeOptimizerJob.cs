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
/// Dynamic fee actuator. For every eligible, owned channel
/// on a node with dynamic fee management enabled it reads the signal
/// (<see cref="ChannelRoutingState"/>), runs the pure <see cref="FeeOptimizerService"/> control
/// law, and applies the resulting outbound/inbound ppm via LND — enforcing the fee-vs-rebalance
/// authority split.
/// Everything is gated by the global ROUTING_ENGINE_ENABLED kill switch.
/// </summary>
[DisallowConcurrentExecution]
public class ChannelFeeOptimizerJob : IJob
{
    private readonly ILogger<ChannelFeeOptimizerJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly IChannelRoutingStateRepository _routingStateRepository;
    private readonly IChannelFeeStateRepository _feeStateRepository;
    private readonly IRebalanceRepository _rebalanceRepository;
    private readonly ILightningService _lightningService;
    private readonly ILightningClientService _lightningClientService;

    public ChannelFeeOptimizerJob(
        ILogger<ChannelFeeOptimizerJob> logger,
        INodeRepository nodeRepository,
        IChannelRepository channelRepository,
        IChannelRoutingStateRepository routingStateRepository,
        IChannelFeeStateRepository feeStateRepository,
        IRebalanceRepository rebalanceRepository,
        ILightningService lightningService,
        ILightningClientService lightningClientService)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _channelRepository = channelRepository;
        _routingStateRepository = routingStateRepository;
        _feeStateRepository = feeStateRepository;
        _rebalanceRepository = rebalanceRepository;
        _lightningService = lightningService;
        _lightningClientService = lightningClientService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        // Global kill switch — checked before any work.
        if (!Constants.ROUTING_ENGINE_ENABLED)
        {
            return;
        }

        _logger.LogInformation("Starting {JobName}...", nameof(ChannelFeeOptimizerJob));

        try
        {
            var tunables = FeeOptimizerTunables.FromConstants();
            var managedNodes = await _nodeRepository.GetAllManagedByNodeGuard(withDisabled: false);

            var anyEnabled = managedNodes.Any(n => n.DynamicFeeManagementEnabled);
            if (!anyEnabled)
            {
                _logger.LogInformation("No managed nodes with dynamic fee management enabled; skipping {JobName}",
                    nameof(ChannelFeeOptimizerJob));
                return;
            }

            // Shared per-run context, only needed when at least one node is under management.
            var channelsByChanId = (await _channelRepository.GetChannelsByOpenAndDynamicFeeEnabled())
                .ToDictionary(c => c.ChanId);
            var inFlightSourceChannelIds = await _rebalanceRepository.GetPendingInFlightSourceChannelIds();

            foreach (var managedNode in managedNodes)
            {
                try
                {
                    if (!managedNode.DynamicFeeManagementEnabled) continue;

                    await OptimizeNode(managedNode, managedNodes, channelsByChanId, inFlightSourceChannelIds, tunables);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error optimizing fees for node {NodeName} ({NodePubKey})",
                        managedNode.Name, managedNode.PubKey);
                }
            }
        }
        catch (Exception ex)
        {
            // Never surface an exception from Execute — the next cycle is our retry.
            _logger.LogError(ex, "Error in {JobName}", nameof(ChannelFeeOptimizerJob));
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelFeeOptimizerJob));
    }

    private sealed class Candidate
    {
        public required Lnrpc.Channel LndChannel { get; init; }
        public required Channel DbChannel { get; init; }
        public required ChannelRoutingState RoutingState { get; init; }
        public required ChannelFeeState? FeeState { get; init; }
    }

    private async Task OptimizeNode(
        Node node,
        IReadOnlyCollection<Node> managedNodes,
        IReadOnlyDictionary<ulong, Channel> channelsByChanId,
        IReadOnlySet<int> inFlightSourceChannelIds,
        FeeOptimizerTunables tunables)
    {
        var listResp = await _lightningClientService.ListChannels(node);
        if (listResp == null)
        {
            _logger.LogWarning("Skipping fee optimization for node {NodeName}: ListChannels unavailable", node.Name);
            return;
        }

        var routingStates = (await _routingStateRepository.GetByManagedNodePubKey(node.PubKey))
            .ToDictionary(s => s.ChannelId);
        var feeStates = (await _feeStateRepository.GetByManagedNodePubKey(node.PubKey))
            .ToDictionary(s => s.ChannelId);

        var now = DateTimeOffset.UtcNow;

        // Eligibility filter.
        var candidates = new List<Candidate>();
        foreach (var lndChannel in listResp.Channels)
        {
            if (!ChannelOwnershipHelper.IsOwnedByManagedNode(lndChannel, managedNodes)) continue;
            if (!channelsByChanId.TryGetValue(lndChannel.ChanId, out var dbChannel)) continue;
            if (lndChannel.Capacity < Constants.ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS) continue;
            if (!routingStates.TryGetValue(dbChannel.Id, out var routingState)) continue; // no signal yet

            feeStates.TryGetValue(dbChannel.Id, out var feeState);

            candidates.Add(new Candidate
            {
                LndChannel = lndChannel,
                DbChannel = dbChannel,
                RoutingState = routingState,
                FeeState = feeState,
            });
        }

        foreach (var candidate in candidates)
        {
            // Authority split: never touch a channel the rebalancer is actively moving.
            if (inFlightSourceChannelIds.Contains(candidate.DbChannel.Id))
            {
                _logger.LogDebug("Skipping channel {ChanId} on {NodeName}: in-flight rebalance owns it",
                    candidate.LndChannel.ChanId, node.Name);
                continue;
            }

            try
            {
                await OptimizeChannel(node, candidate, tunables, now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing fees for channel {ChanId} on node {NodeName}",
                    candidate.LndChannel.ChanId, node.Name);
            }
        }
    }

    /// <summary>Runs the control law for one channel and applies the resulting fee update when needed.</summary>
    private async Task OptimizeChannel(Node node, Candidate candidate, FeeOptimizerTunables tunables, DateTimeOffset now)
    {
        var routingState = candidate.RoutingState;
        var feeState = candidate.FeeState ?? new ChannelFeeState { ChannelId = candidate.DbChannel.Id };

        var decision = FeeOptimizerService.ComputeNextPolicy(
            routingState.EmaLocalRatio,
            routingState.TargetLocalRatio,
            routingState.PeerFlowCategory,
            feeState.LastAppliedOutboundPpm,
            feeState.LastAppliedInboundPpm,
            node.AllowPositiveInboundFees,
            tunables);

        feeState.LastObservedRatio = routingState.EmaLocalRatio;
        feeState.LastComputedTarget = routingState.TargetLocalRatio;

        if (decision.Action != FeeAction.Update)
        {
            _logger.LogInformation("Channel {ChanId} on {NodeName}: {Action} ({Reason})",
                candidate.LndChannel.ChanId, node.Name, decision.Action, decision.Reason);
            await _feeStateRepository.UpsertByChannelId(feeState);
            return;
        }

        // The live policy is needed only on the write path for the untouched base fee/timelock.
        // NoOp channels never cost an LND round-trip.
        var (managedPolicy, _) = await _lightningService.GetChannelFeePolicy(candidate.LndChannel.ChanId, node);
        if (managedPolicy == null)
        {
            _logger.LogWarning("Skipping channel {ChanId} on {NodeName}: current fee policy unavailable",
                candidate.LndChannel.ChanId, node.Name);
            await _feeStateRepository.UpsertByChannelId(feeState);
            return;
        }

        var baseFeeMsat = managedPolicy.FeeBaseMsat;
        var timeLockDelta = managedPolicy.TimeLockDelta;
        var inboundBaseMsat = managedPolicy.InboundFeeBaseMsat; // engine only modulates the ppm rates
        var chanPoint = $"{candidate.DbChannel.FundingTx}:{candidate.DbChannel.FundingTxOutputIndex}";

        if (node.RoutingEngineDryRun)
        {
            _logger.LogInformation("Dry-run: would set channel {ChanId} ({NodeName}-{PeerAlias}) to outbound {Outbound}ppm inbound {Inbound}ppm ({Reason})",
                candidate.LndChannel.ChanId, node.Name, candidate.LndChannel.PeerAlias, decision.OutboundPpm, decision.InboundPpm, decision.Reason);
            feeState.LastAppliedOutboundBaseFeeMsat = baseFeeMsat;
            feeState.LastAppliedOutboundPpm = decision.OutboundPpm;
            feeState.LastAppliedInboundBaseMsat = inboundBaseMsat;
            feeState.LastAppliedInboundPpm = decision.InboundPpm;
            feeState.LastFeeUpdateAt = now;

            await _feeStateRepository.UpsertByChannelId(feeState);
            return;
        }

        try
        {
            await _lightningService.SetChannelFeePolicy(
                chanPoint,
                node.PubKey,
                baseFeeMsat,
                decision.OutboundPpm,
                timeLockDelta,
                inboundBaseMsat,
                decision.InboundPpm,
                // Always an engine write: positive inbound is permitted here, and the node's
                // AllowPositiveInboundFees preference was already applied by ComputeNextPolicy
                // (which collapses inbound to <= 0 when the node disallows it).
                isEngineDriven: true);

            feeState.LastAppliedOutboundBaseFeeMsat = baseFeeMsat;
            feeState.LastAppliedOutboundPpm = decision.OutboundPpm;
            feeState.LastAppliedInboundBaseMsat = inboundBaseMsat;
            feeState.LastAppliedInboundPpm = decision.InboundPpm;
            feeState.LastFeeUpdateAt = now;

            _logger.LogInformation("{NodeName} chan {ChanId}: set outbound {Outbound}ppm inbound {Inbound}ppm ({Reason})",
                node.Name, candidate.LndChannel.ChanId, decision.OutboundPpm, decision.InboundPpm, decision.Reason);

            await _feeStateRepository.UpsertByChannelId(feeState);
        }
        catch (Exception ex)
        {
            // Log and skip this channel for this cycle; the next cycle retries.
            _logger.LogError(ex, "Failed to set fee policy for channel {ChanId} on {NodeName}",
                candidate.LndChannel.ChanId, node.Name);
        }
    }
}
