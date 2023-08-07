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
        Assert.ThrowsAsync<Exception>(async () => await _nodeUpdateManager.NodeUpdateManagement(channelEventUpdate, new Node()));
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
}