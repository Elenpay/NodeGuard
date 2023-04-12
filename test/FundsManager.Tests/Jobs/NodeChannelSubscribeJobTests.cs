using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Jobs;
using FundsManager.Services;
using Lnrpc;
using Microsoft.Extensions.Logging;
using Quartz;
using Channel = FundsManager.Data.Models.Channel;
using Node = FundsManager.Data.Models.Node;


namespace FundsManager.Tests.Jobs;

public class NodeChannelSubscribeJobTests
{
    private Mock<ILogger<NodeChannelSuscribeJob>> _loggerMock;
    private Mock<ILightningService> _lightningServiceMock;
    private Mock<INodeRepository> _nodeRepositoryMock;
    private Mock<IChannelRepository> _channelRepositoryMock;
    private NodeChannelSuscribeJob _nodeUpdateManager;
    private ISchedulerFactory _schedulerFactory;

    public NodeChannelSubscribeJobTests()
    {
        _loggerMock = new Mock<ILogger<NodeChannelSuscribeJob>>();
        _nodeRepositoryMock = new Mock<INodeRepository>();
        _channelRepositoryMock = new Mock<IChannelRepository>();
        _lightningServiceMock = new Mock<ILightningService>();
        
        _nodeUpdateManager = new NodeChannelSuscribeJob(
            _loggerMock.Object,
            _lightningServiceMock.Object,
            _nodeRepositoryMock.Object,
            _channelRepositoryMock.Object,
            _schedulerFactory);
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
    public async Task NodeUpdateManagement_AddsNewNode_WhenRemoteNodeNotFound()
    {
        // Arrange
        var channelEventUpdate = new ChannelEventUpdate()
        {
            Type = ChannelEventUpdate.Types.UpdateType.OpenChannel,
            OpenChannel = new Lnrpc.Channel()
            {
                ChannelPoint = "1:1",
                CloseAddress = "closeAddress",
                RemotePubkey = "remotePubkey",
            },
        };
        _nodeRepositoryMock.Setup(repo => repo.GetByPubkey(channelEventUpdate.OpenChannel.RemotePubkey)).ReturnsAsync((Node?)null);
        _nodeRepositoryMock.Setup(repo => repo.GetByPubkey(channelEventUpdate.OpenChannel.RemotePubkey)).ReturnsAsync(new Node(){Id = 1, Name = "TestAlias", PubKey = "TestPubKey"});
        _nodeRepositoryMock.Setup(repo => repo.AddAsync(It.IsAny<Node>())).ReturnsAsync((true, ""));
        _channelRepositoryMock.Setup(repo => repo.AddAsync(It.IsAny<Channel>())).ReturnsAsync((true, ""));

        _lightningServiceMock.Setup(service => service.GetNodeInfo(channelEventUpdate.OpenChannel.RemotePubkey))
            .ReturnsAsync(new LightningNode() { Alias = "TestAlias", PubKey = "TestPubKey" });

        // Act
        await _nodeUpdateManager.NodeUpdateManagement(channelEventUpdate, new Node(){Id = 1});

        // Assert
        _nodeRepositoryMock.Verify(repo => repo.AddAsync(It.IsAny<Node>()), Times.Once);
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
