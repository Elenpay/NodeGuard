using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using FundsManager.Helpers;
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
    private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;


    public ChannelOpenJob(ILogger<ChannelOpenJob> logger, ILightningService lightningService, IChannelOperationRequestRepository channelOperationRequestRepository)
    {
        _logger = logger;
        _lightningService = lightningService;
        _channelOperationRequestRepository = channelOperationRequestRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(ChannelOpenJob));
        try {
            var token = context.CancellationToken;
            token.ThrowIfCancellationRequested();
            
            await JobRescheduler.SetNextInterval(context);

            var data = context.JobDetail.JobDataMap;
            var openRequestId = data.GetInt("openRequestId");
            var openRequest = await _channelOperationRequestRepository.GetById(openRequestId);
            await _lightningService.OpenChannel(openRequest);

            await context.Scheduler.DeleteJob(context.JobDetail.Key, token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(ChannelOpenJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelOpenJob));
    }
}