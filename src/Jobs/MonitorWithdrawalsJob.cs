using FundsManager.Data.Models;
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

                    var getTxResult = await nbxplorerclient.GetTransactionAsync(uint256.Parse(walletWithdrawalRequest.TxId));

                    var confirmationBlocks =
                        int.Parse(Environment.GetEnvironmentVariable("TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS") ??
                                  throw new InvalidOperationException());

                    if (getTxResult.Confirmations >= confirmationBlocks)
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