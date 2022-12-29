using FundsManager.Data.Repositories.Interfaces;
using Quartz;

namespace FundsManager.Jobs
{
    /// <summary>
    /// CRON Job which checks if there is any funds on the LND hot wallet and sweep them to the returning multisig wallet (if assigned) to the node.
    /// </summary>
    public class SweepAllNodesWalletsJob : IJob
    {
        private readonly ILogger<SweepAllNodesWalletsJob> _logger;
        private readonly ISchedulerFactory _schedulerFactory;
        private readonly INodeRepository _nodeRepository;

        public SweepAllNodesWalletsJob(ILogger<SweepAllNodesWalletsJob> logger,
            ISchedulerFactory schedulerFactory,
            INodeRepository nodeRepository)
        {
            _logger = logger;
            _schedulerFactory = schedulerFactory;
            _nodeRepository = nodeRepository;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation("Starting {JobName}... ", nameof(SweepAllNodesWalletsJob));
            try
            {
                var managedNodes = await _nodeRepository.GetAllManagedByFundsManager();

                foreach (var managedNode in managedNodes.Where(managedNode => managedNode.ChannelAdminMacaroon != null))
                {
                    var job = JobBuilder.Create<SweepNodeWalletsJob>()
                        .WithIdentity($"{nameof(SweepNodeWalletsJob)}-{managedNode.Id}")
                        .SetJobData(new JobDataMap(new Dictionary<string, string> { { "managedNodeId", managedNode.Id.ToString() } }))
                        .Build();

                    var trigger = TriggerBuilder.Create()
                        .WithIdentity($"{nameof(SweepNodeWalletsJob)}Trigger-{managedNode.Id}")
                        .StartNow()
                        .Build();

                    var scheduler = await _schedulerFactory.GetScheduler();
                    await scheduler.ScheduleJob(job, trigger);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on {JobName}", nameof(SweepAllNodesWalletsJob));
                throw new JobExecutionException(e);
            }

            _logger.LogInformation("{JobName} ended", nameof(SweepAllNodesWalletsJob));
        }
    }
}