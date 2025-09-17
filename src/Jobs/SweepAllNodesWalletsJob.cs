// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.



using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using Quartz;

namespace NodeGuard.Jobs
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
                var managedNodes = await _nodeRepository.GetAllManagedByNodeGuard(false);

                var scheduler = await _schedulerFactory.GetScheduler();
                foreach (var managedNode in managedNodes.Where(managedNode => managedNode.ChannelAdminMacaroon != null && managedNode.AutosweepEnabled))
                {
                    var map = new JobDataMap();
                    map.Put("managedNodeId", managedNode.Id.ToString());
                    var job = SimpleJob.Create<SweepNodeWalletsJob>(map, managedNode.Id.ToString());

                    var jobExists = await scheduler.CheckExists(job.Job.Key);
                    if (!jobExists)
                    {
                        _logger.LogInformation("Scheduling {JobName} for {NodeId}", nameof(SweepNodeWalletsJob), managedNode.Id);
                        await scheduler.ScheduleJob(job.Job, job.Trigger);
                    }
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
