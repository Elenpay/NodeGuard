using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Quartz;

namespace FundsManager.Jobs;

/// <summary>
/// Job for the lifetime of the application that intercepts LND channel opening requests to the managed nodes
/// </summary>
/// <returns></returns>
[DisallowConcurrentExecution]
public class ChannelAcceptorJob : IJob
{
    private readonly ILogger<ChannelAcceptorJob> _logger;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly INodeRepository _nodeRepository;

    public ChannelAcceptorJob(ILogger<ChannelAcceptorJob> logger,
        ISchedulerFactory schedulerFactory,
        INodeRepository nodeRepository)
    {
        _logger = logger;
        _schedulerFactory = schedulerFactory;
        _nodeRepository = nodeRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(ChannelAcceptorJob));
        try
        {
            var managedNodes = await _nodeRepository.GetAllManagedByFundsManager();

            var scheduler = await _schedulerFactory.GetScheduler();
            foreach (var managedNode in managedNodes)
            {
                if (managedNode.ChannelAdminMacaroon != null)
                {
                    var map = new JobDataMap();
                    map.Put("managedNodeId", managedNode.Id.ToString());
                    var job = SimpleJob.Create<ProcessNodeChannelAcceptorJob>(map, managedNode.Id.ToString());
                    await scheduler.ScheduleJob(job.Job, job.Trigger);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(ChannelAcceptorJob));
            throw new JobExecutionException(e, true);
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelAcceptorJob));
    }
}