using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Data.Models;
using FundsManager.Services;
using Quartz;

namespace FundsManager.Jobs;

/// <summary>
/// Job for closing channel requests to the managed nodes, with automatic retry
/// </summary>
/// <returns></returns>
[DisallowConcurrentExecution]
public class ChannelCloseJob : IJob
{
    private readonly ILogger<ChannelCloseJob> _logger;
    private readonly ILightningService _lightningService;


    public ChannelCloseJob(ILogger<ChannelCloseJob> logger, ILightningService lightningService)
    {
        _logger = logger;
        _lightningService = lightningService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {}... ", nameof(ChannelCloseJob));
        try
        {
            var token = context.CancellationToken;
            token.ThrowIfCancellationRequested();

            var data = context.JobDetail.JobDataMap;
            var closeRequest = data.Get("closeRequest") as ChannelOperationRequest;
            var forceClose = data.GetBoolean("forceClose");

            await _lightningService.CloseChannel(closeRequest, forceClose);

            var schedule = context.Scheduler.DeleteJob(context.JobDetail.Key, token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {}", nameof(ChannelCloseJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{} ended", nameof(ChannelCloseJob));
    }
}