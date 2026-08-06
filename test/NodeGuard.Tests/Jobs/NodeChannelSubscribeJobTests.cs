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

using System.Runtime.CompilerServices;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Jobs;
using NodeGuard.Services;
using Lnrpc;
using Microsoft.Extensions.Logging;
using Channel = NodeGuard.Data.Models.Channel;
using Node = NodeGuard.Data.Models.Node;


namespace NodeGuard.Tests.Jobs;

public class NodeChannelSubscribeJobTests
{
    private Mock<ILogger<NodeChannelSuscribeJob>> _loggerMock;
    private Mock<ILightningService> _lightningServiceMock;
    private Mock<INodeRepository> _nodeRepositoryMock;
    private Mock<IChannelRepository> _channelRepositoryMock;
    private NodeChannelSuscribeJob _nodeUpdateManager;
    private Mock<ILightningClientService> _lightningClientsService;

    public NodeChannelSubscribeJobTests()
    {
        _loggerMock = new Mock<ILogger<NodeChannelSuscribeJob>>();
        _nodeRepositoryMock = new Mock<INodeRepository>();
        _channelRepositoryMock = new Mock<IChannelRepository>();
        _lightningServiceMock = new Mock<ILightningService>();
        _lightningClientsService = new Mock<ILightningClientService>();

        _nodeUpdateManager = new NodeChannelSuscribeJob(
            _loggerMock.Object,
            _lightningServiceMock.Object,
            _nodeRepositoryMock.Object,
            _channelRepositoryMock.Object,
            _lightningClientsService.Object);
    }

    [Fact]
    public async Task NodeUpdateManagement_ThrowsException_WhenCloseAddressIsEmpty()
    {
        // Arrange
        var channelEventUpdate = new ChannelEventUpdate()
        {
            Type = ChannelEventUpdate.Types.UpdateType.OpenChannel,
            OpenChannel = new Lnrpc.Channel()
            {
                CloseAddress = "",
            },
        };

        // Act + Assert
        await Assert.ThrowsAnyAsync<Exception>(async () => await _nodeUpdateManager.NodeUpdateManagement(channelEventUpdate, new Node()));
    }

    [Fact]
    public async Task NodeUpdateManagement_UpdatesChannelStatus_WhenClosedChannelEventReceived()
    {
        // Arrange
        var channelEventUpdate = new ChannelEventUpdate()
        {
            Type = ChannelEventUpdate.Types.UpdateType.ClosedChannel,
            ClosedChannel = new ChannelCloseSummary()
            {
                ChanId = 0101010101,
            },
        };
        var channelToClose = new Channel()
        {
            ChanId = channelEventUpdate.ClosedChannel.ChanId,
            Status = Channel.ChannelStatus.Open,
        };
        _channelRepositoryMock.Setup(repo => repo.GetByChanId(channelToClose.ChanId)).ReturnsAsync(channelToClose);
        _channelRepositoryMock.Setup(repo => repo.Update(channelToClose)).Returns((true, ""));

        // Act
        await _nodeUpdateManager.NodeUpdateManagement(channelEventUpdate, new Node());

        // Assert
        Assert.Equal(Channel.ChannelStatus.Closed, channelToClose.Status);
        _channelRepositoryMock.Verify(repo => repo.Update(channelToClose), Times.Once);
    }

    [Fact]
    public async Task NodeUpdateManagement_SetsDynamicFeeFromLocalNode_WhenLocalNodeIsInitiator()
    {
        // Arrange
        var localNode = new Node { Id = 1, Endpoint = "10.0.0.1", DynamicFeeManagementEnabled = true };
        var remoteNode = new Node { Id = 2, PubKey = "03remote", DynamicFeeManagementEnabled = false }; // unmanaged (no Endpoint)

        var channelEventUpdate = new ChannelEventUpdate
        {
            Type = ChannelEventUpdate.Types.UpdateType.OpenChannel,
            OpenChannel = new Lnrpc.Channel
            {
                ChanId = 123,
                ChannelPoint = "abc:0",
                Capacity = 1000,
                RemotePubkey = remoteNode.PubKey,
                CloseAddress = "bcrt1qclose",
                Initiator = true,
            },
        };

        var captured = SetupOpenChannelCapture(remoteNode);

        // Act
        await _nodeUpdateManager.NodeUpdateManagement(channelEventUpdate, localNode);

        // Assert: source is the initiator (local node) and the flag follows it
        Assert.NotNull(captured.Value);
        Assert.Equal(localNode.Id, captured.Value!.SourceNodeId);
        Assert.True(captured.Value!.IsDynamicFeeEnabled);
    }

    [Fact]
    public async Task NodeUpdateManagement_SetsDynamicFeeFromLocalNode_WhenBothManagedAndLocalNodeIsNotInitiator()
    {
        // Arrange: both nodes managed. The handler de-dupes by bailing out on the initiator's event,
        // so the record is created here on the non-initiator's event. The source is the initiator
        // (the remote node), while the dynamic-fee flag follows the local subscribing node.
        var localNode = new Node { Id = 2, Endpoint = "10.0.0.2", DynamicFeeManagementEnabled = true };
        var remoteNode = new Node { Id = 1, PubKey = "03remote", Endpoint = "10.0.0.1", DynamicFeeManagementEnabled = false };

        var channelEventUpdate = new ChannelEventUpdate
        {
            Type = ChannelEventUpdate.Types.UpdateType.OpenChannel,
            OpenChannel = new Lnrpc.Channel
            {
                ChanId = 123,
                ChannelPoint = "abc:0",
                Capacity = 1000,
                RemotePubkey = remoteNode.PubKey,
                CloseAddress = "bcrt1qclose",
                Initiator = false,
            },
        };

        var captured = SetupOpenChannelCapture(remoteNode);

        // Act
        await _nodeUpdateManager.NodeUpdateManagement(channelEventUpdate, localNode);

        // Assert: source is the initiator (remote node), and the flag follows the local subscribing node
        Assert.NotNull(captured.Value);
        Assert.Equal(remoteNode.Id, captured.Value!.SourceNodeId);
        Assert.Equal(localNode.DynamicFeeManagementEnabled, captured.Value!.IsDynamicFeeEnabled);
    }

    /// <summary>
    /// Wires the node/channel repositories so an OpenChannel event reaches AddAsync, and returns a
    /// holder that captures the channel the handler tries to persist.
    /// </summary>
    private StrongBox<Channel?> SetupOpenChannelCapture(Node remoteNode)
    {
        var captured = new StrongBox<Channel?>(null);
        _nodeRepositoryMock
            .Setup(r => r.GetOrCreateByPubKey(remoteNode.PubKey, It.IsAny<ILightningService>()))
            .ReturnsAsync(remoteNode);
        _nodeRepositoryMock
            .Setup(r => r.GetByPubkey(remoteNode.PubKey))
            .ReturnsAsync(remoteNode);
        _channelRepositoryMock
            .Setup(r => r.GetByChanId(It.IsAny<ulong>()))
            .ReturnsAsync((Channel?)null);
        _channelRepositoryMock
            .Setup(r => r.AddAsync(It.IsAny<Channel>()))
            .Callback<Channel>(c => captured.Value = c)
            .ReturnsAsync((true, (string?)null));
        return captured;
    }
}