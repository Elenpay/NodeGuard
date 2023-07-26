/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using Quartz;

namespace NodeGuard.Jobs;

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
            var managedNodes = await _nodeRepository.GetAllManagedByNodeGuard();

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
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelAcceptorJob));
    }
}