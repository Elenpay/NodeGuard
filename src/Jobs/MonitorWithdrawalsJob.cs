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
/// Monitors that withdrawals on-chain txs are confirmed
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

        foreach (var walletWithdrawalRequest in withdrawalsPending)
        {
            if (!string.IsNullOrEmpty(walletWithdrawalRequest.TxId))
            {
                try
                {
                    //Let's check if the minimum amount of confirmations are established

                    var getTxResult = await _nbXplorerService.GetTransactionAsync(uint256.Parse(walletWithdrawalRequest.TxId), default);

                    if (getTxResult != null && getTxResult.Confirmations >= Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS)
                    {
                        walletWithdrawalRequest.Status = WalletWithdrawalRequestStatus.OnChainConfirmed;

                        var updateResult = _walletWithdrawalRequestRepository.Update(walletWithdrawalRequest);

                        if (!updateResult.Item1)
                        {
                            _logger.LogError(
                                "Error while updating wallet withdrawal: {RequestId}, status: {RequestStatus}",
                                walletWithdrawalRequest.Id,
                                walletWithdrawalRequest.Status);
                        }
                        else
                        {
                            _logger.LogInformation("Updating wallet withdrawal: {RequestId} to status: {RequestStatus}",
                                walletWithdrawalRequest.Id, walletWithdrawalRequest.Status);
                        }
                    }
                    else if (walletWithdrawalRequest.BumpingWalletWithdrawalRequestId != null && (getTxResult?.Confirmations ?? 0) == 0)
                    {
                        // An RBF replacement can lose the race, even vanish: the transaction it replaced may be mined instead.
                        await _withdrawalRequestService.SettleMinedOriginalAsync(walletWithdrawalRequest);
                    }
                }
                catch (Exception e)
                {
                    // One request must not stop the rest of the batch.
                    _logger.LogError(e, "Error while monitoring TxId: {TxId}", walletWithdrawalRequest.TxId);
                }
            }
        }

        _logger.LogInformation($"Job {nameof(MonitorWithdrawalsJob)} ended");
    }
}