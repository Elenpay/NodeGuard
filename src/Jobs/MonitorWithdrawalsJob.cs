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

﻿using FundsManager.Data.Models;
using FundsManager.Data.Repositories;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using Quartz;

namespace FundsManager.Jobs;

/// <summary>
/// Monitors that withdrawals on-chain txs are confirmed
/// </summary>
[DisallowConcurrentExecution]
public class MonitorWithdrawalsJob : IJob
{
    private readonly ILogger<MonitorWithdrawalsJob> _logger;
    private readonly IWalletWithdrawalRequestRepository _walletWithdrawalRequestRepository;

    public MonitorWithdrawalsJob(ILogger<MonitorWithdrawalsJob> logger,
        IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository)
    {
        _logger = logger;
        _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
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
                    var (network, nbxplorerclient) = LightningHelper.GenerateNetwork();

                    var getTxResult = await nbxplorerclient.Execute(x => x.GetTransactionAsync(uint256.Parse(walletWithdrawalRequest.TxId), default));

                    if (getTxResult.Confirmations >= Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS)
                    {
                        walletWithdrawalRequest.Status = WalletWithdrawalRequestStatus.OnChainConfirmed;

                        var updateResult = _walletWithdrawalRequestRepository.Update(walletWithdrawalRequest);

                        if (!updateResult.Item1)
                        {
                            _logger.LogError("Error while updating wallet withdrawal: {RequestId}, status: {RequestStatus}",
                                walletWithdrawalRequest.Id,
                                walletWithdrawalRequest.Status);
                        }
                        else
                        {
                            _logger.LogInformation("Updating wallet withdrawal: {RequestId} to status: {RequestStatus}",
                                walletWithdrawalRequest.Id, walletWithdrawalRequest.Status);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error while monitoring TxId: {TxId}", walletWithdrawalRequest.TxId);

                    throw new JobExecutionException(e, false);
                }
            }
        }

        _logger.LogInformation($"Job {nameof(MonitorWithdrawalsJob)} ended");
    }
}