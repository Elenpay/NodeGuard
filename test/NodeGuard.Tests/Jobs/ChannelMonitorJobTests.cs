using FluentAssertions;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using NodeGuard.Jobs;
using NodeGuard.Services;
using NodeGuard.TestHelpers;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Tests.Jobs;

public class ChannelMonitorJobTests
{
    private readonly Random _random = new();

    private Mock<IDbContextFactory<ApplicationDbContext>> SetupDbContextFactory()
    {
        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: "ChannelOperationRequestRepositoryTests" + _random.Next())
            .Options;
        var context = ()=> new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
        dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
        return dbContextFactory;
    }

    [Fact]
    public async Task RecoverGhostChannels_ChannelIsNotInitiator()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        var channelMonitorJob = new ChannelMonitorJob(null, dbContextFactory.Object, null, null, null);

        var channel = new Lnrpc.Channel()
        {
            Initiator = false
        };
        // Act
        var act = () => channelMonitorJob.RecoverGhostChannels(null, null, channel);

        // Assert
        await act.Should().NotThrowAsync();
        dbContextFactory.Invocations.Count.Should().Be(0);

    }

    [Fact]
    public async Task RecoverGhostChannels_ChannelAlreadyExists()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        await using var context = await dbContextFactory.Object.CreateDbContextAsync();

        var request1 = new Channel()
        {
            ChanId = 1,
            FundingTx = "abc:0"
        };
        await context.Channels.AddAsync(request1);
        await context.SaveChangesAsync();

        var channelMonitorJob = new ChannelMonitorJob(null, dbContextFactory.Object, null, null, null);

        var channel = new Lnrpc.Channel()
        {
            ChanId = 1,
            Initiator = true
        };
        // Act
        var act = () => channelMonitorJob.RecoverGhostChannels(null, null, channel);

        // Assert
        await act.Should().NotThrowAsync();
        dbContextFactory.Invocations.Count.Should().Be(2);
    }

    [Fact]
    public async Task RecoverGhostChannels_CreatesChannel()
    {
        // Arrange
        var logger = new Mock<ILogger<ChannelMonitorJob>>();
        var dbContextFactory = SetupDbContextFactory();
        var context = await dbContextFactory.Object.CreateDbContextAsync();
        //Mock lightning client with iunmockable methods
        var channelPoint = new ChannelPoint{ FundingTxidBytes = ByteString.CopyFrom(Convert.FromHexString("a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e").Reverse().ToArray()), OutputIndex = 1};

        var listChannelsResponse = new ListChannelsResponse
        {
            Channels = {new Lnrpc.Channel
                {
                    Active = true,
                    RemotePubkey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                    ChannelPoint = $"{LightningHelper.DecodeTxId(channelPoint.FundingTxidBytes)}:{channelPoint.OutputIndex}",
                    ChanId = 123,
                    Capacity = 1000,
                    LocalBalance = 100,
                    RemoteBalance = 900
                }
            }
        };

        var lightningClient = Interceptor.For<Lightning.LightningClient>()
            .Setup(x => x.ListChannelsAsync(
                Arg.Ignore<ListChannelsRequest>(),
                Arg.Ignore<Metadata>(),
                null,
                Arg.Ignore<CancellationToken>()
            ))
            .Returns(MockHelpers.CreateAsyncUnaryCall(listChannelsResponse));
        var originalLightningClient = LightningService.CreateLightningClient;
        LightningService.CreateLightningClient = (_) => lightningClient;

        var channelMonitorJob = new ChannelMonitorJob(logger.Object, dbContextFactory.Object, null, null, null);

        var source = new Node()
        {
            Endpoint = "localhost",
            ChannelAdminMacaroon = "abc"
        };
        var destination = new Node();
        var channel = new Lnrpc.Channel()
        {
            ChanId = 1,
            Initiator = true,
            ChannelPoint =  "a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e:1"
        };

        // Act
        await channelMonitorJob.RecoverGhostChannels(source, destination, channel);

        // Assert
        context.Channels.Count().Should().Be(1);
        LightningService.CreateLightningClient = originalLightningClient;
    }

    [Fact]
    public async Task RecoverChannelInConfirmationPendingStatus_RequestWithDifferentSource()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        await using var context = await dbContextFactory.Object.CreateDbContextAsync();

        var request1 = new ChannelOperationRequest()
        {
            Status = ChannelOperationRequestStatus.OnChainConfirmationPending,
            SourceNodeId = 10
        };
        await context.ChannelOperationRequests.AddAsync(request1);
        await context.SaveChangesAsync();

        var channelMonitorJob = new ChannelMonitorJob(null, dbContextFactory.Object, null, null, null);

        var source = new Node() { Id = 3 };
        // Act
        var act = () => channelMonitorJob.RecoverChannelInConfirmationPendingStatus(source);

        // Assert
        await act.Should().NotThrowAsync();
        (await context.ChannelOperationRequests.FirstOrDefaultAsync()).Status.Should().Be(ChannelOperationRequestStatus.OnChainConfirmationPending);
    }

    [Fact]
    public async Task RecoverChannelInConfirmationPendingStatus_StuckRequestFound()
    {
        // Arrange
        var logger = new Mock<ILogger<ChannelMonitorJob>>();
        var dbContextFactory = SetupDbContextFactory();
        await using var context = await dbContextFactory.Object.CreateDbContextAsync();

        var request1 = new ChannelOperationRequest()
        {
            Status = ChannelOperationRequestStatus.OnChainConfirmationPending,
            SourceNodeId = 3,
            TxId = "a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e"
        };
        await context.ChannelOperationRequests.AddAsync(request1);

        var channel1 = new Channel()
        {
            Id = 4,
            FundingTx = "a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e"
        };
        await context.Channels.AddAsync(channel1);
        await context.SaveChangesAsync();

        var channelMonitorJob = new ChannelMonitorJob(logger.Object, dbContextFactory.Object, null, null, null);

        var source = new Node() { Id = 3 };
        // Act
        var act = () => channelMonitorJob.RecoverChannelInConfirmationPendingStatus(source);

        // Assert
        await using var newContext = await dbContextFactory.Object.CreateDbContextAsync();
        await act.Should().NotThrowAsync();
        var updatedRequest = await newContext.ChannelOperationRequests.FirstAsync();
        updatedRequest.Status.Should().Be(ChannelOperationRequestStatus.OnChainConfirmed);
        updatedRequest.ChannelId.Should().Be(channel1.Id);
    }
}