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
/// Job for performing withdrawal requests from the btc wallet, with automatic retry
/// </summary>
/// <returns></returns>
[DisallowConcurrentExecution]
public class PerformWithdrawalJob : IJob
{
    private readonly ILogger<PerformWithdrawalJob> _logger;
    private readonly IBitcoinService _bitcoinService;
    private readonly IWalletWithdrawalRequestRepository _walletWithdrawalRequestRepository;
    private readonly IWithdrawalRequestService _withdrawalRequestService;


    public PerformWithdrawalJob(ILogger<PerformWithdrawalJob> logger, IBitcoinService bitcoinService,
        IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository, IWithdrawalRequestService withdrawalRequestService)
    {
        _logger = logger;
        _bitcoinService = bitcoinService;
        _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
        _withdrawalRequestService = withdrawalRequestService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(PerformWithdrawalJob));
        var data = context.JobDetail.JobDataMap;
        var withdrawalRequestId = data.GetInt("withdrawalRequestId");
        try
        {
            var withdrawalRequest = await _walletWithdrawalRequestRepository.GetById(withdrawalRequestId);
            await _bitcoinService.PerformWithdrawal(withdrawalRequest!);
        }
        catch (Exception e)
        {
            var request = await _walletWithdrawalRequestRepository.GetById(withdrawalRequestId);
            if (request?.Status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
            {
                // Already broadcast (the txid is persisted before the destination is tracked): nothing to undo.
                _logger.LogWarning("Withdrawal request {RequestId} failed after its transaction {TxId} was broadcast, leaving it pending confirmation",
                    request.Id, request.TxId);
            }
            else if (request != null)
            {
                // Only fail the request once what it replaced is pending again; otherwise nothing would monitor either.
                if (await _withdrawalRequestService.RevertOriginalAsync(request))
                {
                    request.Status = WalletWithdrawalRequestStatus.Failed;
                    var (updated, _) = _walletWithdrawalRequestRepository.Update(request);
                    if (!updated)
                    {
                        _logger.LogError("Failed to update withdrawal request {RequestId} status to failed", request.Id);
                    }
                }
                else
                {
                    _logger.LogError("Leaving withdrawal request {RequestId} as {Status}: the request it replaced could not be reverted",
                        request.Id, request.Status);
                }
            }

            _logger.LogError(e, "Error on {JobName}", nameof(PerformWithdrawalJob));
        }

        _logger.LogInformation("{JobName} ended", nameof(PerformWithdrawalJob));
    }
}
