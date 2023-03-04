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
public class NodeChannelSuscribeJob : IJob
{
    private readonly ILogger<NodeChannelSuscribeJob> _logger;
    private readonly ILightningService _lightningService;
    private readonly INodeRepository _nodeRepository;
    
    public NodeChannelSuscribeJob(ILogger<NodeChannelSuscribeJob> logger, ILightningService lightningService, INodeRepository nodeRepository)
    {
        _logger = logger;
        _lightningService = lightningService;
        _nodeRepository = nodeRepository;
    }
        
    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(NodeChannelSuscribeJob));
        var data = context.JobDetail.JobDataMap;
        var nodeId = data.GetInt("nodeId");
        try
        {
            var node = await _nodeRepository.GetById(nodeId);
            await _lightningService.SubscribeToNode(node);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while subscribing for the channel updates of node {NodeId}", nodeId);
        }
        
        _logger.LogInformation("{JobName} ended", nameof(NodeChannelSuscribeJob));
    }
}