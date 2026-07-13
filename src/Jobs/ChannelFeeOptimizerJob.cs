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

using Microsoft.EntityFrameworkCore;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Quartz;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Jobs;

/// <summary>
/// Phase 2 of the routing engine — the dynamic fee actuator. For every eligible, owned channel
/// on a node with dynamic fee management enabled it reads the Phase 1 signal
/// (<see cref="ChannelRoutingState"/>), runs the pure <see cref="IFeeOptimizerService"/> control
/// law, and applies the resulting outbound/inbound ppm via LND — enforcing the fee-vs-rebalance
/// authority split, a per-channel throttle + circuit breaker, and a baseline snapshot for
/// restore-on-disable. Real LND writes happen only when neither the global nor the per-node
/// dry-run flag is set; everything is gated by the global ROUTING_ENGINE_ENABLED kill switch.
/// </summary>
[DisallowConcurrentExecution]
public class ChannelFeeOptimizerJob : IJob
{
    // Prioritization looks at recent organic (forwarding) revenue over this window.
    private static readonly TimeSpan OrganicFeesWindow = TimeSpan.FromDays(7);

    private readonly ILogger<ChannelFeeOptimizerJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly IChannelRoutingStateRepository _routingStateRepository;
    private readonly IChannelFeeStateRepository _feeStateRepository;
    private readonly IChannelFlowAnalyticsRepository _flowAnalyticsRepository;
    private readonly IRebalanceRepository _rebalanceRepository;
    private readonly IFeeOptimizerService _feeOptimizerService;
    private readonly ILightningService _lightningService;
    private readonly ILightningClientService _lightningClientService;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

