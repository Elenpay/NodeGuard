using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using FundsManager.Helpers;
using Quartz;

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
        try
        {
            await RetriableJob.Execute(context, async () =>
            {
                var data = context.JobDetail.JobDataMap;
                var withdrawalRequestId = data.GetInt("withdrawalRequestId");
                var withdrawalRequest = await _walletWithdrawalRequestRepository.GetById(withdrawalRequestId);
                await _bitcoinService.PerformWithdrawal(withdrawalRequest);
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(PerformWithdrawalJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(PerformWithdrawalJob));
    }
}