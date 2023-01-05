using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using FundsManager.Helpers;
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
    private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;


    public ChannelCloseJob(ILogger<ChannelCloseJob> logger, ILightningService lightningService, IChannelOperationRequestRepository channelOperationRequestRepository)
    {
        _logger = logger;
        _lightningService = lightningService;
        _channelOperationRequestRepository = channelOperationRequestRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(ChannelCloseJob));
        try
        {
            var token = context.CancellationToken;
            token.ThrowIfCancellationRequested();

            await JobRescheduler.SetNextInterval(context);

            var data = context.JobDetail.JobDataMap;
            var closeRequestId = data.GetInt("closeRequestId");
            var forceClose = data.GetBoolean("forceClose");
            var closeRequest = await _channelOperationRequestRepository.GetById(closeRequestId);
            await _lightningService.CloseChannel(closeRequest, forceClose);

            var schedule = context.Scheduler.DeleteJob(context.JobDetail.Key, token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(ChannelCloseJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelCloseJob));
    }
}