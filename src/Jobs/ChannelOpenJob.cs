using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Data.Models;
using FundsManager.Services;
using Quartz;

namespace FundsManager.Jobs;

/// <summary>
/// Job for openning channel requests to the managed nodes, with automatic retry
/// </summary>
/// <returns></returns>
[DisallowConcurrentExecution]
public class ChannelOpenJob : IJob
{
    private readonly ILogger<ChannelOpenJob> _logger;
    private readonly ILightningService _lightningService;


    public ChannelOpenJob(ILogger<ChannelOpenJob> logger, ILightningService lightningService)
    {
        _logger = logger;
        _lightningService = lightningService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(ChannelOpenJob));
        try
        {
            var token = context.CancellationToken;
            token.ThrowIfCancellationRequested();

            var data = context.JobDetail.JobDataMap;
            var openRequest = data.Get("openRequest") as ChannelOperationRequest;

            await _lightningService.OpenChannel(openRequest);

            var schedule = context.Scheduler.DeleteJob(context.JobDetail.Key, token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(ChannelOpenJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelOpenJob));
    }
}