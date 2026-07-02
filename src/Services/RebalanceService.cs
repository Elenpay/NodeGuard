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

using Lnrpc;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Jobs;
using Quartz;

namespace NodeGuard.Services;

public record RebalanceRequest(
    int NodeId,
    int SourceChannelId,
    string TargetPubkey,
    long AmountSats,
    double? MaxFeePct,
    int TimeoutSeconds = 60,
    bool IsManual = true,
    string? UserRequestorId = null,
    double? AmountBackoffRatio = null,
    int? MaxAttempts = null,
    double? RetryMaxFeePct = null);

public interface IRebalanceService
{
    /// <summary>
    /// Persists a new Rebalance row, runs the first attempt synchronously, and returns
    /// the row in its post-attempt state. Eligible failures are queued for retry via Quartz.
    /// </summary>
    Task<Rebalance> RebalanceAsync(RebalanceRequest request, CancellationToken ct = default);

    /// <summary>
    /// Runs (or re-runs) the probe + payment for an existing Rebalance row. Used by
    /// <see cref="RebalanceJob"/>.
    /// </summary>
    Task<Rebalance> ExecuteAsync(int rebalanceId, CancellationToken ct = default);
}

public class RebalanceService : IRebalanceService
{
    private readonly ILogger<RebalanceService> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly IRebalanceRepository _rebalanceRepository;
    private readonly ILightningService _lightningService;
    private readonly IAuditService _auditService;
    private readonly ISchedulerFactory _schedulerFactory;

