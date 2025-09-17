// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.



using Google.Protobuf;
using Lnrpc;
using Microsoft.EntityFrameworkCore;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Quartz;
using Channel = Lnrpc.Channel;
using ChannelStatus = NodeGuard.Data.Models.Channel.ChannelStatus;

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
    private readonly ILightningClientService _lightningClientService;
    private readonly Dictionary<string, Node> _remoteNodes = new();
    private readonly HashSet<string> _checkedRemoteNodes = new();

    public ChannelMonitorJob(ILogger<ChannelMonitorJob> logger, IDbContextFactory<ApplicationDbContext> dbContextFactory, INodeRepository nodeRepository, ILightningService lightningService, ILightningClientService lightningClientService)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _nodeRepository = nodeRepository;
        _lightningService = lightningService;
        _lightningClientService = lightningClientService;
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

            var client = _lightningClientService.GetLightningClient(node1.Endpoint);
            var result = await _lightningClientService.ListChannels(node1, client);

            var channels = result?.Channels.ToList();
            await MarkClosedChannelsAsClosed(node1, channels);
            foreach (var channel in channels ?? new())
            {
                var node2 = await GetRemoteNode(channel.RemotePubkey);
                if (node2 == null)
                {
                    _logger.LogWarning("The external node {NodeId} was set up for monitoring but the remote node {RemoteNodeId} doesn't exist anymore", nodeId, channel.RemotePubkey);
                    continue;
                }
                await RefreshExternalNodeData(node1, node2, client);

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

    private async Task<Node?> GetRemoteNode(string pubKey)
    {
        if (_remoteNodes.TryGetValue(pubKey, out var node))
        {
            return node;
        }
        node = await _nodeRepository.GetOrCreateByPubKey(pubKey, _lightningService);
        _remoteNodes.Add(node.PubKey, node);
        return node;
    }

    private async Task RefreshExternalNodeData(Node managedNode, Node remoteNode, Lightning.LightningClient lightningClient)
    {
        if (_checkedRemoteNodes.Contains(remoteNode.PubKey))
        {
            return;
        }
        var nodeInfo = await _lightningClientService.GetNodeInfo(managedNode, remoteNode.PubKey, lightningClient);
        if (nodeInfo == null)
        {
            _logger.LogWarning("The external node {NodeId} was set up for monitoring but the remote node {RemoteNodeId} doesn't exist anymore", managedNode.Id, remoteNode.PubKey);
            return;
        }

        if (remoteNode.Name == nodeInfo.Alias) return;

        remoteNode.Name = nodeInfo.Alias;
        var (updated, error) = _nodeRepository.Update(remoteNode);
        if (!updated)
        {
            _logger.LogWarning("Couldn't update Node {NodeId} with Name {Name}: {Error}", remoteNode.Id, remoteNode.Name, error);
        }
        _checkedRemoteNodes.Add(remoteNode.PubKey);
    }

    public async Task RecoverGhostChannels(Node source, Node destination, Channel channel)
    {
        if (!channel.Initiator && destination.IsManaged) return;
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            var channelPoint = channel.ChannelPoint.Split(":");
            var fundingTx = channelPoint[0];
            var outputIndex = Convert.ToUInt32(channelPoint[1]);

            var channelExists = await dbContext.Channels.AnyAsync(c => c.FundingTx.Equals(fundingTx, StringComparison.Ordinal) && c.FundingTxOutputIndex == outputIndex);
            if (channelExists) return;

            var parsedChannelPoint = new ChannelPoint
            {
                FundingTxidStr = fundingTx,
                FundingTxidBytes = ByteString.CopyFrom(Convert.FromHexString(fundingTx).Reverse().ToArray()),
                OutputIndex = outputIndex
            };

            var createdChannel = await _lightningService.CreateChannel(source, destination.Id, parsedChannelPoint, channel.Capacity, channel.CloseAddress);
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

    public async Task MarkClosedChannelsAsClosed(Node source, List<Channel>? channels)
    {
        if (channels == null) return;
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        try
        {
            var openChannels = dbContext.Channels.Where(c => (c.SourceNodeId == source.Id || c.DestinationNodeId == source.Id) && c.Status == ChannelStatus.Open).ToList();
            foreach (var openChannel in openChannels)
            {
                var channel = channels.FirstOrDefault(c => c.ChanId == openChannel.ChanId);
                if (channel == null)
                {
                    openChannel.Status = ChannelStatus.Closed;
                    await dbContext.SaveChangesAsync();
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while marking closed channels as closed, {SourceNodeId}: {Error}", source.Id, e);
        }
    }
}
