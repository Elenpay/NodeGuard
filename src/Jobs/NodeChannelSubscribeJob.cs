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
using NBitcoin.Protocol;
using Quartz;
using Channel = FundsManager.Data.Models.Channel;
using Node = FundsManager.Data.Models.Node;

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
            
            if (node == null)
            {
                _logger.LogInformation("The node: {@Node} is no longer ready to be supported quartz jobs", node);
                return;
            }

            var client = LightningService.CreateLightningClient(node.Endpoint);
            var result = client.Execute(x => x.SubscribeChannelEvents(new ChannelEventSubscription(), 
                new Metadata {
                    {"macaroon", node.ChannelAdminMacaroon}
                }, null, default));

            while (await result.ResponseStream.MoveNext())
            {
                node = await _nodeRepository.GetById(nodeId);
            
                if (node == null)
                {
                    _logger.LogInformation("The node: {@Node} is no longer ready to be supported quartz jobs", node);
                    return;
                }
                
                try {
                    var channelEventUpdate = result.ResponseStream.Current;
                    _logger.LogInformation("Channel event update received for node {@NodeId}", node.Id);
                    NodeUpdateManagement(channelEventUpdate);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error reading and update event of node {NodeId}", nodeId);
                    throw new JobExecutionException(e, true);
                }
            }

        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while subscribing for the channel updates of node {NodeId}", nodeId);
            throw new JobExecutionException(e, true);
        }
        
        _logger.LogInformation("{JobName} ended", nameof(NodeChannelSuscribeJob));
    }

    public async Task NodeUpdateManagement(ChannelEventUpdate channelEventUpdate)
    {
        switch (channelEventUpdate.Type)
        {
            case ChannelEventUpdate.Types.UpdateType.OpenChannel:
                if (String.IsNullOrEmpty(channelEventUpdate.OpenChannel.CloseAddress))
                {
                    _logger.LogError("Close address is empty");
                    throw new Exception("Close address is empty");
                }

                var channelOpened = channelEventUpdate.OpenChannel;
                var fundingTxAndIndex = channelOpened.ChannelPoint.Split(":");
                var channelToOpen = new Channel()
                {
                    ChanId = channelOpened.ChanId,
                    SatsAmount = channelOpened.Capacity,
                    Status = Channel.ChannelStatus.Open,
                    IsAutomatedLiquidityEnabled = false,
                    BtcCloseAddress = channelOpened.CloseAddress,
                    FundingTx = fundingTxAndIndex[0],
                    FundingTxOutputIndex = Convert.ToUInt32(fundingTxAndIndex[1]),
                    CreatedByNodeGuard = false,
                    CreationDatetime = DateTimeOffset.Now,
                    UpdateDatetime = DateTimeOffset.Now,
                };

                var remoteNode = await _nodeRepository.GetByPubkey(channelOpened.RemotePubkey);
                if (remoteNode == null)
                {
                    var foundNode = await _lightningService.GetNodeInfo(channelOpened.RemotePubkey);
                    if (foundNode == null)
                    {
                        throw new Exception("Node info not found");
                    }

                    remoteNode = new Node()
                    {
                        Name = foundNode.Alias,
                        PubKey = foundNode.PubKey,
                    };
                    var addNode = await _nodeRepository.AddAsync(remoteNode);
                    if (!addNode.Item1)
                    {
                        throw new Exception(addNode.Item2);
                    }


                }
                break;
            
            case ChannelEventUpdate.Types.UpdateType.ClosedChannel:
                var channelClosed = channelEventUpdate.ClosedChannel;
                var channelToClose = await _channelRepository.GetByChanId(channelClosed.ChanId);
                if (channelToClose == null)
                {
                    _logger.LogInformation("Channel with chanId: {ChanId} not found in the system", channelClosed.ChanId);
                }
                else
                {
                    channelToClose.Status = Channel.ChannelStatus.Closed;
                    var updateChannel = _channelRepository.Update(channelToClose);
                    if (!updateChannel.Item1)
                    {
                        throw new Exception(updateChannel.Item2);
                    }
                }

                break;
        }
    }
}