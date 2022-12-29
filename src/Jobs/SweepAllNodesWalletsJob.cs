using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
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
            _logger.LogInformation("Starting {}... ", nameof(SweepAllNodesWalletsJob));
            try
            {
                var managedNodes = await _nodeRepository.GetAllManagedByFundsManager();

                var scheduler = await _schedulerFactory.GetScheduler();
                foreach (var managedNode in managedNodes.Where(managedNode => managedNode.ChannelAdminMacaroon != null))
                {
                    var map = new JobDataMap();
                    map.Put("managedNodeId", managedNode.Id.ToString());
                    var job = SimpleJob.Create<SweepNodeWalletsJob>(map, managedNode.Id.ToString());
                    await scheduler.ScheduleJob(job.Job, job.Trigger);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on {}", nameof(SweepAllNodesWalletsJob));
                throw new JobExecutionException(e);
            }

            _logger.LogInformation("{} ended", nameof(SweepAllNodesWalletsJob));
        }
    }
}