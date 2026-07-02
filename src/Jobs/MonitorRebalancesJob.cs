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
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Reconciles local Rebalance rows against LND's TrackPaymentV2 truth. Closes the gap where
/// a Rebalance is left InFlight after a crash or stuck, or wrongly marked Failed when the stream was
/// cancelled but LND actually settled the payment.
/// </summary>
[DisallowConcurrentExecution]
public class MonitorRebalancesJob : IJob
{
    private readonly ILogger<MonitorRebalancesJob> _logger;
    private readonly IRebalanceRepository _rebalanceRepository;
    private readonly ILightningService _lightningService;
    private readonly IAuditService _auditService;

    public MonitorRebalancesJob(
        ILogger<MonitorRebalancesJob> logger,
        IRebalanceRepository rebalanceRepository,
        ILightningService lightningService,
        IAuditService auditService)
    {
        _logger = logger;
        _rebalanceRepository = rebalanceRepository;
        _lightningService = lightningService;
        _auditService = auditService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}...", nameof(MonitorRebalancesJob));
        try
        {
            var window = TimeSpan.FromHours(Constants.REBALANCE_RECONCILE_TERMINAL_WINDOW_HOURS);
            var rebalances = await _rebalanceRepository.GetReconcilable(window);

            _logger.LogInformation(
                "{JobName} found {Count} reconcilable rebalance(s) within window {WindowHours}h",
                nameof(MonitorRebalancesJob), rebalances.Count, window.TotalHours);

            foreach (var rebalance in rebalances)
            {
                try
                {
                    await ReconcileAsync(rebalance, context.CancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unexpected error reconciling rebalance {RebalanceId}. Monitoring will continue for other rebalances",
                        rebalance.Id);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(MonitorRebalancesJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(MonitorRebalancesJob));
    }

    private async Task ReconcileAsync(Rebalance rebalance, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rebalance.PaymentHashHex))
        {
            _logger.LogDebug(
                "Rebalance {RebalanceId} (status={Status}) has no PaymentHashHex; nothing to reconcile against LND",
                rebalance.Id, rebalance.Status);
            return;
        }

        if (rebalance.Node == null)
        {
            _logger.LogWarning("Rebalance {RebalanceId} has no node loaded; skipping reconciliation", rebalance.Id);
            return;
        }

        byte[] paymentHash;
        try
        {
            paymentHash = Convert.FromHexString(rebalance.PaymentHashHex);
        }
        catch (FormatException)
        {
            _logger.LogWarning("Rebalance {RebalanceId} has malformed PaymentHashHex {PaymentHashHex}; skipping reconciliation",
                rebalance.Id, rebalance.PaymentHashHex);
            return;
        }

        _logger.LogInformation(
            "Reconciling rebalance {RebalanceId} (node={NodeId}, status={Status}, hash={PaymentHashHex}, attempt={Attempt}) against LND",
            rebalance.Id, rebalance.NodeId, rebalance.Status, rebalance.PaymentHashHex, rebalance.AttemptNumber);

        var payment = await _lightningService.TrackPaymentV2Async(rebalance.Node, paymentHash, ct);

        if (payment == null)
        {
            // LND returned NotFound (or the call errored). The right action depends on which
            // non-terminal state the row is in:
            //
            // - Pending: the local ExecuteAsync persisted PaymentHashHex right after AddInvoice,
            //   but SendPaymentV2 hasn't fired yet — so "no record in LND" is the EXPECTED state
            //   during this window. Flipping the row to Failed here would kill an in-progress
            //   attempt. Leave it alone; the local execution is the source of truth until it
            //   dispatches SendPaymentV2.
            //
            // - InFlight: SendpaymentV2 has been invoked, so LND should know.
            //   If it doesn't, the payment never reached LND (process crashed mid-dispatch,
            //   or LND lost it). Flip to Failed so the row doesn't stay InFlight forever.
            //
            // - Terminal statuses: leave alone — a transient track error must not reopen them.
            if (rebalance.Status == RebalanceStatus.InFlight)
            {
                var oldStatus = rebalance.Status;
                rebalance.Status = RebalanceStatus.Failed;
                _rebalanceRepository.Update(rebalance);
                _logger.LogWarning(
                    "Rebalance {RebalanceId} has no record in LND for hash {PaymentHashHex}; flipping {OldStatus} -> Failed",
                    rebalance.Id, rebalance.PaymentHashHex, oldStatus);
                await _auditService.LogSystemAsync(
                    AuditActionType.RebalanceCompleted,
                    AuditEventType.Failure,
                    AuditObjectType.Rebalance,
                    rebalance.Id.ToString(),
                    new
                    {
                        Reason = "MonitorReconciliation",
                        Detail = "LND has no record of this payment hash",
                        OldStatus = oldStatus.ToString(),
                        NewStatus = rebalance.Status.ToString(),
                    });
            }
            else if (rebalance.Status is RebalanceStatus.Pending)
            {
                var window = TimeSpan.FromHours(Constants.REBALANCE_RECONCILE_TERMINAL_WINDOW_HOURS);
                if (rebalance.UpdateDatetime < DateTimeOffset.UtcNow - window)
                {
                    _logger.LogWarning(
                        "Rebalance {RebalanceId} (status={Status}, hash={PaymentHashHex}) has no record in LND and is older than {WindowHours}h; flipping to Failed",
                        rebalance.Id, rebalance.Status, rebalance.PaymentHashHex, window.TotalHours);
                    var oldStatus = rebalance.Status;
                    rebalance.Status = RebalanceStatus.Failed;
                    _rebalanceRepository.Update(rebalance);
                    await _auditService.LogSystemAsync(
                        AuditActionType.RebalanceCompleted,
                        AuditEventType.Failure,
                        AuditObjectType.Rebalance,
                        rebalance.Id.ToString(),
                        new
                        {
                            Reason = "MonitorReconciliation",
                            Detail = "LND has no record of this payment hash and the row is older than the reconciliation window",
                            OldStatus = oldStatus.ToString(),
                            NewStatus = rebalance.Status.ToString(),
                        });
                }
                else
                {
                    _logger.LogDebug(
                        "Rebalance {RebalanceId} not yet visible in LND (status={Status}, hash={PaymentHashHex}); local execution still owns it, leaving as-is",
                        rebalance.Id, rebalance.Status, rebalance.PaymentHashHex);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Rebalance {RebalanceId} TrackPaymentV2 returned no payment; row already terminal ({Status}), leaving as-is",
                    rebalance.Id, rebalance.Status);
            }

            return;
        }

        if (rebalance.Status is RebalanceStatus.Pending)
        {
            _logger.LogDebug(
                "Rebalance {RebalanceId} (status={Status}) has a payment record in LND (status={LndStatus}, hash={PaymentHashHex}); local execution still owns it, leaving as-is",
                rebalance.Id, rebalance.Status, payment.Status, rebalance.PaymentHashHex);
            return;
        }

        if (IsNonTerminalLndStatus(payment.Status))
        {
            // LND still in flight — emitted at info so an operator can spot rebalances stuck
            // in flight across monitor sweeps (same RebalanceId appearing tick after tick).
            _logger.LogInformation(
                "Rebalance {RebalanceId} still {LndStatus} in LND (db status={DbStatus}, attempt={Attempt}, hash={PaymentHashHex}); skipping update",
                rebalance.Id, payment.Status, rebalance.Status, rebalance.AttemptNumber, rebalance.PaymentHashHex);
            return;
        }

        var previousStatus = rebalance.Status;
        RebalanceService.ApplyTerminalPayment(rebalance, payment);

        if (rebalance.Status == previousStatus)
        {
            _logger.LogInformation(
                "Rebalance {RebalanceId} terminal status {Status} matches LND ({LndStatus}); no update needed",
                rebalance.Id, rebalance.Status, payment.Status);
            return;
        }

        _rebalanceRepository.Update(rebalance);

        var eventType = rebalance.Status == RebalanceStatus.Succeeded
            ? AuditEventType.Success
            : AuditEventType.Failure;

        _logger.LogInformation(
            "Rebalance {RebalanceId} reconciled: {OldStatus} -> {NewStatus} (LND status={LndStatus}, failureReason={LndFailureReason}, feeSats={FeeSats}, ppm={Ppm})",
            rebalance.Id, previousStatus, rebalance.Status, payment.Status, payment.FailureReason,
            rebalance.FeePaidSats, rebalance.EffectivePpm);

        await _auditService.LogSystemAsync(
            AuditActionType.RebalanceCompleted,
            eventType,
            AuditObjectType.Rebalance,
            rebalance.Id.ToString(),
            new
            {
                Reason = "MonitorReconciliation",
                OldStatus = previousStatus.ToString(),
                NewStatus = rebalance.Status.ToString(),
                LndPaymentStatus = payment.Status.ToString(),
                LndFailureReason = payment.FailureReason.ToString(),
                rebalance.FeePaidSats,
                rebalance.EffectivePpm,
            });
    }

    private static bool IsNonTerminalLndStatus(Payment.Types.PaymentStatus status) =>
        status is Payment.Types.PaymentStatus.Initiated
            or Payment.Types.PaymentStatus.InFlight;
}
