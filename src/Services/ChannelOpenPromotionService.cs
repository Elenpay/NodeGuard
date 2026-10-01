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
using NodeGuard.Jobs;
using Quartz;

namespace NodeGuard.Services;

public interface IChannelOpenPromotionService
{
    /// <summary>
    /// Capacity may differ from what was suggested. The request still follows the normal approval and
    /// signing path — promotion removes the decision, never a signature.
    /// </summary>
    Task<(bool, string?)> Promote(int recommendationId, long capacitySats, string? userId);

    Task<(bool, string?)> Dismiss(int recommendationId, string reason, string? userId);
}

public class ChannelOpenPromotionService : IChannelOpenPromotionService
{
    private readonly ILogger<ChannelOpenPromotionService> _logger;
    private readonly IChannelOpenRecommendationRepository _recommendationRepository;
    private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
    private readonly INodeRepository _nodeRepository;
    private readonly IWalletRepository _walletRepository;
    private readonly ILightningService _lightningService;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IAuditService _auditService;

    public ChannelOpenPromotionService(
        ILogger<ChannelOpenPromotionService> logger,
        IChannelOpenRecommendationRepository recommendationRepository,
        IChannelOperationRequestRepository channelOperationRequestRepository,
        INodeRepository nodeRepository,
        IWalletRepository walletRepository,
        ILightningService lightningService,
        ISchedulerFactory schedulerFactory,
        IAuditService auditService)
    {
        _logger = logger;
        _recommendationRepository = recommendationRepository;
        _channelOperationRequestRepository = channelOperationRequestRepository;
        _nodeRepository = nodeRepository;
        _walletRepository = walletRepository;
        _lightningService = lightningService;
        _schedulerFactory = schedulerFactory;
        _auditService = auditService;
    }

    public async Task<(bool, string?)> Promote(int recommendationId, long capacitySats, string? userId)
    {
        var (recommendation, loadError) = await LoadOpen(recommendationId, "promoted");
        if (recommendation == null)
        {
            return (false, loadError);
        }

        // GetById includes the Node, and the FK is required, so it is always populated.
        var node = recommendation.Node;
        if (node == null)
        {
            return (false, "Source node not found");
        }

        if (!node.AutoChannelOpenWalletId.HasValue)
        {
            return (false, "The node has no funding wallet configured for channel opens");
        }

        if (capacitySats <= 0)
        {
            return (false, "Capacity must be greater than zero");
        }

        try
        {
            // The peer is not a managed node, so it may not have a row yet.
            var destNode = await _nodeRepository.GetOrCreateByPubKey(recommendation.PeerPubKey, _lightningService);

            // A second channel to this peer arrives full, so at the global default it would be the
            // cheapest route to a peer the drain evidence proves is expensive. It opens at the price we
            // already charge that peer, captured with the evidence and gated on — asking LND again here
            // would reprice off gossip and could contradict the number the operator approved.
            if (recommendation.InheritedFeeRatePpm is not > 0)
            {
                return (false, $"Recommendation {recommendationId} carries no inherited fee policy, so there is no price to open at — it predates fee capture and will be replaced on the next run");
            }

            var request = new ChannelOperationRequest
            {
                SatsAmount = capacitySats,
                AmountCryptoUnit = MoneyUnit.Satoshi,
                Description = BuildDescription(recommendation),
                Status = ChannelOperationRequestStatus.Pending,
                RequestType = OperationRequestType.Open,
                WalletId = node.AutoChannelOpenWalletId,
                SourceNodeId = node.Id,
                DestNodeId = destNode.Id,
                UserId = userId,
                IsChannelPrivate = false,
                MempoolRecommendedFeesType = MempoolRecommendedFeesType.EconomyFee,
                InitialChannelBaseFeeMsat = recommendation.InheritedBaseFeeMsat,
                InitialChannelFeeRatePpm = recommendation.InheritedFeeRatePpm
            };

            var (added, addError) = await _channelOperationRequestRepository.AddAsync(request);
            if (!added)
            {
                _logger.LogError("Could not create the channel operation request for recommendation {Id}: {Error}",
                    recommendationId, addError);
                return (false, addError);
            }

            recommendation.Status = ChannelOpenRecommendationStatus.Promoted;
            recommendation.ChannelOperationRequestId = request.Id;

            var (updated, updateError) = await _recommendationRepository.Update(recommendation);
            if (!updated)
            {
                _logger.LogError("Channel operation request {RequestId} was created but recommendation {Id} could not be marked promoted: {Error}",
                    request.Id, recommendationId, updateError);
                return (false, updateError);
            }

            await _auditService.LogAsync(
                AuditActionType.Create,
                AuditEventType.Success,
                AuditObjectType.ChannelOperationRequest,
                request.Id.ToString(),
                new
                {
                    RequestId = request.Id,
                    RecommendationId = recommendationId,
                    SuggestedCapacitySats = recommendation.SuggestedCapacitySats,
                    ApprovedCapacitySats = capacitySats,
                    PeerPubKey = recommendation.PeerPubKey,
                    InitialChannelBaseFeeMsat = recommendation.InheritedBaseFeeMsat,
                    InitialChannelFeeRatePpm = recommendation.InheritedFeeRatePpm,
                    Description = $"Channel open request created from recommendation {recommendationId} " +
                                  $"({capacitySats} sats, suggested {recommendation.SuggestedCapacitySats} sats, " +
                                  $"opening at {recommendation.InheritedFeeRatePpm} ppm / {recommendation.InheritedBaseFeeMsat} msat, " +
                                  $"the most we already charge this peer)"
                });

            _logger.LogInformation(
                "Promoted channel open recommendation {Id} to request {RequestId}: {Capacity} sats to {Peer} (suggested {Suggested})",
                recommendationId, request.Id, capacitySats, recommendation.PeerPubKey, recommendation.SuggestedCapacitySats);

            return await DispatchIfNoSignaturesNeeded(request, node.AutoChannelOpenWalletId.Value);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error promoting channel open recommendation {Id}", recommendationId);
            return (false, e.Message);
        }
    }

