using FundsManager.Data.Repositories.Interfaces;
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
        _logger.LogInformation("Starting ChannelAcceptorJob... ");
        try
        {
            var managedNodes = await _nodeRepository.GetAllManagedByFundsManager();

            foreach (var managedNode in managedNodes)
            {
                if (managedNode.ChannelAdminMacaroon != null)
                {
                    var job = JobBuilder.Create<ProcessNodeChannelAcceptorJob>()
                        .WithIdentity($"{nameof(ProcessNodeChannelAcceptorJob)}-{managedNode.Id}")
                        .SetJobData(new JobDataMap(new Dictionary<string, string> { { "managedNodeId", managedNode.Id.ToString() } }))
                        .Build();

                    var trigger = TriggerBuilder.Create()
                        .WithIdentity($"{nameof(ProcessNodeChannelAcceptorJob)}Trigger-{managedNode.Id}")
                        .StartNow()
                        .Build();

                    var scheduler = await _schedulerFactory.GetScheduler();
                    await scheduler.ScheduleJob(job, trigger);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {}", nameof(ChannelAcceptorJob));
            throw new JobExecutionException(e, true);
        }

        _logger.LogInformation("ChannelAcceptorJob ended");
    }
}