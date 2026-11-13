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

namespace NodeGuard.Jobs;

/// <summary>
/// Opens a fixed-size channel to a peer whose route earned us fees but keeps running out of outbound.
/// Writes <see cref="ChannelOpenRecommendation"/> rows, and in <see cref="AutoChannelOpenMode.Auto"/>
/// promotes them. Promotion removes the human *decision*, not the *signature*: a multisig wallet still
/// collects signatures through the normal approval flow.
/// </summary>
[DisallowConcurrentExecution]
public class AutoChannelOpenJob : IJob
{
    private readonly ILogger<AutoChannelOpenJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IForwardingHtlcEventRepository _forwardingHtlcEventRepository;
    private readonly IChannelOpenRecommendationRepository _recommendationRepository;
    private readonly IChannelOpenPromotionService _promotionService;
    private readonly ILightningService _lightningService;
    private readonly INBXplorerService _nbXplorerService;
    private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
    private readonly IAuditService _auditService;
    private readonly ICoinSelectionService _coinSelectionService;

    public AutoChannelOpenJob(
        ILogger<AutoChannelOpenJob> logger,
        INodeRepository nodeRepository,
        IForwardingHtlcEventRepository forwardingHtlcEventRepository,
        IChannelOpenRecommendationRepository recommendationRepository,
        IChannelOpenPromotionService promotionService,
        ILightningService lightningService,
        INBXplorerService nbXplorerService,
        IChannelOperationRequestRepository channelOperationRequestRepository,
        IAuditService auditService,
        ICoinSelectionService coinSelectionService)
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
        _coinSelectionService = coinSelectionService;
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
            // HourFee is at or above the EconomyFee the funding tx is built at, so the wallet check errs safe.
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

        if (node.AutoChannelOpenWalletId == null)
        {
            _logger.LogInformation("Node {NodeName}: no wallet set for automatic channel opening", node.Name);
            return;
        }

        if (node.AutoChannelOpenSizeSats is not > 0)
        {
            _logger.LogInformation("Node {NodeName}: no channel size set for automatic channel opening", node.Name);
            return;
        }

        var remainingBudget = await GetRemainingBudget(node, now);
        if (remainingBudget <= 0)
        {
            _logger.LogInformation("Node {NodeName}: channel open budget exhausted", node.Name);
            return;
        }

        var peers = await BuildPeerSnapshots(node, now);
        if (peers.Count == 0)
        {
            _logger.LogDebug("Node {NodeName}: no peer refused forwards in the shortage window", node.Name);
            return;
        }

        var walletSpendableSats = await GetWalletSpendableSats(node, feeRate);
        var tunables = ChannelOpenInitiatorTunables.FromConstants(node, walletSpendableSats, remainingBudget);
        var classification = ChannelOpenInitiatorService.Evaluate(peers, tunables);

        foreach (var rejection in classification.Rejected)
        {
            _logger.LogInformation("Node {NodeName}: no channel open to {Peer} — {Reason}",
                node.Name, rejection.PeerPubKey, rejection.Reason);
        }

