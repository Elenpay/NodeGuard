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

using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using Quartz;

namespace NodeGuard.Jobs;

public interface IHtlcMonitoringScheduler
{
    Task<bool> EnsureNodeWorkerScheduled(Node node);
}

public class HtlcMonitoringScheduler : IHtlcMonitoringScheduler
{
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly ILogger<HtlcMonitoringScheduler> _logger;

    public HtlcMonitoringScheduler(ISchedulerFactory schedulerFactory, ILogger<HtlcMonitoringScheduler> logger)
    {
        _schedulerFactory = schedulerFactory;
        _logger = logger;
    }

    public async Task<bool> EnsureNodeWorkerScheduled(Node node)
    {

        if (node.IsNodeDisabled || string.IsNullOrWhiteSpace(node.Endpoint) || string.IsNullOrWhiteSpace(node.ChannelAdminMacaroon))
        {
            _logger.LogInformation("Skipping HTLC worker scheduling for node {NodeId} because node is not eligible", node.Id);
            return false;
        }

        var scheduler = await _schedulerFactory.GetScheduler();
        var suffix = node.Id.ToString();
        if (await SimpleJob.IsJobExists<NodeHtlcSubscribeJob>(scheduler, suffix))
        {
            return false;
        }

        var map = new JobDataMap
        {
            { "nodeId", suffix }
        };

        try
        {
            var job = SimpleJob.Create<NodeHtlcSubscribeJob>(map, suffix);
            await scheduler.ScheduleJob(job.Job, job.Trigger);
            _logger.LogInformation("HTLC worker scheduled for node {NodeId}", node.Id);
            return true;
        }
        catch (ObjectAlreadyExistsException)
        {
            return false;
        }
    }
}