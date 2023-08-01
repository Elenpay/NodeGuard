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

using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Channel = Lnrpc.Channel;

namespace NodeGuard.Jobs;

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
    private readonly ILightningClientsStorageService _lightningClientsStorageService;

    public ChannelMonitorJob(ILogger<ChannelMonitorJob> logger, IDbContextFactory<ApplicationDbContext> dbContextFactory, INodeRepository nodeRepository, ILightningService lightningService, ILightningClientsStorageService lightningClientsStorageService)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _nodeRepository = nodeRepository;
        _lightningService = lightningService;
        _lightningClientsStorageService = lightningClientsStorageService;
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
                _logger.LogInformation("The node {NodeId} was set up for monitoring but the node doesn't exist anymore", nodeId);
                return;
            }

            var client = _lightningClientsStorageService.GetLightningClient(node1.Endpoint);
            var result = client.ListChannels(new ListChannelsRequest(),
                new Metadata
                {
                    { "macaroon", node1.ChannelAdminMacaroon }
                });

            foreach (var channel in result?.Channels)
            {
                var node2 = await _nodeRepository.GetOrCreateByPubKey(channel.RemotePubkey, _lightningService);

                // Recover Operations on channels
                await RecoverGhostChannels(node1, node2, channel);
                await RecoverChannelInConfirmationPendingStatus(node1);
            }

        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to monitor channels of node {NodeId}", nodeId);
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(ChannelMonitorJob));
    }

    public async Task RecoverGhostChannels(Node source, Node destination, Channel channel)
    {
        if (!channel.Initiator && destination.IsManaged) return;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            var channelExists = await dbContext.Channels.AnyAsync(c => c.ChanId == channel.ChanId);
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
            createdChannel.CreatedByNodeGuard = false;

            await dbContext.Channels.AddAsync(createdChannel);
            await dbContext.SaveChangesAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while recovering ghost channel, {SourceNodeId}, {ChannelId}: {Error}", source.Id, channel?.ChanId, e);
        }
    }

    public async Task RecoverChannelInConfirmationPendingStatus(Node source)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        try
        {
            var confirmationPendingRequests = dbContext.ChannelOperationRequests.Where(or => or.Status == ChannelOperationRequestStatus.OnChainConfirmationPending).ToList();
            foreach (var request in confirmationPendingRequests)
            {
                if (request.SourceNodeId != source.Id) return;
                if (request.TxId == null)
                {
                    _logger.LogWarning("The channel operation request {RequestId} is in OnChainConfirmationPending status but the txId is null", request.Id);
                    return;
                }

                var channel = await dbContext.Channels.FirstAsync(c => c.FundingTx == request.TxId);
                request.ChannelId = channel.Id;
                request.Status = ChannelOperationRequestStatus.OnChainConfirmed;
                await dbContext.SaveChangesAsync();
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while recovering channel in OnChainConfirmationPending status, {SourceNodeId}: {Error}", source.Id, e);
        }
    }
}