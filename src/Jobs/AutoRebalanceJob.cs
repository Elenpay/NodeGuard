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
/// The routing engine's rebalance actuator. For every node with <see cref="Node.AutoRebalanceEnabled"/>
/// it takes a snapshot, hands it to the pure <see cref="RebalanceInitiatorService"/> to plan
/// too-local-source → too-remote-destination circular rebalances, and dispatches them via
/// <see cref="IRebalanceService"/> — bounded by the node's fee budget, its in-flight cap, and the
/// per-run initiation cap.
/// <para>
/// Runs on its own cadence (ROUTING_ENGINE_REBALANCE_JOB_INTERVAL_MINUTES), independent of
/// <see cref="ChannelFeeOptimizerJob"/>.
/// </summary>
[DisallowConcurrentExecution]
public class AutoRebalanceJob : IJob
{
    private readonly ILogger<AutoRebalanceJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly IRebalanceRepository _rebalanceRepository;
    private readonly IRebalanceService _rebalanceService;
    private readonly IRoutingEngineSnapshotService _snapshotService;
    private readonly ILightningService _lightningService;
    private readonly IAuditService _auditService;

    public AutoRebalanceJob(
        ILogger<AutoRebalanceJob> logger,
        INodeRepository nodeRepository,
        IChannelRepository channelRepository,
        IRebalanceRepository rebalanceRepository,
        IRebalanceService rebalanceService,
        IRoutingEngineSnapshotService snapshotService,
        ILightningService lightningService,
        IAuditService auditService)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _channelRepository = channelRepository;
        _rebalanceRepository = rebalanceRepository;
        _rebalanceService = rebalanceService;
        _snapshotService = snapshotService;
        _lightningService = lightningService;
        _auditService = auditService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        // Global kill switch — checked before any work
        if (!Constants.ROUTING_ENGINE_ENABLED)
        {
            return;
        }

        _logger.LogInformation("Starting {JobName}...", nameof(AutoRebalanceJob));

        try
        {
            var managedNodes = await _nodeRepository.GetAllManagedByNodeGuard(withDisabled: false);

            var relevantNodes = managedNodes.Where(n => n.AutoRebalanceEnabled).ToList();
            if (relevantNodes.Count == 0)
            {
                _logger.LogInformation("No managed nodes with the rebalancer enabled; skipping {JobName}",
                    nameof(AutoRebalanceJob));
                return;
            }

            // Shared per-run context, only needed when at least one node is under management
            var openChannelsByChanId = (await _channelRepository.GetOpenChannels())
                .ToDictionary(c => c.ChanId);
            var inFlightSourceChannelIds = await _rebalanceRepository.GetPendingInFlightSourceChannelIds();

            foreach (var node in relevantNodes)
            {
                try
                {
                    await RebalanceNode(node, openChannelsByChanId, inFlightSourceChannelIds);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error rebalancing node {NodeName} ({NodePubKey})",
                        node.Name, node.PubKey);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in {JobName}", nameof(AutoRebalanceJob));
        }

        _logger.LogInformation("{JobName} ended", nameof(AutoRebalanceJob));
    }

    private async Task RebalanceNode(
        Node node,
        IReadOnlyDictionary<ulong, Channel> openChannelsByChanId,
        IReadOnlySet<int> inFlightSourceChannelIds)
    {
        var now = DateTimeOffset.UtcNow;

        // A budget must be configured before we spend anything. Checked before the LND round-trip
        var budgetSats = node.RebalanceBudgetSats ?? 0;
        if (budgetSats <= 0)
        {
            _logger.LogInformation("Node {NodeName}: no rebalance budget configured; skipping", node.Name);
            return;
        }

        // Budget-period refresh
        var refreshInterval = node.RebalanceBudgetRefreshInterval
                              ?? TimeSpan.FromHours(Constants.ROUTING_ENGINE_REBALANCE_DEFAULT_BUDGET_REFRESH_HOURS);
        if (!node.RebalanceBudgetStartDatetime.HasValue ||
            now - node.RebalanceBudgetStartDatetime.Value >= refreshInterval)
        {
            _logger.LogInformation("Refreshing rebalance budget for node {NodeName}", node.Name);
            node.RebalanceBudgetStartDatetime = now;
            _nodeRepository.Update(node);
        }

        var periodStart = node.RebalanceBudgetStartDatetime ?? now;

        // Remaining fee budget for this period. GetConsumedFeesSince already counts in-flight
        // reservations, so a rebalance started earlier this period is charged immediately
        var consumed = await _rebalanceRepository.GetConsumedFeesSince(node.Id, periodStart);
        var remainingBudget = budgetSats - consumed;
        if (remainingBudget <= 0)
        {
            _logger.LogInformation("Node {NodeName}: rebalance fee budget exhausted ({Consumed}/{Budget} sats)",
                node.Name, consumed, budgetSats);
            return;
        }

        // In-flight cap
        var inFlight = await _rebalanceRepository.GetInFlightByNode(node.Id);
        var maxInFlight = node.MaxRebalancesInFlight ?? Constants.ROUTING_ENGINE_REBALANCE_DEFAULT_MAX_IN_FLIGHT;
        if (inFlight >= maxInFlight)
        {
            _logger.LogInformation("Node {NodeName}: max rebalances in flight reached ({InFlight}/{Max})",
                node.Name, inFlight, maxInFlight);
            return;
        }

        var owned = await _snapshotService.GetOwnedChannelsAsync(node, openChannelsByChanId, withFeeState: false);
        if (owned == null)
        {
            _logger.LogWarning("Skipping node {NodeName}: ListChannels unavailable", node.Name);
            return;
        }

        var signals = owned.Select(oc => new ChannelSignal(
            oc.DbChannel.Id,
            oc.Lnd.ChanId,
            oc.Lnd.RemotePubkey,
            oc.Lnd.LocalBalance,
            oc.Lnd.RemoteBalance,
            oc.RoutingState.EmaLocalRatio,
            oc.RoutingState.TargetLocalRatio,
            oc.Lnd.Active,
            // A channel is a fresh source only if opted in and not already being drained
            oc.DbChannel.IsAutoRebalanceEnabled && !inFlightSourceChannelIds.Contains(oc.DbChannel.Id)))
            .ToList();

        var tunables = RebalanceInitiatorTunables.FromConstants(node);
        var classification = RebalanceInitiatorService.Classify(signals, tunables);

        if (classification.Sources.Count == 0 && classification.Destinations.Count == 0)
        {
            _logger.LogInformation("Node {NodeName}: nothing to rebalance (no channel tripped the trigger)", node.Name);
            return;
        }

        _logger.LogInformation(
            "Node {NodeName}: detected {Sources} source(s) and {Destinations} destination(s); " +
            "fallback pool {FallbackSources} source(s), {FallbackDestinations} destination(s)",
            node.Name, classification.Sources.Count, classification.Destinations.Count,
            classification.FallbackSources.Count, classification.FallbackDestinations.Count);

        var earnRates = await _lightningService.GetLocalOutboundFeeRatesPpmAsync(node);
        if (earnRates == null)
        {
            _logger.LogWarning("Skipping node {NodeName}: FeeReport unavailable, so nothing can be profit-gated",
                node.Name);
            return;
        }

        var plans = RebalanceInitiatorService.BuildPlans(classification, earnRates, tunables);
        if (plans.Count == 0)
        {
            _logger.LogInformation("Node {NodeName}: no profitable rebalance plans this cycle", node.Name);
            return;
        }

        var initiations = 0;

        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];

