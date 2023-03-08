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
using Grpc.Core;
using Lnrpc;
using Quartz;
using Channel = FundsManager.Data.Models.Channel;

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
    private readonly IChannelRepository _channelRepository;
    private readonly ISchedulerFactory _schedulerFactory;
    
    public NodeChannelSuscribeJob(ILogger<NodeChannelSuscribeJob> logger, ILightningService lightningService, INodeRepository nodeRepository, IChannelRepository channelRepository, ISchedulerFactory schedulerFactory)
    {
        _logger = logger;
        _lightningService = lightningService;
        _nodeRepository = nodeRepository;
        _channelRepository = channelRepository;
        _schedulerFactory = schedulerFactory;
    }
        
    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.JobDetail.JobDataMap;
        var nodeId = data.GetInt("nodeId");
        _logger.LogInformation("Starting {JobName} for node {nodeId}... ", nameof(NodeChannelSuscribeJob), nodeId);
        try
        {
            var node = await _nodeRepository.GetById(nodeId);
            var client = LightningService.CreateLightningClient(node.Endpoint);
            var result = client.Execute(x => x.SubscribeChannelEvents(new ChannelEventSubscription(), 
                new Metadata {
                    {"macaroon", node.ChannelAdminMacaroon}
                }, null, default));

            while (await result.ResponseStream.MoveNext())
            {
                try {
                    var channelEventUpdate = result.ResponseStream.Current;

                    if (channelEventUpdate.OpenChannel != null)
                    {
                        var channelOpened = channelEventUpdate.OpenChannel;
                        var channel = new Channel()
                        {
                            ChanId = channelOpened.ChanId,
                            SatsAmount = channelOpened.Capacity,
                            Status = Channel.ChannelStatus.Open,
                            IsAutomatedLiquidityEnabled = false,
                            BtcCloseAddress = channelOpened.CloseAddress,
                            FundingTx = channelOpened.ChannelPoint,
                            CreatedByNodeGuard = false,
                            CreationDatetime = DateTimeOffset.Now,
                            UpdateDatetime = DateTimeOffset.Now
                        };
                        var remoteNode = await _nodeRepository.GetByPubkey(channelOpened.RemotePubkey);
                        if (channelOpened.Initiator)
                        {
                            channel.SourceNodeId = node.Id;
                            channel.DestinationNodeId = remoteNode == null ? 1 : remoteNode.Id;
                        }
                        else
                        {
                            channel.SourceNodeId = remoteNode == null ? 1 : remoteNode.Id;
                            channel.DestinationNodeId = node.Id;
                        }
                        
                        var channelExists = await _channelRepository.GetByChanId(channel.ChanId);
                        if (channelExists == null)
                        {
                            var addChannel = await _channelRepository.AddAsync(channel);
                            if (!addChannel.Item1)
                            {
                                throw new Exception(addChannel.Item2);
                            }

                            _logger.LogInformation("Channel with id: {ChannelId} added to the system", channel.Id);
                        }
                        else
                        {
                            _logger.LogInformation("Channel with id: {ChannelId} already exists in the system", channel.Id);
                        }
                        
                    }

                    if (channelEventUpdate.ClosedChannel != null)
                    {
                        var channelClosed = channelEventUpdate.ClosedChannel;
                        var channel = await _channelRepository.GetByChanId(channelClosed.ChanId);
                        if (channel == null)
                        {
                            throw new Exception("Channel not found");
                        }

                        channel.Status = Channel.ChannelStatus.Closed;
                        var updateChannel = _channelRepository.Update(channel);
                        if (!updateChannel.Item1)
                        {
                            throw new Exception(updateChannel.Item2);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error reading and update event of node {NodeId}", nodeId);
                }
            }

        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while subscribing for the channel updates of node {NodeId}", nodeId);
        }
        
        _logger.LogInformation("{JobName} ended", nameof(NodeChannelSuscribeJob));
    }

}