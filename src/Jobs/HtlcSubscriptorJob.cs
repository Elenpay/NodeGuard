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
using Quartz;

namespace NodeGuard.Jobs;

[DisallowConcurrentExecution]
public class HtlcSubscriptorJob : IJob
{
    private readonly ILogger<HtlcSubscriptorJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IHtlcMonitoringScheduler _htlcMonitoringScheduler;

    public HtlcSubscriptorJob(ILogger<HtlcSubscriptorJob> logger,
        INodeRepository nodeRepository,
        IHtlcMonitoringScheduler htlcMonitoringScheduler)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _htlcMonitoringScheduler = htlcMonitoringScheduler;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(HtlcSubscriptorJob));
        try
        {
            var managedNodes = await _nodeRepository.GetAllManagedByNodeGuard(false);
            foreach (var managedNode in managedNodes)
            {
                await _htlcMonitoringScheduler.EnsureNodeWorkerScheduled(managedNode);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(HtlcSubscriptorJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(HtlcSubscriptorJob));
    }
}