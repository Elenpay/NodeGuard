using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Data.Models;
using FundsManager.Services;
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


    public PerformWithdrawalJob(ILogger<PerformWithdrawalJob> logger, IBitcoinService bitcoinService)
    {
        _logger = logger;
        _bitcoinService = bitcoinService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {}... ", nameof(PerformWithdrawalJob));
        try
        {
            var token = context.CancellationToken;
            token.ThrowIfCancellationRequested();

            var data = context.JobDetail.JobDataMap;
            var withdrawalRequest = data.Get("withdrawalRequest") as WalletWithdrawalRequest;
            await _bitcoinService.PerformWithdrawal(withdrawalRequest);

            var schedule = context.Scheduler.DeleteJob(context.JobDetail.Key, token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {}", nameof(PerformWithdrawalJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{} ended", nameof(PerformWithdrawalJob));
    }
}