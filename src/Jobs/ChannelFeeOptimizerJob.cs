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
/// The routing engine's fee actuator. For every eligible channel on a node with
/// <see cref="Node.DynamicFeeManagementEnabled"/> it reads the signal
/// (<see cref="ChannelRoutingState"/>), runs the pure <see cref="FeeOptimizerService"/> control
/// law, and applies the resulting outbound/inbound ppm via LND.
/// Everything is gated by the global ROUTING_ENGINE_ENABLED kill switch.
/// </summary>
[DisallowConcurrentExecution]
public class ChannelFeeOptimizerJob : IJob
{
    private readonly ILogger<ChannelFeeOptimizerJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly IChannelFeeStateRepository _feeStateRepository;
    private readonly IRebalanceRepository _rebalanceRepository;
    private readonly IRoutingEngineSnapshotService _snapshotService;
    private readonly ILightningService _lightningService;

    public ChannelFeeOptimizerJob(
        ILogger<ChannelFeeOptimizerJob> logger,
        INodeRepository nodeRepository,
        IChannelRepository channelRepository,
        IChannelFeeStateRepository feeStateRepository,
        IRebalanceRepository rebalanceRepository,
        IRoutingEngineSnapshotService snapshotService,
        ILightningService lightningService)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _channelRepository = channelRepository;
        _feeStateRepository = feeStateRepository;
        _rebalanceRepository = rebalanceRepository;
        _snapshotService = snapshotService;
        _lightningService = lightningService;
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

            var relevantNodes = managedNodes.Where(n => n.DynamicFeeManagementEnabled).ToList();
            if (relevantNodes.Count == 0)
            {
                _logger.LogInformation("No managed nodes with dynamic fee management enabled; skipping {JobName}",
                    nameof(ChannelFeeOptimizerJob));
                return;
            }

            var inFlightSourceChannelIds = await _rebalanceRepository.GetPendingInFlightSourceChannelIds();

            // Get all the open channels that are eligible for fee optimization in one DB call, then filter per node.
            var openChannelsByChanId = (await _channelRepository.GetOpenChannels())
                .Where(c => c.IsDynamicFeeEnabled)
                .Where(c => c.SatsAmount >= Constants.ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS)
                .Where(c => !inFlightSourceChannelIds.Contains(c.Id))
                .ToDictionary(c => c.ChanId);

            foreach (var node in relevantNodes)
            {
                try
                {
                    await OptimizeNode(node, openChannelsByChanId, inFlightSourceChannelIds, tunables);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error optimizing fees for node {NodeName} ({NodePubKey})",
                        node.Name, node.PubKey);
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

    private async Task OptimizeNode(
        Node node,
        IReadOnlyDictionary<ulong, Channel> openChannelsByChanId,
        IReadOnlySet<int> inFlightSourceChannelIds,
        FeeOptimizerTunables tunables)
    {
        var owned = await _snapshotService.GetOwnedChannelsAsync(node, openChannelsByChanId, withFeeState: true);
        if (owned == null)
        {
            _logger.LogWarning("Skipping node {NodeName}: ListChannels unavailable", node.Name);
            return;
        }

        foreach (var oc in owned)
        {
            try
            {
                await OptimizeChannel(node, oc, tunables);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing fees for channel {ChanId} on node {NodeName}",
                    oc.Lnd.ChanId, node.Name);
            }
        }
    }

    /// <summary>Runs the control law for one channel and applies the resulting fee update when needed.</summary>
    private async Task OptimizeChannel(Node node, OwnedChannel candidate, FeeOptimizerTunables tunables)
    {
        var routingState = candidate.RoutingState;
        var feeState = candidate.FeeState ?? new ChannelFeeState
        {
            ChannelId = candidate.DbChannel.Id,
            ManagedNodePubKey = node.PubKey,
        };

        var now = DateTimeOffset.UtcNow;

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
                candidate.Lnd.ChanId, node.Name, decision.Action, decision.Reason);
            await _feeStateRepository.UpsertByChannelAndNode(feeState);
            return;
        }

        // The live policy is needed only on the write path for the untouched base fee/timelock.
        // NoOp channels never cost an LND round-trip.
        var (managedPolicy, _) = await _lightningService.GetChannelFeePolicy(candidate.Lnd.ChanId, node);
        if (managedPolicy == null)
        {
            _logger.LogWarning("Skipping channel {ChanId} on {NodeName}: current fee policy unavailable",
                candidate.Lnd.ChanId, node.Name);
            await _feeStateRepository.UpsertByChannelAndNode(feeState);
            return;
        }

        var baseFeeMsat = managedPolicy.FeeBaseMsat;
        var timeLockDelta = managedPolicy.TimeLockDelta;
        var inboundBaseMsat = managedPolicy.InboundFeeBaseMsat; // engine only modulates the ppm rates
        var chanPoint = $"{candidate.DbChannel.FundingTx}:{candidate.DbChannel.FundingTxOutputIndex}";

        if (node.RoutingEngineDryRun)
        {
            _logger.LogInformation("Dry-run: would set channel {ChanId} ({NodeName}-{PeerAlias}) to outbound {Outbound}ppm inbound {Inbound}ppm ({Reason})",
                candidate.Lnd.ChanId, node.Name, candidate.Lnd.PeerAlias, decision.OutboundPpm, decision.InboundPpm, decision.Reason);
            feeState.LastAppliedOutboundBaseFeeMsat = baseFeeMsat;
            feeState.LastAppliedOutboundPpm = decision.OutboundPpm;
            feeState.LastAppliedInboundBaseMsat = inboundBaseMsat;
            feeState.LastAppliedInboundPpm = decision.InboundPpm;
            feeState.LastFeeUpdateAt = now;

            await _feeStateRepository.UpsertByChannelAndNode(feeState);
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
                node.Name, candidate.Lnd.ChanId, decision.OutboundPpm, decision.InboundPpm, decision.Reason);

            await _feeStateRepository.UpsertByChannelAndNode(feeState);
        }
        catch (Exception ex)
        {
            // Log and skip this channel for this cycle; the next cycle retries.
            _logger.LogError(ex, "Failed to set fee policy for channel {ChanId} on {NodeName}",
                candidate.Lnd.ChanId, node.Name);
        }
    }
}
