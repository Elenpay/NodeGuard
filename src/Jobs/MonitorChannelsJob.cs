using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using Quartz;

namespace NodeGuard.Jobs;

public class MonitorChannelsJob : IJob
{
    private readonly ILogger<MonitorChannelsJob> _logger;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly INodeRepository _nodeRepository;

    public MonitorChannelsJob(ILogger<MonitorChannelsJob> logger, ISchedulerFactory schedulerFactory, INodeRepository nodeRepository)
    {
        _logger = logger;
        _schedulerFactory = schedulerFactory;
        _nodeRepository = nodeRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(MonitorChannelsJob));
        try
        {
            var managedNodes = await _nodeRepository.GetAllManagedByNodeGuard();

            var scheduler = await _schedulerFactory.GetScheduler();

            foreach (var managedNode in managedNodes)
            {
                if (managedNode.ChannelAdminMacaroon != null)
                {
                    var map = new JobDataMap();
                    map.Put("nodeId", managedNode.Id.ToString());
                    var job = SimpleJob.Create<ChannelMonitorJob>(map, managedNode.Id.ToString());
                    await scheduler.ScheduleJob(job.Job, job.Trigger);

                    var jobId = job.Job.Key.ToString();
                    managedNode.JobId = jobId;
                    var jobUpateResult = _nodeRepository.Update(managedNode);
                    if (!jobUpateResult.Item1)
                    {
                        _logger.LogWarning("Couldn't update Node {NodeId} with JobId {JobId}", managedNode.Id, jobId);
                    }
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(MonitorChannelsJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(MonitorChannelsJob));
    }
}