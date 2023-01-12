using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using FundsManager.Helpers;
using FundsManager.Data.Models;
using Quartz;
using Quartz.Impl.Triggers;

namespace FundsManager.Jobs;

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


    public PerformWithdrawalJob(ILogger<PerformWithdrawalJob> logger, IBitcoinService bitcoinService, IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository)
    {
        _logger = logger;
        _bitcoinService = bitcoinService;
        _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(PerformWithdrawalJob));
        var data = context.JobDetail.JobDataMap;
        var withdrawalRequestId = data.GetInt("withdrawalRequestId");
        try
        {
            await RetriableJob.Execute(context, async () =>
            {
                var withdrawalRequest = await _walletWithdrawalRequestRepository.GetById(withdrawalRequestId);
                await _bitcoinService.PerformWithdrawal(withdrawalRequest);
            });
        }
        catch (Exception e)
        {
            await RetriableJob.OnFail(context, async () =>
            {
                var request = await _walletWithdrawalRequestRepository.GetById(withdrawalRequestId);
                request.Status = WalletWithdrawalRequestStatus.Failed;
                _walletWithdrawalRequestRepository.Update(request);
            });

            _logger.LogError(e, "Error on {JobName}", nameof(PerformWithdrawalJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(PerformWithdrawalJob));
    }
}