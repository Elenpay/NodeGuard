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
using NodeGuard.Services;
using NBitcoin;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Settles withdrawals whose transaction reached the confirmation depth, then gives every request linked by RBF its
/// final status from what its chain holds (a replaced request that was mined instead, a replacement that confirmed).
/// Each row is written on its own, so a failing one is retried on the next run without touching the rest.
/// </summary>
[DisallowConcurrentExecution]
public class MonitorWithdrawalsJob : IJob
{
    private readonly ILogger<MonitorWithdrawalsJob> _logger;
    private readonly IWalletWithdrawalRequestRepository _walletWithdrawalRequestRepository;
    private readonly INBXplorerService _nbXplorerService;
    private readonly IWithdrawalRequestService _withdrawalRequestService;

    public MonitorWithdrawalsJob(ILogger<MonitorWithdrawalsJob> logger,
        IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository,
        INBXplorerService nbXplorerService,
        IWithdrawalRequestService withdrawalRequestService
    )
    {
        _logger = logger;
        _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
        _nbXplorerService = nbXplorerService;
        _withdrawalRequestService = withdrawalRequestService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation($"Job {nameof(MonitorWithdrawalsJob)} started");
        var withdrawalsPending = await _walletWithdrawalRequestRepository.GetOnChainPendingWithdrawals();

        // An unset TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS is 0. Replaced requests are polled too, and one whose
        // transaction was evicted sits at 0 confirmations: that is not mined.
        var minimumConfirmations = Math.Max(1, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS);

        foreach (var walletWithdrawalRequest in withdrawalsPending)
        {
            if (string.IsNullOrEmpty(walletWithdrawalRequest.TxId)) continue;

            try
            {
                var getTxResult = await _nbXplorerService.GetTransactionAsync(uint256.Parse(walletWithdrawalRequest.TxId), default);
                if (getTxResult == null)
                {
                    _logger.LogWarning("Transaction {TxId} of withdrawal request {RequestId} is unknown to NBXplorer",
                        walletWithdrawalRequest.TxId, walletWithdrawalRequest.Id);
                    continue;
                }

                if (getTxResult.Confirmations < minimumConfirmations) continue;

                walletWithdrawalRequest.Status = WalletWithdrawalRequestStatus.OnChainConfirmed;
                var (updated, _) = _walletWithdrawalRequestRepository.Update(walletWithdrawalRequest);
                if (updated)
                {
                    _logger.LogInformation("Updating wallet withdrawal: {RequestId} to status: {RequestStatus}",
                        walletWithdrawalRequest.Id, walletWithdrawalRequest.Status);
                }
                else
                {
                    _logger.LogError("Error while updating wallet withdrawal: {RequestId}, status: {RequestStatus}",
                        walletWithdrawalRequest.Id, walletWithdrawalRequest.Status);
                }
            }
            catch (Exception e)
            {
                // One request must not stop the rest of the batch.
                _logger.LogError(e, "Error while monitoring TxId: {TxId}", walletWithdrawalRequest.TxId);
            }
        }

        try
        {
            // After settling, so a request confirmed in this run already drives its RBF chain in this run.
            await _withdrawalRequestService.ReconcileReplacementChainsAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while reconciling RBF replacement chains");
        }

        _logger.LogInformation($"Job {nameof(MonitorWithdrawalsJob)} ended");
    }
}