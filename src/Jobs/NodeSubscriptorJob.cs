using FundsManager.Data.Repositories;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Quartz;

namespace FundsManager.Jobs;

public class NodeSubscriptorJob : IJob
{
    private readonly ILogger<NodeSubscriptorJob> _logger;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly INodeRepository _nodeRepository;

    public NodeSubscriptorJob(ILogger<NodeSubscriptorJob> logger, ISchedulerFactory schedulerFactory, INodeRepository nodeRepository)
    {
        _logger = logger;
        _schedulerFactory = schedulerFactory;
        _nodeRepository = nodeRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(NodeSubscriptorJob));
        try
        {
            var managedNodes = await _nodeRepository.GetAllManagedByFundsManager();

            var scheduler = await _schedulerFactory.GetScheduler();
            
            foreach (var managedNode in managedNodes)
            {
                if (managedNode.ChannelAdminMacaroon != null)
                {
                    var map = new JobDataMap();
                    map.Put("nodeId", managedNode.Id.ToString());
                    var job = RetriableJob.Create<NodeChannelSuscribeJob>(map, managedNode.Id.ToString(), new []{1,1,1,1});
                    await scheduler.ScheduleJob(job.Job, job.Trigger);
                    
                    managedNode.JobId = job.Job.Key.ToString();
                    var jobUpateResult = _nodeRepository.Update(managedNode);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(NodeSubscriptorJob));
            throw new JobExecutionException(e, false);
        }
        
        _logger.LogInformation("{JobName} ended", nameof(NodeSubscriptorJob));
    }
}