    public ChannelFeeOptimizerJob(
        ILogger<ChannelFeeOptimizerJob> logger,
        INodeRepository nodeRepository,
        IChannelRepository channelRepository,
        IChannelRoutingStateRepository routingStateRepository,
        IChannelFeeStateRepository feeStateRepository,
        IChannelFlowAnalyticsRepository flowAnalyticsRepository,
        IRebalanceRepository rebalanceRepository,
        IFeeOptimizerService feeOptimizerService,
        ILightningService lightningService,
        ILightningClientService lightningClientService,
        IDbContextFactory<ApplicationDbContext> dbContextFactory)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _channelRepository = channelRepository;
        _routingStateRepository = routingStateRepository;
        _feeStateRepository = feeStateRepository;
        _flowAnalyticsRepository = flowAnalyticsRepository;
        _rebalanceRepository = rebalanceRepository;
        _feeOptimizerService = feeOptimizerService;
        _lightningService = lightningService;
        _lightningClientService = lightningClientService;
        _dbContextFactory = dbContextFactory;
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
                    if (managedNode.DynamicFeeManagementEnabled)
                    {
                        await OptimizeNode(managedNode, managedNodes, openChannelsByChanId, tunables);
                    }
                    else
                    {
                        // Node opted out (or just flipped off) — restore baselines / freeze, once.
                        await HandleDisabledNode(managedNode);
                    }
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
        public ChannelFeeState? FeeState { get; set; }
        public long OrganicFeesMsat { get; set; }
        public double Deviation { get; set; }
    }

    private async Task OptimizeNode(
        Node node,
        IReadOnlyCollection<Node> managedNodes,
        IReadOnlyDictionary<ulong, Channel> openChannelsByChanId,
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
        var organicSince = now - OrganicFeesWindow;

        // Eligibility filter (before prioritization).
        var candidates = new List<Candidate>();
        foreach (var lndChannel in listResp.Channels)
        {
            if (!ChannelOwnershipHelper.IsOwnedByManagedNode(lndChannel, managedNodes)) continue;
            if (!openChannelsByChanId.TryGetValue(lndChannel.ChanId, out var dbChannel)) continue;
            if (!dbChannel.IsDynamicFeeEnabled) continue;
            if (lndChannel.Capacity < Constants.ROUTING_ENGINE_FEE_MIN_CHANNEL_SIZE_SATS) continue;
            if (!routingStates.TryGetValue(dbChannel.Id, out var routingState)) continue; // no signal yet

            feeStates.TryGetValue(dbChannel.Id, out var feeState);
            if (feeState is { ConsecutiveFailures: > 0 }
                && feeState.ConsecutiveFailures >= Constants.ROUTING_ENGINE_FEE_MAX_CONSECUTIVE_FAILURES)
            {
                _logger.LogDebug("Channel {ChanId} on {NodeName} is circuit-broken; toggle IsDynamicFeeEnabled to reset",
                    lndChannel.ChanId, node.Name);
                continue;
            }

            candidates.Add(new Candidate
            {
                LndChannel = lndChannel,
                DbChannel = dbChannel,
                RoutingState = routingState,
                FeeState = feeState,
                OrganicFeesMsat = await _flowAnalyticsRepository.GetOrganicFeesEarnedMsat(node.PubKey, lndChannel.ChanId, organicSince),
                Deviation = routingState.EmaLocalRatio - routingState.TargetLocalRatio,
            });
        }

        // Prioritize by recent organic revenue, then by how far off-target the channel is.
        var prioritized = candidates
            .OrderByDescending(c => c.OrganicFeesMsat)
            .ThenByDescending(c => Math.Abs(c.Deviation));

        var examined = 0;
        foreach (var candidate in prioritized)
        {
            if (examined >= Constants.ROUTING_ENGINE_FEE_MAX_UPDATES_PER_RUN) break;

            // Authority split: never touch a channel the rebalancer is actively moving.
            if (await _rebalanceRepository.HasInFlightRebalanceBySourceChannel(candidate.DbChannel.Id))
            {
                _logger.LogDebug("Skipping channel {ChanId} on {NodeName}: in-flight rebalance owns it",
                    candidate.LndChannel.ChanId, node.Name);
                continue;
            }

            try
            {
                examined++;
                await OptimizeChannel(node, candidate, tunables, now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error optimizing fees for channel {ChanId} on node {NodeName}",
                    candidate.LndChannel.ChanId, node.Name);
            }
        }
    }

    private async Task OptimizeChannel(Node node, Candidate candidate, FeeOptimizerTunables tunables, DateTimeOffset now)
    {
        var (managedPolicy, _) = await _lightningService.GetChannelFeePolicy(candidate.LndChannel.ChanId, node);
        if (managedPolicy == null)
        {
            _logger.LogWarning("Skipping channel {ChanId} on {NodeName}: current fee policy unavailable",
                candidate.LndChannel.ChanId, node.Name);
            return;
        }

        var routingState = candidate.RoutingState;
        var feeState = candidate.FeeState ?? new ChannelFeeState { ChannelId = candidate.DbChannel.Id };

        // Snapshot the operator's pre-engine policy once, before any write, for restore-on-disable.
        if (feeState.BaselineCapturedAt == null)
        {
            feeState.BaselineCapturedAt = now;
            feeState.BaselineOutboundBaseFeeMsat = managedPolicy.FeeBaseMsat;
            feeState.BaselineOutboundPpm = (uint)managedPolicy.FeeRateMilliMsat;
            feeState.BaselineInboundBaseMsat = managedPolicy.InboundFeeBaseMsat;
            feeState.BaselineInboundPpm = managedPolicy.InboundFeeRateMilliMsat;
        }

        var decision = _feeOptimizerService.ComputeNextPolicy(
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
            _logger.LogDebug("Channel {ChanId} on {NodeName}: {Action} ({Reason})",
                candidate.LndChannel.ChanId, node.Name, decision.Action, decision.Reason);
            await _feeStateRepository.UpsertByChannelId(feeState);
            return;
        }

        // Throttle: at most one applied change per MIN_UPDATE_INTERVAL, surviving restarts.
        if (feeState.LastFeeUpdateAt is { } last
            && (now - last).TotalMinutes < Constants.ROUTING_ENGINE_FEE_MIN_UPDATE_INTERVAL_MINUTES)
        {
            _logger.LogDebug("Channel {ChanId} on {NodeName}: throttled (last update {Last})",
                candidate.LndChannel.ChanId, node.Name, last);
            await _feeStateRepository.UpsertByChannelId(feeState);
            return;
        }

        var baseFeeMsat = managedPolicy.FeeBaseMsat;
        var timeLockDelta = managedPolicy.TimeLockDelta;
        var inboundBaseMsat = managedPolicy.InboundFeeBaseMsat; // engine only modulates the ppm rates
        var chanPoint = $"{candidate.DbChannel.FundingTx}:{candidate.DbChannel.FundingTxOutputIndex}";

        // Effective dry-run = global master OR per-node — lets operators go live one node at a time.
        var dryRun = Constants.ROUTING_ENGINE_DRY_RUN || node.RoutingEngineDryRun;
        if (dryRun)
        {
            _logger.LogInformation(
                "[DRY-RUN] {NodeName} chan {ChanId}: would set outbound {Outbound}ppm inbound {Inbound}ppm ({Reason})",
                node.Name, candidate.LndChannel.ChanId, decision.OutboundPpm, decision.InboundPpm, decision.Reason);
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
            feeState.ConsecutiveFailures = 0;

            _logger.LogInformation("{NodeName} chan {ChanId}: set outbound {Outbound}ppm inbound {Inbound}ppm ({Reason})",
                node.Name, candidate.LndChannel.ChanId, decision.OutboundPpm, decision.InboundPpm, decision.Reason);
        }
        catch (Exception ex)
        {
            feeState.ConsecutiveFailures++;
            _logger.LogError(ex, "Failed to set fee policy for channel {ChanId} on {NodeName} (failure {Count}/{Max})",
                candidate.LndChannel.ChanId, node.Name, feeState.ConsecutiveFailures,
                Constants.ROUTING_ENGINE_FEE_MAX_CONSECUTIVE_FAILURES);

            if (feeState.ConsecutiveFailures >= Constants.ROUTING_ENGINE_FEE_MAX_CONSECUTIVE_FAILURES)
            {
                await WriteAuditAsync(candidate.DbChannel, node, "circuit-broken", new
                {
                    candidate.LndChannel.ChanId,
                    feeState.ConsecutiveFailures,
                });
                _logger.LogWarning("Channel {ChanId} on {NodeName} circuit-broken after {Count} consecutive failures",
                    candidate.LndChannel.ChanId, node.Name, feeState.ConsecutiveFailures);
            }
        }

        await _feeStateRepository.UpsertByChannelId(feeState);
    }

    /// <summary>
    /// A managed node with dynamic fee management OFF. For each channel we previously touched
    /// (baseline captured), either restore the operator's baseline policy once (default) or leave
    /// the last-set fees frozen with an audit marker — then clear our snapshot so this is one-shot.
    /// </summary>
    private async Task HandleDisabledNode(Node node)
    {
        var toHandle = (await _feeStateRepository.GetByManagedNodePubKey(node.PubKey))
            .Where(fs => fs.BaselineCapturedAt != null)
            .ToList();
        if (toHandle.Count == 0) return;

        var dryRun = Constants.ROUTING_ENGINE_DRY_RUN || node.RoutingEngineDryRun;

        foreach (var feeState in toHandle)
        {
            var dbChannel = feeState.Channel;
            var chanPoint = $"{dbChannel.FundingTx}:{dbChannel.FundingTxOutputIndex}";

            try
            {
                if (node.RestoreFeeBaselineOnDisable)
                {
                    if (dryRun)
                    {
                        _logger.LogInformation(
                            "[DRY-RUN] {NodeName} chan {ChanId}: would restore baseline fees (engine disabled)",
                            node.Name, dbChannel.ChanId);
                    }
                    else
                    {
                        // Engine never changes the timelock, so restore fee rates against the live one.
                        var (managedPolicy, _) = await _lightningService.GetChannelFeePolicy(dbChannel.ChanId, node);
                        if (managedPolicy == null)
                        {
                            _logger.LogWarning(
                                "Deferring baseline restore for chan {ChanId} on {NodeName}: current policy unavailable",
                                dbChannel.ChanId, node.Name);
                            continue; // keep baseline, retry next cycle
                        }

                        await _lightningService.SetChannelFeePolicy(
                            chanPoint,
                            node.PubKey,
                            feeState.BaselineOutboundBaseFeeMsat ?? managedPolicy.FeeBaseMsat,
                            feeState.BaselineOutboundPpm ?? (uint)managedPolicy.FeeRateMilliMsat,
                            managedPolicy.TimeLockDelta,
                            feeState.BaselineInboundBaseMsat ?? managedPolicy.InboundFeeBaseMsat,
                            feeState.BaselineInboundPpm ?? managedPolicy.InboundFeeRateMilliMsat,
                            isEngineDriven: true);

                        await WriteAuditAsync(dbChannel, node, "baseline-restored", new
                        {
                            dbChannel.ChanId,
                            feeState.BaselineOutboundPpm,
                            feeState.BaselineInboundPpm,
                        });
                        _logger.LogInformation("{NodeName} chan {ChanId}: restored baseline fees (engine disabled)",
                            node.Name, dbChannel.ChanId);
                    }
                }
                else
                {
                    _logger.LogInformation("{NodeName} chan {ChanId}: engine disabled, fees frozen", node.Name, dbChannel.ChanId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling disabled-node fee state for chan {ChanId} on {NodeName}",
                    dbChannel.ChanId, node.Name);
                continue; // keep baseline, retry next cycle
            }

            // One-shot: clear our tracking. A later re-enable recaptures a fresh baseline.
            feeState.BaselineCapturedAt = null;
            feeState.BaselineOutboundBaseFeeMsat = null;
            feeState.BaselineOutboundPpm = null;
            feeState.BaselineInboundBaseMsat = null;
            feeState.BaselineInboundPpm = null;
            feeState.LastAppliedOutboundBaseFeeMsat = null;
            feeState.LastAppliedOutboundPpm = null;
            feeState.LastAppliedInboundBaseMsat = null;
            feeState.LastAppliedInboundPpm = null;
            feeState.LastFeeUpdateAt = null;
            feeState.ConsecutiveFailures = 0;
            await _feeStateRepository.UpsertByChannelId(feeState);
        }
    }

    private async Task WriteAuditAsync(Channel channel, Node node, string action, object extraDetails)
    {
        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            await context.AuditLogs.AddAsync(new AuditLog
            {
                ActionType = AuditActionType.Update,
                EventType = AuditEventType.Success,
                ObjectAffected = AuditObjectType.Channel,
                ObjectId = channel.Id.ToString(),
                Username = "SYSTEM",
                Details = System.Text.Json.JsonSerializer.Serialize(new
                {
                    EngineWrite = true,
                    RoutingEngineAction = action,
                    ChannelId = channel.Id,
                    NodeId = node.Id,
                    NodePubKey = node.PubKey,
                    Extra = extraDetails,
                }),
            });
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write routing-engine audit log ({Action}) for channel {ChannelId}",
                action, channel.Id);
        }
    }
}
