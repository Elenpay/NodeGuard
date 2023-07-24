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

using FundsManager.Data;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Services;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Channel = Lnrpc.Channel;

namespace FundsManager.Jobs;

/// <summary>
/// Job for update the status of the channels
/// </summary>
/// <returns></returns>
[DisallowConcurrentExecution]
public class ChannelMonitorJob : IJob
{
    private readonly ILogger<ChannelMonitorJob> _logger;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly INodeRepository _nodeRepository;
    private readonly ILightningService _lightningService;

    public ChannelMonitorJob(ILogger<ChannelMonitorJob> logger, IDbContextFactory<ApplicationDbContext> dbContextFactory, INodeRepository nodeRepository, ILightningService lightningService)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _nodeRepository = nodeRepository;
        _lightningService = lightningService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.JobDetail.JobDataMap;
        var nodeId = data.GetInt("nodeId");
        _logger.LogInformation("Starting {JobName} for node {nodeId}... ", nameof(ChannelMonitorJob), nodeId);
        try
        {
            var node1 = await _nodeRepository.GetById(nodeId);

            if (node1 == null)
            {
                _logger.LogInformation("The node: {@Node} is no longer ready to be supported quartz jobs", node1);
                return;
            }

            var client = LightningService.CreateLightningClient(node1.Endpoint);
            var result = client.Execute(x => x.ListChannels(new ListChannelsRequest(),
                new Metadata {
                    {"macaroon", node1.ChannelAdminMacaroon}
                }, null, default));

            foreach (var channel in result?.Channels)
            {
                var node2 = await _nodeRepository.GetByPubkey(channel.RemotePubkey);

                if (node2 == null)
                {
                    var foundNode = await _lightningService.GetNodeInfo(channel.RemotePubkey);
                    if (foundNode == null)
                    {
                        throw new Exception("Node info not found");
                    }

                    node2 = new Node()
                    {
                        Name = foundNode.Alias,
                        PubKey = foundNode.PubKey,
                    };
                    var addNode = await _nodeRepository.AddAsync(node2);
                    if (!addNode.Item1)
                    {
                        throw new Exception(addNode.Item2);
                    }
                }

                try {
                    // Recover Operations on channels
                    await RecoverGhostChannels(node1, node2, channel);
                    await RecoverChannelInConfirmationPendingStatus(node1);
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

        _logger.LogInformation("{JobName} ended", nameof(ChannelMonitorJob));
    }

    private async Task RecoverGhostChannels(Node source, Node destination, Channel? channel)
    {
        if (!channel.Initiator) return;

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var channelExists = await context.Channels.AnyAsync(c => c.ChanId == channel.ChanId);
        if (channelExists) return;

        var channelPoint = channel.ChannelPoint.Split(":");
        var fundingTx = channelPoint[0];
        var outputIndex = channelPoint[1];

        var parsedChannelPoint = new ChannelPoint
        {
            FundingTxidStr = fundingTx, FundingTxidBytes = ByteString.CopyFrom(Convert.FromHexString(fundingTx).Reverse().ToArray()),
            OutputIndex = Convert.ToUInt32(outputIndex)
        };

        var createdChannel = await LightningService.CreateChannel(source, destination.Id, parsedChannelPoint, channel.Capacity, channel.CloseAddress);

        await context.Channels.AddAsync(createdChannel);
        await context.SaveChangesAsync();
    }

    private async Task RecoverChannelInConfirmationPendingStatus(Node source)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var confirmationPendingRequests = context.ChannelOperationRequests.Where(or => or.Status == ChannelOperationRequestStatus.OnChainConfirmationPending).ToList();
        foreach (var request in confirmationPendingRequests)
        {
            if (request.SourceNodeId != source.Id) return;
            if (request.TxId == null)
            {
                _logger.LogWarning("The channel operation request {RequestId} is in OnChainConfirmationPending status but the txId is null", request.Id);
                return;
            }
            var channel = await context.Channels.FirstOrDefaultAsync(c => c.FundingTx == request.TxId);
            request.ChannelId = channel.Id;
            request.Status = ChannelOperationRequestStatus.OnChainConfirmed;
            await context.SaveChangesAsync();
        }
    }
}