    public RebalanceService(
        ILogger<RebalanceService> logger,
        INodeRepository nodeRepository,
        IChannelRepository channelRepository,
        IRebalanceRepository rebalanceRepository,
        ILightningService lightningService,
        IAuditService auditService,
        ISchedulerFactory schedulerFactory)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _channelRepository = channelRepository;
        _rebalanceRepository = rebalanceRepository;
        _lightningService = lightningService;
        _auditService = auditService;
        _schedulerFactory = schedulerFactory;
    }

    public async Task<Rebalance> RebalanceAsync(RebalanceRequest request, CancellationToken ct = default)
    {
        if (request.AmountSats <= 0)
            throw new ArgumentException(
                "Rebalance amount is zero — channel is already at or above the requested inbound ratio, no rebalance needed.",
                nameof(request.AmountSats));

        if (request.AmountBackoffRatio is { } ratio && (ratio <= 0.0 || ratio > 1.0))
            throw new ArgumentException(
                "Amount backoff ratio must be in the half-open interval (0, 1]. 1 never shrinks (every attempt retries the full amount); values below 1 shrink the amount on each retry.",
                nameof(request.AmountBackoffRatio));

        if (request.MaxAttempts is { } attempts && attempts <= 0)
            throw new ArgumentException(
                "Max attempts must be at least 1.",
                nameof(request.MaxAttempts));

        if (request.RetryMaxFeePct is { } retryPct && retryPct <= 0.0)
            throw new ArgumentException(
                "Retry max fee % must be greater than 0.",
                nameof(request.RetryMaxFeePct));

        if (request.SourceChannelId <= 0)
            throw new ArgumentException(
                "Source channel id is required.",
                nameof(request.SourceChannelId));

        if (string.IsNullOrWhiteSpace(request.TargetPubkey))
            throw new ArgumentException(
                "Target pubkey is required.",
                nameof(request.TargetPubkey));

        var node = await _nodeRepository.GetById(request.NodeId);
        if (node == null)
            throw new ArgumentException($"Node {request.NodeId} not found", nameof(request.NodeId));

        var sourceChannel = await _channelRepository.GetById(request.SourceChannelId);
        if (sourceChannel == null)
            throw new ArgumentException(
                $"Source channel {request.SourceChannelId} not found",
                nameof(request.SourceChannelId));
        var sourceChanIdLnd = sourceChannel.ChanId;

        var counterpartyPeerId = sourceChannel.SourceNodeId == node.Id
            ? sourceChannel.DestinationNodeId
            : sourceChannel.SourceNodeId;

        var counterpartyPeer = await _nodeRepository.GetById(counterpartyPeerId);
        if (counterpartyPeer == null)
            throw new InvalidOperationException(
                $"Counterparty peer node {counterpartyPeerId} not found for source channel {request.SourceChannelId}");

        if (counterpartyPeer.PubKey == request.TargetPubkey)
            throw new ArgumentException(
                "Target pubkey is the same as the source channel's counterparty peer; rebalance would be a no-op.",
                nameof(request.TargetPubkey));

        // LastHopPubkey constrains the receiving peer, not a specific channel — LND picks
        // which of that peer's channels to use. We don't accept or persist a target chan_id.
        var targetPubkey = request.TargetPubkey;

        var maxFeePct = ResolveMaxFeePct(request.MaxFeePct, Constants.REBALANCE_DEFAULT_MAX_FEE_PCT);

        var rebalance = new Rebalance
        {
            NodeId = node.Id,
            SourceNodePubKey = node.PubKey,
            Status = RebalanceStatus.Pending,
            IsManual = request.IsManual,
            AttemptNumber = 1,
            RequestedAmountSats = request.AmountSats,
            SatsAmount = request.AmountSats,
            MaxFeePct = maxFeePct,
            SourceChannelId = request.SourceChannelId,
            SourceChanIdLnd = sourceChanIdLnd,
            TargetPubkey = targetPubkey,
            UserRequestorId = request.UserRequestorId,
            TimeoutSeconds = request.TimeoutSeconds == 0 ? 60 : request.TimeoutSeconds,
            AmountBackoffRatio = request.AmountBackoffRatio,
            MaxAttempts = request.MaxAttempts,
            RetryMaxFeePct = request.RetryMaxFeePct,
        };

        var (added, addError) = await _rebalanceRepository.AddAsync(rebalance);
        if (!added)
        {
            _logger.LogError("Failed to persist rebalance: {Error}", addError);
            throw new InvalidOperationException($"Failed to persist rebalance: {addError}");
        }

        _logger.LogInformation(
            "Rebalance {RebalanceId} created (node={NodeId} '{NodeName}', amount={AmountSats} sats, maxFeePct={MaxFeePct}, sourceChanId={SourceChanIdLnd}, targetPubkey={TargetPubkey}, isManual={IsManual})",
            rebalance.Id, node.Id, node.Name, rebalance.RequestedAmountSats, rebalance.MaxFeePct,
            rebalance.SourceChanIdLnd, rebalance.TargetPubkey, rebalance.IsManual);

        await _auditService.LogAsync(
            AuditActionType.RebalanceInitiated,
            AuditEventType.Attempt,
            AuditObjectType.Rebalance,
            rebalance.Id.ToString(),
            new
            {
                rebalance.NodeId,
                rebalance.RequestedAmountSats,
                rebalance.MaxFeePct,
                rebalance.SourceChanIdLnd,
                rebalance.TargetPubkey,
                rebalance.IsManual,
                rebalance.AttemptNumber,
            });

        return await ExecuteAsync(rebalance.Id, ct);
    }

    public async Task<Rebalance> ExecuteAsync(int rebalanceId, CancellationToken ct = default)
    {
        var rebalance = await _rebalanceRepository.GetById(rebalanceId);
        if (rebalance == null)
            throw new InvalidOperationException($"Rebalance {rebalanceId} not found");

        var node = rebalance.Node ?? await _nodeRepository.GetById(rebalance.NodeId);
        if (node == null)
            throw new InvalidOperationException($"Node {rebalance.NodeId} not found for rebalance {rebalanceId}");

        _logger.LogInformation(
            "Executing rebalance {RebalanceId} attempt {Attempt}/{MaxAttempts} (node={NodeId} '{NodeName}', amount={AmountSats} sats, maxFeePct={MaxFeePct}, timeoutSeconds={TimeoutSeconds})",
            rebalance.Id, rebalance.AttemptNumber, rebalance.MaxAttempts ?? Constants.REBALANCE_MAX_ATTEMPTS,
            node.Id, node.Name, rebalance.SatsAmount, rebalance.MaxFeePct, rebalance.TimeoutSeconds);

        try
        {
            // Avoid double paying a rebalance that has already succeeded.
            if (!string.IsNullOrWhiteSpace(rebalance.PaymentHashHex))
            {
                byte[] paymentHash = Convert.FromHexString(rebalance.PaymentHashHex);
                var oldPayment = await _lightningService.TrackPaymentV2Async(node, paymentHash, ct);
                if (oldPayment != null)
                {
                    if (oldPayment.Status is Payment.Types.PaymentStatus.Succeeded)
                    {
                        _logger.LogInformation(
                            "Rebalance {RebalanceId} already succeeded in LND (hash={PaymentHashHex}, feeSats={FeeSats}, ppm={Ppm})",
                            rebalance.Id, rebalance.PaymentHashHex, oldPayment.FeeMsat / 1_000L,
                            (long?)Math.Round((decimal)oldPayment.FeeMsat / rebalance.SatsAmount * 1_000_000m, MidpointRounding.AwayFromZero));
                        ApplyTerminalPayment(rebalance, oldPayment);
                        _rebalanceRepository.Update(rebalance);
                        await _auditService.LogAsync(AuditActionType.RebalanceCompleted, AuditEventType.Success,
                            AuditObjectType.Rebalance, rebalance.Id.ToString(),
                            new
                            {
                                rebalance.FeePaidSats,
                                rebalance.EffectivePpm,
                                ActualAmountSats = rebalance.SatsAmount,
                                rebalance.AttemptNumber,
                            });
                        return rebalance;
                    }
                    else if (oldPayment.Status is Payment.Types.PaymentStatus.InFlight or Payment.Types.PaymentStatus.Initiated)
                    {
                        _logger.LogInformation(
                            "Rebalance {RebalanceId} already in-flight in LND (hash={PaymentHashHex}, status={LndStatus})",
                            rebalance.Id, rebalance.PaymentHashHex, oldPayment.Status);
                        await ScheduleRetryIfEligibleAsync(rebalance);
                        return rebalance;
                    }
                }
            }

            // Per-attempt amount: the full requested amount on attempt 1, shrunk by the backoff
            // ratio on each retry (ratio = 1 ⇒ never shrink). The amount rides on SendPaymentV2,
            // not the invoice, so partial-amount retries reuse the same amountless invoice.
            rebalance.SatsAmount = ComputeAttemptAmountSats(rebalance);

            // One amountless self-invoice per rebalance: created on the first attempt and reused
            // on every retry, so the whole rebalance settles against a single payment hash.
            if (string.IsNullOrEmpty(rebalance.PaymentRequest))
            {
                var memo = $"NG rebalance #{rebalance.Id}";
                var invoiceExpiry = ComputeInvoiceExpirySeconds(rebalance);
                // amountSats: 0 → amountless invoice; the amount is supplied per attempt on SendPaymentV2.
                var invoice = await _lightningService.AddInvoiceAsync(node, 0, memo, invoiceExpiry);
                if (invoice == null || string.IsNullOrEmpty(invoice.PaymentRequest))
                {
                    _logger.LogWarning(
                        "Rebalance {RebalanceId} failed to create self-invoice on node {NodeId}",
                        rebalance.Id, node.Id);
                    rebalance.Status = RebalanceStatus.Failed;
                    _rebalanceRepository.Update(rebalance);
                    await _auditService.LogAsync(AuditActionType.RebalanceCompleted, AuditEventType.Failure,
                        AuditObjectType.Rebalance, rebalance.Id.ToString(),
                        new { Reason = "Failed to create self-invoice" });
                    await ScheduleRetryIfEligibleAsync(rebalance);
                    return rebalance;
                }

                // Persist the invoice + payment hash before sending so MonitorRebalancesJob can
                // resolve the true outcome against LND if this process dies mid-stream.
                rebalance.PaymentRequest = invoice.PaymentRequest;
                rebalance.PaymentHashHex = Convert.ToHexString(invoice.RHash.ToByteArray()).ToLowerInvariant();
                _rebalanceRepository.Update(rebalance);

                _logger.LogInformation(
                    "Rebalance {RebalanceId} amountless self-invoice created (hash={PaymentHashHex}, expirySeconds={ExpirySeconds})",
                    rebalance.Id, rebalance.PaymentHashHex, invoiceExpiry);
            }

            var feeLimitMsat = ComputeFeeLimitMsat(rebalance.SatsAmount, rebalance.MaxFeePct);

            var outgoingChanIds = rebalance.SourceChanIdLnd.HasValue
                ? [rebalance.SourceChanIdLnd.Value]
                : Array.Empty<ulong>();

            rebalance.Status = RebalanceStatus.InFlight;
            _rebalanceRepository.Update(rebalance);

            _logger.LogInformation(
                "Rebalance {RebalanceId} dispatching SendPaymentV2 (amount={AmountSats} sats, feeLimitMsat={FeeLimitMsat}, timeoutSeconds={TimeoutSeconds}, hash={PaymentHashHex})",
                rebalance.Id, rebalance.SatsAmount, feeLimitMsat, rebalance.TimeoutSeconds, rebalance.PaymentHashHex);

            // No pre-probe: LND's own pathfinder (mission control + MPP + per-route retries
            // within the timeout) finds and settles the route. Failures fall through to the
            // attempt-level retry/backoff below, which shrinks the amount per AmountBackoffRatio.
            var payment = await _lightningService.SendPaymentV2Async(
                node,
                rebalance.PaymentRequest!,
                rebalance.SatsAmount,
                feeLimitMsat,
                outgoingChanIds,
                rebalance.TargetPubkey,
                rebalance.TimeoutSeconds,
                ct);

            ApplyTerminalPayment(rebalance, payment);

            _logger.LogInformation(
                "Rebalance {RebalanceId} payment terminal: lndStatus={LndStatus}, failureReason={FailureReason}, dbStatus={DbStatus}, feeSats={FeeSats}, ppm={Ppm}",
                rebalance.Id, payment.Status, payment.FailureReason, rebalance.Status,
                rebalance.FeePaidSats, rebalance.EffectivePpm);

            // Defensive post-hoc ppm guard. Should never fire because feeLimitMsat clamps,
            // but if it does we surface a distinct status for operators / Grafana.
            var maxFeePpmCap = (long)Math.Round(rebalance.MaxFeePct * 10_000d, MidpointRounding.AwayFromZero);
            if (rebalance.Status == RebalanceStatus.Succeeded
                && rebalance.EffectivePpm.HasValue
                && rebalance.EffectivePpm.Value > maxFeePpmCap)
            {
                _logger.LogWarning(
                    "Rebalance {RebalanceId} effective ppm {EffectivePpm} exceeded cap {MaxFeePpmCap}; flipping Succeeded -> ExceededFeeLimit",
                    rebalance.Id, rebalance.EffectivePpm.Value, maxFeePpmCap);
                rebalance.Status = RebalanceStatus.ExceededFeeLimit;
            }

            _rebalanceRepository.Update(rebalance);

            if (rebalance.Status == RebalanceStatus.Succeeded)
            {
                _logger.LogInformation(
                    "Rebalance {RebalanceId} SUCCEEDED on attempt {Attempt} (feeSats={FeeSats}, ppm={Ppm}, actualAmountSats={ActualAmountSats})",
                    rebalance.Id, rebalance.AttemptNumber, rebalance.FeePaidSats, rebalance.EffectivePpm,
                    rebalance.SatsAmount);
                await _auditService.LogAsync(AuditActionType.RebalanceCompleted, AuditEventType.Success,
                    AuditObjectType.Rebalance, rebalance.Id.ToString(),
                    new
                    {
                        rebalance.FeePaidSats,
                        rebalance.EffectivePpm,
                        ActualAmountSats = rebalance.SatsAmount,
                        rebalance.AttemptNumber,
                    });
            }
            else
            {
                _logger.LogInformation(
                    "Rebalance {RebalanceId} attempt {Attempt} ended status={Status} (lndFailureReason={FailureReason})",
                    rebalance.Id, rebalance.AttemptNumber, rebalance.Status, payment.FailureReason);
                await _auditService.LogAsync(AuditActionType.RebalanceCompleted, AuditEventType.Failure,
                    AuditObjectType.Rebalance, rebalance.Id.ToString(),
                    new
                    {
                        rebalance.Status,
                        FailureReason = payment.FailureReason.ToString(),
                    });
                await ScheduleRetryIfEligibleAsync(rebalance);
            }

            return rebalance;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rebalance {Id} attempt {Attempt} failed unexpectedly",
                rebalance.Id, rebalance.AttemptNumber);
            rebalance.Status = RebalanceStatus.Failed;
            _rebalanceRepository.Update(rebalance);
            await _auditService.LogAsync(AuditActionType.RebalanceCompleted, AuditEventType.Failure,
                AuditObjectType.Rebalance, rebalance.Id.ToString(),
                new { ExceptionType = ex.GetType().Name, Message = ex.Message });
            await ScheduleRetryIfEligibleAsync(rebalance);
            return rebalance;
        }
    }

    /// <summary>
    /// Resolves the max fee percentage to use. Uses the caller-supplied value when positive,
    /// otherwise the supplied default. Convention: 0.05 means 0.05%.
    /// </summary>
    private static double ResolveMaxFeePct(double? userSupplied, decimal defaultPct)
        => userSupplied is { } v && v > 0 ? v : (double)defaultPct;

    /// <summary>
    /// fee_msat = sats × (pct / 100) × 1000 = sats × pct × 10. Decimal math, banker-safe rounding.
    /// </summary>
    private static long ComputeFeeLimitMsat(long satsAmount, double maxFeePct)
        => (long)Math.Round(satsAmount * (decimal)maxFeePct * 10m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Amount to pay on the current attempt: RequestedAmountSats × ratio^(AttemptNumber-1),
    /// clamped to [min(REBALANCE_MIN_AMOUNT_SATS, requested), requested]. Attempt 1 always uses
    /// the full requested amount (ratio^0 = 1); each retry shrinks by the backoff ratio
    /// (ratio = 1 ⇒ no shrink). The same amountless invoice carries whatever amount this returns.
    /// </summary>
    private static long ComputeAttemptAmountSats(Rebalance rebalance)
    {
        var ratio = rebalance.AmountBackoffRatio ?? Constants.REBALANCE_AMOUNT_BACKOFF_RATIO;
        var scaled = (long)Math.Round(
            rebalance.RequestedAmountSats * Math.Pow(ratio, rebalance.AttemptNumber - 1),
            MidpointRounding.AwayFromZero);
        var floor = Math.Min(Constants.REBALANCE_MIN_AMOUNT_SATS, rebalance.RequestedAmountSats);
        return Math.Clamp(scaled, floor, rebalance.RequestedAmountSats);
    }

    /// <summary>
    /// Sizes the self-invoice's expiry to outlive the full retry window — per-attempt
    /// payment timeout + every backoff gap between retries + a safety buffer. The probe is
    /// unbounded and an exponential backoff (60s → 120s → 240s) can push later attempts
    /// well past a short expiry; matching the retry budget guarantees the invoice is still
    /// honoured by LND when SendPaymentV2 finally fires.
    /// </summary>
    private static long ComputeInvoiceExpirySeconds(Rebalance rebalance)
    {
        var maxAttempts = Math.Max(1, rebalance.MaxAttempts ?? Constants.REBALANCE_MAX_ATTEMPTS);
        var initialDelay = Constants.REBALANCE_INITIAL_RETRY_DELAY_SECONDS;
        var multiplier = Constants.REBALANCE_RETRY_BACKOFF_MULTIPLIER;

        // Mirrors ScheduleRetryIfEligibleAsync: delay between attempt N and N+1 is
        // initialDelay * multiplier^(N-1). Sum across all retries we might schedule.
        double totalBackoffSeconds = 0;
        for (var i = 0; i < maxAttempts - 1; i++)
        {
            totalBackoffSeconds += initialDelay * Math.Pow(multiplier, i);
        }

        return rebalance.TimeoutSeconds + (long)totalBackoffSeconds + 60;
    }


    internal static void ApplyTerminalPayment(Rebalance rebalance, Payment payment)
    {
        switch (payment.Status)
        {
            case Payment.Types.PaymentStatus.Succeeded:
                rebalance.Status = RebalanceStatus.Succeeded;
                rebalance.FeePaidMsat = payment.FeeMsat;
                rebalance.FeePaidSats = payment.FeeMsat / 1_000L;
                rebalance.PreimageHex = payment.PaymentPreimage;
                break;
            case Payment.Types.PaymentStatus.Failed:
                rebalance.Status = payment.FailureReason switch
                {
                    PaymentFailureReason.FailureReasonNoRoute => RebalanceStatus.NoRoute,
                    PaymentFailureReason.FailureReasonTimeout => RebalanceStatus.Timeout,
                    PaymentFailureReason.FailureReasonInsufficientBalance => RebalanceStatus.InsufficientBalance,
                    _ => RebalanceStatus.Failed,
                };
                break;
            default:
                rebalance.Status = RebalanceStatus.Failed;
                break;
        }
    }

    private async Task ScheduleRetryIfEligibleAsync(Rebalance rebalance)
    {
        if (rebalance.Status is RebalanceStatus.Succeeded
            or RebalanceStatus.InsufficientBalance
            or RebalanceStatus.ExceededFeeLimit)
        {
            _logger.LogInformation(
                "Rebalance {RebalanceId} not eligible for retry due to terminal status {Status}",
                rebalance.Id, rebalance.Status);
            return;
        }

        var maxAttempts = rebalance.MaxAttempts ?? Constants.REBALANCE_MAX_ATTEMPTS;
        if (rebalance.AttemptNumber >= maxAttempts)
        {
            _logger.LogInformation(
                "Rebalance {RebalanceId} exhausted retry budget at attempt {Attempt}/{MaxAttempts} (status={Status})",
                rebalance.Id, rebalance.AttemptNumber, maxAttempts, rebalance.Status);
            return;
        }

        var nextAttempt = rebalance.AttemptNumber + 1;
        var retryDefaultPct = rebalance.RetryMaxFeePct.HasValue
            ? (decimal)rebalance.RetryMaxFeePct.Value
            : Constants.REBALANCE_DEFAULT_RETRY_MAX_FEE_PCT;
        var retryPct = ResolveMaxFeePct(null, retryDefaultPct);

        rebalance.AttemptNumber = nextAttempt;
        rebalance.MaxFeePct = Math.Max(rebalance.MaxFeePct, retryPct);
        rebalance.Status = RebalanceStatus.Pending;
        _rebalanceRepository.Update(rebalance);

        var delaySeconds = Constants.REBALANCE_INITIAL_RETRY_DELAY_SECONDS
            * Math.Pow(Constants.REBALANCE_RETRY_BACKOFF_MULTIPLIER, nextAttempt - 2);
        var fireAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);

        var scheduler = await _schedulerFactory.GetScheduler();
        var jobData = new JobDataMap();
        jobData.Put("rebalanceId", rebalance.Id);

        var job = JobBuilder.Create<RebalanceJob>()
            .WithIdentity($"{nameof(RebalanceJob)}-{rebalance.Id}-a{nextAttempt}")
            .UsingJobData(jobData)
            .RequestRecovery()
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{nameof(RebalanceJob)}Trigger-{rebalance.Id}-a{nextAttempt}")
            .StartAt(fireAt)
            .Build();

        await scheduler.ScheduleJob(job, trigger);

        _logger.LogInformation(
            "Rebalance {RebalanceId} retry scheduled: attempt {NextAttempt}/{MaxAttempts} in {DelaySeconds}s (newMaxFeePct={NewMaxFeePct}, fireAt={FireAt:O})",
            rebalance.Id, nextAttempt, maxAttempts, delaySeconds, rebalance.MaxFeePct, fireAt);

        await _auditService.LogAsync(AuditActionType.RebalanceRetryScheduled, AuditEventType.Attempt,
            AuditObjectType.Rebalance, rebalance.Id.ToString(),
            new
            {
                NewAttemptNumber = nextAttempt,
                NewMaxFeePct = rebalance.MaxFeePct,
                FireAt = fireAt,
            });
    }
}