    /// <summary>
    /// Without this a promoted hot-wallet request dead-ends: Pending with no template, and its only UI
    /// action opens an external-signer modal a hot wallet has no signer for.
    /// </summary>
    private async Task<(bool, string?)> DispatchIfNoSignaturesNeeded(ChannelOperationRequest request, int walletId)
    {
        var wallet = await _walletRepository.GetById(walletId);
        if (wallet is not { IsHotWallet: true })
        {
            // Multisig / watch-only: leave it Pending for the signers, which is the intended flow.
            return (true, null);
        }

        var (templatePsbt, noUtxosAvailable) = await _lightningService.GenerateTemplatePSBT(request);
        if (templatePsbt == null)
        {
            var reason = noUtxosAvailable
                ? "No UTXOs available in the funding wallet"
                : "Could not generate the template PSBT";

            request.Status = ChannelOperationRequestStatus.Failed;
            request.StatusLogs.Add(ChannelStatusLog.Error(reason));
            _channelOperationRequestRepository.Update(request);
            _logger.LogError("Channel open request {RequestId} failed before dispatch: {Reason}", request.Id, reason);

            return (false, reason);
        }

        var persisted = await _channelOperationRequestRepository.GetById(request.Id);
        if (persisted is not { AreAllRequiredHumanSignaturesCollected: true })
        {
            // Nothing broken — the wallet simply still needs signatures. Leave it for them.
            return (true, null);
        }

        var map = new JobDataMap();
        map.Put("openRequestId", request.Id);
        var retryList = RetriableJob.ParseRetryListFromString(Constants.JOB_RETRY_INTERVAL_LIST_IN_MINUTES);
        var job = RetriableJob.Create<ChannelOpenJob>(map, request.Id.ToString(), retryList);

        var scheduler = await _schedulerFactory.GetScheduler();
        await scheduler.ScheduleJob(job.Job, job.Trigger);

        _logger.LogInformation("Scheduled ChannelOpenJob for hot-wallet request {RequestId}", request.Id);

        return (true, null);
    }

    public async Task<(bool, string?)> Dismiss(int recommendationId, string reason, string? userId)
    {
        var (recommendation, loadError) = await LoadOpen(recommendationId, "dismissed");
        if (recommendation == null)
        {
            return (false, loadError);
        }

        recommendation.Status = ChannelOpenRecommendationStatus.Dismissed;
        recommendation.DismissReason = reason;

        var (updated, error) = await _recommendationRepository.Update(recommendation);
        if (!updated)
        {
            return (false, error);
        }

        await _auditService.LogAsync(
            AuditActionType.Reject,
            AuditEventType.Success,
            AuditObjectType.ChannelOpenRecommendation,
            recommendationId.ToString(),
            new
            {
                RecommendationId = recommendationId,
                PeerPubKey = recommendation.PeerPubKey,
                Reason = reason,
                Description = $"Channel open recommendation {recommendationId} dismissed: {reason}"
            });

        return (true, null);
    }

    private async Task<(ChannelOpenRecommendation?, string?)> LoadOpen(int recommendationId, string verb)
    {
        var recommendation = await _recommendationRepository.GetById(recommendationId);
        if (recommendation == null)
        {
            return (null, "Recommendation not found");
        }

        return recommendation.Status == ChannelOpenRecommendationStatus.Open
            ? (recommendation, null)
            : (null, $"Recommendation is {recommendation.Status}, only Open ones can be {verb}");
    }

    /// <summary>
    /// Summarised onto the request so an approver sees why it exists without leaving that page.
    /// </summary>
    private static string BuildDescription(ChannelOpenRecommendation r) =>
        $"Auto channel open: {r.Bursts} refused payments, " +
        $"{r.MissedFeeMsat / 1000} sats of missed fees. " +
        $"Drain {r.OutSats + r.MissedSats} sats vs refill {r.InSats} sats ({r.Regime} sizing). " +
        $"Largest refused payment {r.MaxBurstPaymentSats} sats.";
}