            // The cap stops the loop outright, so every remaining plan is abandoned
            if (inFlight + initiations >= maxInFlight)
            {
                _logger.LogInformation(
                    "Node {NodeName}: dropped {DroppedCount} of {PlannedCount} planned rebalance(s) — " +
                    "in-flight cap reached ({Reached}/{Max}, {Before} already in flight before this run; " +
                    "raise the node's MaxRebalancesInFlight to dispatch more per cycle)",
                    node.Name, plans.Count - i, plans.Count, inFlight + initiations, maxInFlight, inFlight);

                for (var dropped = i; dropped < plans.Count; dropped++)
                {
                    _logger.LogInformation("Node {NodeName}: dropped plan — {Reason}", node.Name, plans[dropped].Reason);
                }

                break;
            }

            var reservedFee = Rebalance.WorstCaseFeeSats(plan.AmountSats, plan.MaxFeePct);
            if (reservedFee > remainingBudget)
            {
                _logger.LogInformation(
                    "Node {NodeName}: skipping plan (reserved {Reserved} sats > remaining budget {Remaining} sats): {Reason}",
                    node.Name, reservedFee, remainingBudget, plan.Reason);
                continue;
            }

            if (node.RoutingEngineDryRun)
            {
                _logger.LogInformation("Dry-run: node {NodeName} would rebalance — {Reason}", node.Name, plan.Reason);
                remainingBudget -= reservedFee;
                initiations++;
                continue;
            }

            try
            {
                var request = new RebalanceRequest(
                    NodeId: node.Id,
                    SourceChannelId: plan.SourceChannelId,
                    TargetPubkey: plan.DestinationPeerPubKey,
                    AmountSats: plan.AmountSats,
                    MaxFeePct: plan.MaxFeePct,
                    IsManual: false,
                    // Keep retries within the profitable ceiling
                    RetryMaxFeePct: plan.MaxFeePct);

                await _rebalanceService.RebalanceAsync(request, CancellationToken.None);

                // Audit the performed rebalance
                await _auditService.LogSystemAsync(
                    AuditActionType.RebalanceInitiated,
                    AuditEventType.Attempt,
                    AuditObjectType.Rebalance,
                    plan.SourceChannelId.ToString(),
                    new
                    {
                        NodeName = node.Name,
                        NodeId = node.Id,
                        NodePubKey = node.PubKey,
                        SourceChannelId = plan.SourceChannelId,
                        TargetPubkey = plan.DestinationPeerPubKey,
                        AmountSats = plan.AmountSats,
                        MaxFeePct = plan.MaxFeePct,
                        ReservedFeeSats = reservedFee,
                        BudgetRemainingSats = remainingBudget - reservedFee,
                        BudgetTotalSats = budgetSats,
                        InFlightCount = inFlight + initiations + 1,
                        MaxInFlightCount = maxInFlight,
                        Reason = plan.Reason
                    });

                remainingBudget -= reservedFee;
                initiations++;
                _logger.LogInformation("Node {NodeName}: initiated rebalance — {Reason}", node.Name, plan.Reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Node {NodeName}: failed to initiate rebalance ({Reason})", node.Name, plan.Reason);
            }
        }

        _logger.LogInformation(
            "Node {NodeName}: initiated {Count} of {PlannedCount} planned rebalance(s), remaining budget {Remaining}/{Budget} sats",
            node.Name, initiations, plans.Count, remainingBudget, budgetSats);
    }
}