        // Each plan was checked against the full remaining budget, so spend has to be tracked across the
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
    /// One snapshot per peer that refused forwards in the shortage window. Every input is fetched once
    /// for the whole node, never per channel.
    /// </summary>
    private async Task<List<PeerSnapshot>> BuildPeerSnapshots(Node node, DateTimeOffset now)
    {
        var lookbackSince = now.AddDays(-Constants.AUTO_CHANNEL_OPEN_LOOKBACK_DAYS);
        var shortageSince = now.AddHours(-Constants.AUTO_CHANNEL_OPEN_SHORTAGE_HOURS);

        var refusalsTask = _forwardingHtlcEventRepository.GetInsufficientBalanceCountsByChannel(node.PubKey, shortageSince);
        var feesTask = _forwardingHtlcEventRepository.GetEarnedFeesMsatByChannel(node.PubKey, lookbackSince);
        var outTask = _forwardingHtlcEventRepository.GetOutgoingAmountsMsatByChannel(node.PubKey, lookbackSince);
        var listChannelsTask = _lightningService.ListChannels(node);
        var policyTask = _lightningService.GetLocalOutboundPoliciesAsync(node);
        var lastDecisionTask = _recommendationRepository.GetLastDecisionByPeer(node.Id);
        await Task.WhenAll(refusalsTask, feesTask, outTask, listChannelsTask, policyTask, lastDecisionTask);

        var refusalsByChanId = refusalsTask.Result;
        if (refusalsByChanId.Count == 0)
        {
            return [];
        }

        var listChannels = listChannelsTask.Result;
        if (listChannels == null)
        {
            _logger.LogWarning("Node {NodeName}: could not list channels", node.Name);
            return [];
        }

        var feesByChanId = feesTask.Result;
        var outByChanId = outTask.Result;
        var policyByChanId = policyTask.Result;
        var lastDecisionByPeer = lastDecisionTask.Result;
        var cooldownCutoff = now.AddHours(-Constants.AUTO_CHANNEL_OPEN_PEER_COOLDOWN_HOURS);

        // Fees and refusals on channels that have since closed are dropped with them: only the peer's
        // current channels map back to a pubkey.
        return listChannels.Channels
            .GroupBy(x => x.RemotePubkey)
            .Select(peerChannels => (Peer: peerChannels.Key, Channels: peerChannels.ToList()))
            .Where(x => x.Channels.Sum(c => refusalsByChanId.GetValueOrDefault(c.ChanId)) > 0)
            .Select(x => new PeerSnapshot(
                PeerPubKey: x.Peer,
                EarnedFeeSats: x.Channels.Sum(c => feesByChanId.GetValueOrDefault(c.ChanId)) / 1000,
                SettledOutSats: x.Channels.Sum(c => outByChanId.GetValueOrDefault(c.ChanId)) / 1000,
                Refusals: x.Channels.Sum(c => refusalsByChanId.GetValueOrDefault(c.ChanId)),
                // Usable, not raw: the reserve can never be forwarded, and on a drained channel it is
                // most of what local_balance reports.
                UsableLocalSats: x.Channels.Sum(c => Math.Max(0, c.LocalBalance - c.LocalChanReserveSat)),
                CapacitySats: x.Channels.Sum(c => c.Capacity),
                // The highest rate, with the base fee from that same channel rather than a mix.
                OutboundPolicy: x.Channels
                    .Select(c => policyByChanId != null && policyByChanId.TryGetValue(c.ChanId, out var p) ? p : null)
                    .Where(p => p != null)
                    .MaxBy(p => p!.FeeRatePpm),
                OnCooldown: lastDecisionByPeer.TryGetValue(x.Peer, out var last) && last > cooldownCutoff))
            .ToList();
    }

    /// <summary>
    /// The biggest channel the wallet can fund from the coins coin selection would hand it, with the
    /// funding fee already taken out.
    /// </summary>
    private async Task<long> GetWalletSpendableSats(Node node, decimal feeRate)
    {
        var derivationStrategy = node.AutoChannelOpenWallet?.GetDerivationStrategy();
        if (derivationStrategy == null)
        {
            return 0;
        }

        var utxos = await _coinSelectionService.GetAvailableUTXOsAsync(derivationStrategy);

        return LightningHelper.MaxFundableChannelSats(derivationStrategy, utxos, feeRate);
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
                node.Name, plan.PeerPubKey);

            return false;
        }

        _logger.LogInformation(
            "Node {NodeName}: recommending a {Capacity} sats channel to {Peer} — earned {EarnedFee} sats in {LookbackDays}d, {Refusals} refused forward(s) in {ShortageHours}h, {UsableLocal}/{PeerCapacity} sats usable outbound",
            node.Name, plan.SuggestedCapacitySats, plan.PeerPubKey,
            plan.EarnedFeeSats, Constants.AUTO_CHANNEL_OPEN_LOOKBACK_DAYS,
            plan.Refusals, Constants.AUTO_CHANNEL_OPEN_SHORTAGE_HOURS,
            plan.UsableLocalSats, plan.PeerCapacitySats);

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
                plan.EarnedFeeSats,
                plan.Refusals,
                plan.UsableLocalSats,
                plan.PeerCapacitySats,
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
