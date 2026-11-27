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

using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using NodeGuard.Helpers;
using NodeGuard.Data.Models;
using Quartz;
using Quartz.Impl.Triggers;

namespace NodeGuard.Jobs;

/// <summary>
/// Job for performing withdrawal requests from the btc wallet. Before signing, the integrators intercepting the
/// wallet's withdrawals (SubscribeWalletEvents with intercept_withdrawals) are asked; a block rejects the request.
/// </summary>
/// <returns></returns>
[DisallowConcurrentExecution]
public class PerformWithdrawalJob : IJob
{
    private readonly ILogger<PerformWithdrawalJob> _logger;
    private readonly IBitcoinService _bitcoinService;
    private readonly IWalletWithdrawalRequestRepository _walletWithdrawalRequestRepository;
    private readonly IWithdrawalInterceptorGate _interceptorGate;
    private readonly IWithdrawalRequestService _withdrawalRequestService;
    private readonly IAuditService _auditService;

    public PerformWithdrawalJob(ILogger<PerformWithdrawalJob> logger,
        IBitcoinService bitcoinService,
        IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository,
        IWithdrawalInterceptorGate interceptorGate,
        IWithdrawalRequestService withdrawalRequestService,
        IAuditService auditService)
    {
        _logger = logger;
        _bitcoinService = bitcoinService;
        _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
        _interceptorGate = interceptorGate;
        _withdrawalRequestService = withdrawalRequestService;
        _auditService = auditService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(PerformWithdrawalJob));
        var data = context.JobDetail.JobDataMap;
        var withdrawalRequestId = data.GetInt("withdrawalRequestId");
        try
        {
            var withdrawalRequest = await _walletWithdrawalRequestRepository.GetById(withdrawalRequestId);
            if (withdrawalRequest != null)
            {
                var outcome = await AskIntegratorsAsync(withdrawalRequest, context.CancellationToken);
                switch (outcome.Kind)
                {
                    case WithdrawalInterceptOutcomeKind.Allowed:
                        await _auditService.LogSystemAsync(AuditActionType.WithdrawalAllowedByIntegrator,
                            AuditEventType.Success, AuditObjectType.WalletWithdrawalRequest,
                            withdrawalRequest.Id.ToString(), AuditDetails(withdrawalRequest, outcome));
                        break;
                    case WithdrawalInterceptOutcomeKind.Blocked:
                        await RejectBlockedAsync(withdrawalRequest, outcome);
                        _logger.LogInformation("{JobName} ended: withdrawal request {RequestId} blocked by an integrator",
                            nameof(PerformWithdrawalJob), withdrawalRequestId);
                        return;
                }
            }

            await _bitcoinService.PerformWithdrawal(withdrawalRequest!);
        }
        catch (Exception e)
        {
            var request = await _walletWithdrawalRequestRepository.GetById(withdrawalRequestId);
            if (request == null)
            {
                _logger.LogError(e, "Error on {JobName}, and withdrawal request {RequestId} could not be found",
                    nameof(PerformWithdrawalJob), withdrawalRequestId);
                return;
            }

            // A failed RBF replacement puts the request it was replacing back to pending confirmation
            await _withdrawalRequestService.RevertOriginalAsync(request);

            request.Status = WalletWithdrawalRequestStatus.Failed;
            var (updated, _) = _walletWithdrawalRequestRepository.Update(request);
            if (!updated)
            {
                _logger.LogError("Failed to update withdrawal request status to failed");
            }

            _logger.LogError(e, "Error on {JobName}", nameof(PerformWithdrawalJob));
        }

        _logger.LogInformation("{JobName} ended", nameof(PerformWithdrawalJob));
    }

    /// <summary>
    /// Puts the request to the integrators intercepting its wallet and returns their outcome, without acting on it.
    /// Skipped with nobody intercepting, or when the request is not about to be signed.
    /// </summary>
    private Task<WithdrawalInterceptOutcome> AskIntegratorsAsync(WalletWithdrawalRequest request,
        CancellationToken cancellationToken)
    {
        // Only a request about to be signed is put to the integrators; anything else fails in PerformWithdrawal as before
        if (request.Status is not (WalletWithdrawalRequestStatus.PSBTSignaturesPending
            or WalletWithdrawalRequestStatus.FinalizingPSBT))
        {
            return Task.FromResult(WithdrawalInterceptOutcome.Skipped);
        }

        return _interceptorGate.RequestDecisionAsync(request, cancellationToken);
    }

    /// <summary>
    /// Rejects a request an integrator blocked, and reverts the request it was replacing, for an RBF replacement
    /// </summary>
    private async Task RejectBlockedAsync(WalletWithdrawalRequest request, WithdrawalInterceptOutcome outcome)
    {
        _logger.LogWarning("Withdrawal request {RequestId} blocked by integrator {Integrator}: {Reason}",
            request.Id, outcome.DecidedBy, outcome.Reason);

        request.Status = WalletWithdrawalRequestStatus.Rejected;
        request.RejectCancelDescription = $"Blocked by integrator '{outcome.DecidedBy}': {outcome.Reason}";
        var (updated, error) = _walletWithdrawalRequestRepository.Update(request);
        if (!updated)
        {
            // Never sign a blocked withdrawal: let the job's failure path mark it failed instead
            throw new InvalidOperationException(
                $"Withdrawal request {request.Id} was blocked by an integrator but could not be rejected: {error}");
        }

        await _withdrawalRequestService.RevertOriginalAsync(request);

        await _auditService.LogSystemAsync(AuditActionType.WithdrawalBlockedByIntegrator, AuditEventType.Success,
            AuditObjectType.WalletWithdrawalRequest, request.Id.ToString(), AuditDetails(request, outcome));
    }

    private static object AuditDetails(WalletWithdrawalRequest request, WithdrawalInterceptOutcome outcome) => new
    {
        RequestId = request.Id,
        request.WalletId,
        outcome.DecisionId,
        outcome.Interceptors,
        outcome.DecidedBy,
        outcome.Reason,
        request.BumpingWalletWithdrawalRequestId,
        request.IsRbfCancellation
    };
}
