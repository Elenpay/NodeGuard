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

using NBitcoin;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Demand-driven channel opening: turns the forwards we refused for lack of outbound into
/// <see cref="ChannelOpenRecommendation"/> rows.
/// <para>
/// In <see cref="AutoChannelOpenMode.Auto"/> promotion removes the human *decision*, not the human
/// *signature*: a multisig wallet still collects signatures through the normal approval flow.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public class AutoChannelOpenJob : IJob
{
    /// <summary>
    /// Feeds only the capital-efficiency floor, so precision here does not change decisions near the
    /// threshold. This calculation assumes a typical channel open around 140vBytes and close around 170vBytes.
    /// </summary>
    private const int EstimatedOpenCloseVBytes = 310;

    private readonly ILogger<AutoChannelOpenJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IForwardingHtlcEventRepository _forwardingHtlcEventRepository;
    private readonly IChannelOpenRecommendationRepository _recommendationRepository;
    private readonly IChannelOpenPromotionService _promotionService;
    private readonly ILightningService _lightningService;
    private readonly INBXplorerService _nbXplorerService;
    private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
    private readonly IAuditService _auditService;

    public AutoChannelOpenJob(
        ILogger<AutoChannelOpenJob> logger,
        INodeRepository nodeRepository,
        IForwardingHtlcEventRepository forwardingHtlcEventRepository,
        IChannelOpenRecommendationRepository recommendationRepository,
        IChannelOpenPromotionService promotionService,
        ILightningService lightningService,
        INBXplorerService nbXplorerService,
        IChannelOperationRequestRepository channelOperationRequestRepository,
        IAuditService auditService)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _forwardingHtlcEventRepository = forwardingHtlcEventRepository;
        _recommendationRepository = recommendationRepository;
        _promotionService = promotionService;
        _lightningService = lightningService;
        _nbXplorerService = nbXplorerService;
        _channelOperationRequestRepository = channelOperationRequestRepository;
        _auditService = auditService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        // Killing the routing engine must stop every actuator, most of all the one that spends on-chain.
        if (!Constants.ROUTING_ENGINE_ENABLED || !Constants.AUTO_CHANNEL_OPEN_ENABLED)
        {
            return;
        }

        _logger.LogInformation("Starting {JobName}...", nameof(AutoChannelOpenJob));

        try
        {
            // With this we can aproximate the costs of opening and closing channels on-chain.
            var feeRate = await _nbXplorerService.GetFeesByType(MempoolRecommendedFeesType.HourFee);
            if (feeRate == null)
            {
                _logger.LogWarning("{JobName}: no feerate available, skipping run", nameof(AutoChannelOpenJob));
                return;
            }

            var nodes = await _nodeRepository.GetAllWithAutoChannelOpenEnabled();
            _logger.LogInformation("Found {Count} node(s) with automatic channel opening enabled", nodes.Count);

            foreach (var node in nodes)
            {
                try
                {
                    await ProcessNode(node, feeRate.Value);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error processing automatic channel opening for node {NodeName} ({NodePubKey})",
                        node.Name, node.PubKey);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error in {JobName}", nameof(AutoChannelOpenJob));
        }

        _logger.LogInformation("{JobName} ended", nameof(AutoChannelOpenJob));
    }

    private async Task ProcessNode(Node node, decimal feeRate)
    {
        var now = DateTimeOffset.UtcNow;

        // Every run starts clean: the upsert below re-opens whatever still qualifies, so the panel is
        // always this run's output and nothing lingers from an earlier one.
        var expired = await _recommendationRepository.ExpireOpen(node.Id);
        if (expired > 0)
        {
            _logger.LogInformation("Node {NodeName}: expired {Count} open recommendation(s) ahead of this run",
                node.Name, expired);
        }

        // Ahead of the cooldown this run derives: a promotion whose channel never opened committed
        // nothing to the peer, so leaving it Promoted would suppress the peer for a channel it never got.
        var unrealized = await _recommendationRepository.FailUnrealizedPromotions(node.Id);
        if (unrealized > 0)
        {
            _logger.LogInformation("Node {NodeName}: {Count} promoted recommendation(s) whose channel never opened marked failed, releasing their peers from the cooldown",
                node.Name, unrealized);
        }

        // Check if a wallet is set to the node, otherwise we cannot proceed with channel opening.
        if (node.AutoChannelOpenWalletId == null)
        {
            _logger.LogInformation("Node {NodeName}: no wallet set for automatic channel opening", node.Name);
            return;
        }

        var remainingBudget = await GetRemainingBudget(node, now);
        if (remainingBudget <= 0)
        {
            _logger.LogInformation("Node {NodeName}: channel open budget exhausted", node.Name);
            return;
        }

        var since = now.AddHours(-Constants.AUTO_CHANNEL_OPEN_WINDOW_HOURS);
        var failures = await _forwardingHtlcEventRepository.GetInsufficientBalanceFailures(node.PubKey, since);
        if (failures.Count == 0)
        {
            _logger.LogDebug("Node {NodeName}: no refused forwards in the window", node.Name);
            return;
        }

        var (failuresByPeer, contexts) = await ResolveDemand(node, failures, since);
        if (contexts.Count == 0)
        {
            _logger.LogInformation("Node {NodeName}: none of the refused forwards resolve to a current peer", node.Name);
            return;
        }

        var walletBalanceSats = await GetWalletBalanceSats(node);
        if (walletBalanceSats <= 0)
        {
            _logger.LogInformation("Node {NodeName}: funding wallet has no confirmed balance", node.Name);
            return;
        }

        var tunables = ChannelOpenInitiatorTunables.FromConstants(
            node,
            walletBalanceSats,
            remainingBudget,
            (long)(feeRate * EstimatedOpenCloseVBytes));

        var classification = ChannelOpenInitiatorService.BuildPlans(failuresByPeer, contexts, tunables);

        foreach (var rejection in classification.Rejected)
        {
            _logger.LogInformation("Node {NodeName}: no channel open to {Peer} — {Reason}",
                node.Name, rejection.PeerPubKey, rejection.Reason);
        }

        // Each plan was sized against the full remaining budget, so spend has to be tracked across the
        // run — otherwise a per-run cap above 1 would commit the budget once per plan.
        foreach (var plan in classification.Plans)
        {
            if (plan.SuggestedCapacitySats > remainingBudget)
            {
                _logger.LogInformation("Node {NodeName}: skipping a {Capacity} sats plan, only {Remaining} sats of budget left",
                    node.Name, plan.SuggestedCapacitySats, remainingBudget);
                continue;
            }

            if (await RecordAndMaybePromote(node, plan))
            {
                remainingBudget -= plan.SuggestedCapacitySats;
            }
        }
    }

    /// <summary>
    /// Peer-level throughout, because non-strict forwarding makes per-channel counts an arbitrary
    /// partition of one signal. Every input is fetched once for the whole node, never per channel.
    /// </summary>
    private async Task<(ILookup<string, PeerDemandFailure>, Dictionary<string, PeerDemandContext>)> ResolveDemand(
        Node node,
        IReadOnlyList<ForwardingHtlcFailure> failures,
        DateTimeOffset since)
    {
        var empty = Array.Empty<PeerDemandFailure>().ToLookup(x => x.PeerPubKey);
        var contexts = new Dictionary<string, PeerDemandContext>();

        var listChannelsTask = _lightningService.ListChannels(node);
        var policyTask = _lightningService.GetLocalOutboundPoliciesAsync(node);
        var outTask = _forwardingHtlcEventRepository.GetOutgoingAmountsMsatByChannel(node.PubKey, since);
        var inTask = _forwardingHtlcEventRepository.GetIncomingAmountsMsatByChannel(node.PubKey, since);
        var lastDecisionTask = _recommendationRepository.GetLastDecisionByPeer(node.Id);
        await Task.WhenAll(listChannelsTask, policyTask, outTask, inTask, lastDecisionTask);

        var listChannels = listChannelsTask.Result;
        if (listChannels == null)
        {
            _logger.LogWarning("Node {NodeName}: could not list channels", node.Name);
            return (empty, contexts);
        }

        var peerByChanId = listChannels.Channels.ToDictionary(x => x.ChanId, x => x.RemotePubkey);
        var policyByChanId = policyTask.Result;
        var outByChanId = outTask.Result;
        var inByChanId = inTask.Result;
        var lastDecisionByPeer = lastDecisionTask.Result;
        var cooldownCutoff = DateTimeOffset.UtcNow.AddHours(-Constants.AUTO_CHANNEL_OPEN_PEER_COOLDOWN_HOURS);

        // Group failures by peer, ignoring channels that are no longer listed.
        var failuresByPeer = failures
            .Where(x => peerByChanId.ContainsKey(x.OutgoingChannelId))
            .Select(x => new PeerDemandFailure(
                PeerPubKey: peerByChanId[x.OutgoingChannelId],
                OutgoingChannelId: x.OutgoingChannelId,
                EventTimestamp: x.EventTimestamp,
                OutgoingAmountMsat: x.OutgoingAmountMsat,
                RoutingFeePpm: x.RoutingFeePpm))
            .ToLookup(x => x.PeerPubKey);

        foreach (var group in failuresByPeer)
        {
            var peerPubKey = group.Key;
            var peerChannels = listChannels.Channels.Where(x => x.RemotePubkey == peerPubKey).ToList();

            // Usable, not raw: the reserve can never be forwarded, and on a drained channel it is
            // most of what local_balance reports.
            var usableLocal = peerChannels.Sum(x => Math.Max(0, x.LocalBalance - x.LocalChanReserveSat));

            // If we don't have the policy information, we cannot make an informed decision about opening a channel.
            if (policyByChanId == null)
            {
                _logger.LogWarning("Node {NodeName}: no policy information available for peer {PeerPubKey}", node.Name, peerPubKey);
                continue;
            }

            // Use one advertised policy as the peer's representative: choose the highest proportional
            // fee rate, keeping its base fee from that same channel rather than combining policies.
            var highestFeeRatePolicy = peerChannels
                .Select(x => policyByChanId.TryGetValue(x.ChanId, out var p) ? p : null)
                .Where(x => x != null)
                .MaxBy(x => x!.FeeRatePpm);
            
            if (highestFeeRatePolicy == null)
            {
                _logger.LogWarning("Node {NodeName}: no valid outbound policy found for peer {PeerPubKey}", node.Name, peerPubKey);
                continue;
            }

            contexts[peerPubKey] = new PeerDemandContext(
                PeerPubKey: peerPubKey,
                PeerAlias: FindAliasForPeer(failures, peerChannels),
                UsableLocalSats: usableLocal,
                SettledOutSats: peerChannels.Sum(x => outByChanId.GetValueOrDefault(x.ChanId)) / 1000,
                SettledInSats: peerChannels.Sum(x => inByChanId.GetValueOrDefault(x.ChanId)) / 1000,
                CurrentOutboundPolicy: highestFeeRatePolicy,
                PeerOnCooldown: lastDecisionByPeer.TryGetValue(peerPubKey, out var last) && last > cooldownCutoff);
        }

        return (failuresByPeer, contexts);
    }

    private static string? FindAliasForPeer(IReadOnlyList<ForwardingHtlcFailure> failures, IEnumerable<Lnrpc.Channel> peerChannels)
    {
        var ids = peerChannels.Select(x => x.ChanId).ToHashSet();

        return failures.FirstOrDefault(x => ids.Contains(x.OutgoingChannelId))?.OutgoingPeerAlias;
    }

    private async Task<long> GetWalletBalanceSats(Node node)
    {
        if (node.AutoChannelOpenWallet == null)
        {
            return 0;
        }

        var balance = await _lightningService.GetWalletBalance(node.AutoChannelOpenWallet);

        return balance?.Confirmed is Money confirmed ? confirmed.Satoshi : 0;
    }

    /// <summary>
    /// Rolls the budget period over and persists it when elapsed — a Get that writes.
    /// </summary>
    private async Task<long> GetRemainingBudget(Node node, DateTimeOffset now)
    {
        var budgetSats = node.AutoChannelOpenBudgetSats ?? 0;
        if (budgetSats <= 0)
        {
            _logger.LogInformation("Node {NodeName}: no channel open budget configured; skipping", node.Name);
            return 0;
        }

        var refreshInterval = node.AutoChannelOpenBudgetRefreshInterval
                              ?? TimeSpan.FromHours(Constants.AUTO_CHANNEL_OPEN_DEFAULT_BUDGET_REFRESH_HOURS);
        if (!node.AutoChannelOpenBudgetStartDatetime.HasValue ||
            now - node.AutoChannelOpenBudgetStartDatetime.Value >= refreshInterval)
        {
            _logger.LogInformation("Refreshing channel open budget for node {NodeName}", node.Name);
            node.AutoChannelOpenBudgetStartDatetime = now;
            _nodeRepository.Update(node);
        }

        var periodStart = node.AutoChannelOpenBudgetStartDatetime ?? now;
        var committed = await _channelOperationRequestRepository.GetOpenSatsCommittedSince(node.Id, periodStart);

        return budgetSats - committed;
    }

    /// <summary>
    /// Returns true only when the run has committed capital, so the caller can decrement its budget.
    /// </summary>
    private async Task<bool> RecordAndMaybePromote(Node node, ChannelOpenRecommendation plan)
    {
        plan.NodeId = node.Id;

        var (recommendation, error) = await _recommendationRepository.Upsert(plan);
        if (recommendation == null)
        {
            _logger.LogError("Node {NodeName}: could not record channel open recommendation — {Error}", node.Name, error);
            return false;
        }

        // Checked before the log and the audit entry: an untouched row means nothing was recorded, and
        // claiming a creation that did not happen would repeat every run until the open resolves.
        if (recommendation.Status != ChannelOpenRecommendationStatus.Open)
        {
            _logger.LogDebug("Node {NodeName}: skipping {Peer}, a channel open to it is still in progress",
                node.Name, plan.PeerAlias ?? plan.PeerPubKey);

            return false;
        }

        _logger.LogInformation(
            "Node {NodeName}: recommending a {Capacity} sats channel to {Peer} ({Regime}) — {Bursts} bursts across {Chans} existing channel(s), {MissedFee} msat missed",
            node.Name, plan.SuggestedCapacitySats, plan.PeerAlias ?? plan.PeerPubKey, plan.Regime,
            plan.Bursts, plan.ChannelsInvolved, plan.MissedFeeMsat);

        await _auditService.LogAsync(
            AuditActionType.Create,
            AuditEventType.Success,
            AuditObjectType.ChannelOpenRecommendation,
            recommendation.Id.ToString(),
            new
            {
                NodeId = node.Id,
                plan.PeerPubKey,
                plan.SuggestedCapacitySats,
                Regime = plan.Regime.ToString(),
                plan.Bursts,
                plan.MissedFeeMsat,
                Description = $"Channel open recommended to {plan.PeerPubKey} for {plan.SuggestedCapacitySats} sats"
            });

        if (node.AutoChannelOpenMode != AutoChannelOpenMode.Auto)
        {
            return false;
        }

        if (node.RoutingEngineDryRun)
        {
            _logger.LogInformation("Node {NodeName}: dry run, not promoting the recommendation", node.Name);
            return false;
        }

        var (promoted, promotionError) = await _promotionService.Promote(
            recommendation.Id, recommendation.SuggestedCapacitySats, userId: null);

        if (!promoted)
        {
            _logger.LogError("Node {NodeName}: could not promote recommendation {Id} — {Error}",
                node.Name, recommendation.Id, promotionError);
        }

        return promoted;
    }
}
