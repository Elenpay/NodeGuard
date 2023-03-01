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

using FundsManager.Data.Repositories;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using Quartz;

namespace FundsManager.Jobs;

/// <summary>
/// Job for update the status of the channels
/// </summary>
/// <returns></returns>
[DisallowConcurrentExecution]
public class ChannelUpdateJob : IJob
{
    private readonly ILogger<ChannelUpdateJob> _logger;
    private readonly ILightningService _lightningService;
    private readonly IChannelRepository _channelRepository;
    
    public ChannelUpdateJob(ILogger<ChannelUpdateJob> logger, ILightningService lightningService, ChannelRepository channelRepository)
    {
        _logger = logger;
        _lightningService = lightningService;
        _channelRepository = channelRepository;
    }
        
    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(ChannelUpdateJob));
        var data = context.JobDetail.JobDataMap;
        var channelId = data.GetInt("channelId");
        try
        {
            var channel = await _channelRepository.GetById(channelId);
            var res = await _lightningService.UpdateChannel(channel);
            if (!res.Item1) throw new Exception(res.Item2);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while updating channel {ChannelId}", channelId);
        }
        
        _logger.LogInformation("{JobName} ended", nameof(ChannelUpdateJob));
    }
}