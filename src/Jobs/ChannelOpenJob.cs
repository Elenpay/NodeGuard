using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using FundsManager.Helpers;
using FundsManager.Data.Models;
using Quartz;
using Quartz.Impl.Triggers;

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
        var data = context.JobDetail.JobDataMap;
        var openRequestId = data.GetInt("openRequestId");
        try
        {
            await RetriableJob.Execute(context, async () =>
            {
                var openRequest = await _channelOperationRequestRepository.GetById(openRequestId);
                await _lightningService.OpenChannel(openRequest);
            });
        }
        catch (Exception e)
        {
            await RetriableJob.OnFail(context, async () =>
            {
                var request = await _channelOperationRequestRepository.GetById(openRequestId);
                request.Status = ChannelOperationRequestStatus.Failed;
                _channelOperationRequestRepository.Update(request);
            });

            _logger.LogError(e, "Error on {JobName}", nameof(ChannelOpenJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelOpenJob));
    }
}