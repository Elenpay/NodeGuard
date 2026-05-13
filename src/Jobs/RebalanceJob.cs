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

using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// One-shot Quartz job that fires a queued run/retry of an existing Rebalance row.
/// The row carries all the parameters; this job only loads it and re-runs the probe + payment.
/// </summary>
[DisallowConcurrentExecution]
public class RebalanceJob : IJob
{
    private readonly ILogger<RebalanceJob> _logger;
    private readonly IRebalanceService _rebalanceService;

    public RebalanceJob(ILogger<RebalanceJob> logger, IRebalanceService rebalanceService)
    {
        _logger = logger;
        _rebalanceService = rebalanceService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var rebalanceId = context.MergedJobDataMap.GetIntValue("rebalanceId");
        _logger.LogInformation("Firing scheduled run/retry for rebalance {Id}", rebalanceId);

        try
        {
            await _rebalanceService.ExecuteAsync(rebalanceId, context.CancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error executing scheduled retry for rebalance {Id}", rebalanceId);
            throw new JobExecutionException(e, refireImmediately: false);
        }
    }
}
