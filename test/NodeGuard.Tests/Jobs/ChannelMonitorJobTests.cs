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

using FluentAssertions;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using NodeGuard.Jobs;
using NodeGuard.Services;
using Google.Protobuf;
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
        var context = () => new ApplicationDbContext(options);
        dbContextFactory.Setup(x => x.CreateDbContext()).Returns(context);
        dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
        return dbContextFactory;
    }

    [Fact]
    public async Task RecoverGhostChannels_ChannelIsNotInitiatorButManaged()
    {
        // Arrange
        var dbContextFactory = SetupDbContextFactory();
        var channelMonitorJob = new ChannelMonitorJob(null, dbContextFactory.Object, null, null, null);

        var channel = new Lnrpc.Channel()
        {
            Initiator = false
        };
        var destination = new Node() { Endpoint = "abc" };
        // Act
        var act = () => channelMonitorJob.RecoverGhostChannels(null, destination, channel);

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
            FundingTx = "abc",
            FundingTxOutputIndex = 0
        };
        await context.Channels.AddAsync(request1);
        await context.SaveChangesAsync();

        var channelMonitorJob = new ChannelMonitorJob(null, dbContextFactory.Object, null, null, null);

        var channel = new Lnrpc.Channel()
        {
            ChanId = 2,
            ChannelPoint = "abc:0",
            Initiator = true
        };
        // Act
        var act = () => channelMonitorJob.RecoverGhostChannels(null, null, channel);

        // Assert
        await act.Should().NotThrowAsync();
        dbContextFactory.Invocations.Count.Should().Be(2);
    }

    [Theory]
    [InlineData("locahost")]
    [InlineData(null)]
    public async Task RecoverGhostChannels_CreatesChannel(string? endpoint)
    {
        // Arrange
        var logger = new Mock<ILogger<ChannelMonitorJob>>();
        var dbContextFactory = SetupDbContextFactory();
        var context = await dbContextFactory.Object.CreateDbContextAsync();
        //Mock lightning client with iunmockable methods
        var channelPoint = new ChannelPoint { FundingTxidBytes = ByteString.CopyFrom(Convert.FromHexString("a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e").Reverse().ToArray()), OutputIndex = 1 };

        var listChannelsResponse = new ListChannelsResponse
        {
            Channels =
            {
                new Lnrpc.Channel
                {
                    Active = true,
                    RemotePubkey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                    ChannelPoint = $"{LightningHelper.DecodeTxId(channelPoint.FundingTxidBytes)}:{channelPoint.OutputIndex}",
                    ChanId = 123,
                    Capacity = 1000,
                    LocalBalance = 100,
                    RemoteBalance = 900,
                    Initiator = true
                }
            }
        };

        var lightningClientService = new Mock<ILightningClientService>() { CallBase = true };
        lightningClientService.Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lightning.LightningClient>())).ReturnsAsync(listChannelsResponse);

        var lightningService = new LightningService(null, null, null, null, null, null, null, null, null, lightningClientService.Object, null);

        var channelMonitorJob = new ChannelMonitorJob(logger.Object, dbContextFactory.Object, null, lightningService, lightningClientService.Object);

        var source = new Node()
        {
            Id = 1,
            Endpoint = "localhost",
            ChannelAdminMacaroon = "abc"
        };
        var destination = new Node()
        {
            Id = 2,
            Endpoint = endpoint
        };
        var channel = new Lnrpc.Channel()
        {
            ChanId = 1,
            Initiator = true,
            ChannelPoint = "a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e:1"
        };

        // Act
        await channelMonitorJob.RecoverGhostChannels(source, destination, channel);

        // Assert
        var createdChannel = await context.Channels.FirstAsync();
        createdChannel.SourceNodeId.Should().Be(source.Id);
        createdChannel.DestinationNodeId.Should().Be(destination.Id);
        context.Channels.Count().Should().Be(1);
    }

    [Fact]
    public async Task RecoverGhostChannels_CreatesChannelNotInitiator()
    {
        // Arrange
        var logger = new Mock<ILogger<ChannelMonitorJob>>();
        var dbContextFactory = SetupDbContextFactory();
        var context = await dbContextFactory.Object.CreateDbContextAsync();
        //Mock lightning client with iunmockable methods
        var channelPoint = new ChannelPoint { FundingTxidBytes = ByteString.CopyFrom(Convert.FromHexString("a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e").Reverse().ToArray()), OutputIndex = 1 };

        var listChannelsResponse = new ListChannelsResponse
        {
            Channels =
            {
                new Lnrpc.Channel
                {
                    Active = true,
                    RemotePubkey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                    ChannelPoint = $"{LightningHelper.DecodeTxId(channelPoint.FundingTxidBytes)}:{channelPoint.OutputIndex}",
                    ChanId = 123,
                    Capacity = 1000,
                    LocalBalance = 100,
                    RemoteBalance = 900,
                    Initiator = false
                }
            }
        };

        var lightningClientService = new Mock<ILightningClientService>() { CallBase = true };
        lightningClientService.Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lightning.LightningClient>())).ReturnsAsync(listChannelsResponse);

        var lightningService = new LightningService(null, null, null, null, null, null, null, null, null, lightningClientService.Object, null);

        var channelMonitorJob = new ChannelMonitorJob(logger.Object, dbContextFactory.Object, null, lightningService, lightningClientService.Object);

        var source = new Node()
        {
            Id = 1,
            Endpoint = "localhost",
            ChannelAdminMacaroon = "abc"
        };
        var destination = new Node()
        {
            Id = 2,
            Endpoint = null,
        };
        var channel = new Lnrpc.Channel()
        {
            ChanId = 1,
            Initiator = false,
            ChannelPoint = "a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e:1"
        };

        // Act
        await channelMonitorJob.RecoverGhostChannels(source, destination, channel);

        // Assert
        var createdChannel = await context.Channels.FirstAsync();
        createdChannel.SourceNodeId.Should().Be(destination.Id);
        createdChannel.DestinationNodeId.Should().Be(source.Id);
        context.Channels.Count().Should().Be(1);
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

    [Fact]
    public async Task MarkClosedChannelsAsClosed_ChannelsIsNull()
    {
        // Arrange
        var logger = new Mock<ILogger<ChannelMonitorJob>>();
        var dbContextFactory = SetupDbContextFactory();
        await using var context = await dbContextFactory.Object.CreateDbContextAsync();

        var channel1 = new Channel()
        {
            Id = 4,
            FundingTx = "a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e",
            Status = Channel.ChannelStatus.Open
        };
        await context.Channels.AddAsync(channel1);
        await context.SaveChangesAsync();

        var channelMonitorJob = new ChannelMonitorJob(logger.Object, dbContextFactory.Object, null, null, null);

        var source = new Node() { Id = 3 };
        // Act
        var act = () => channelMonitorJob.MarkClosedChannelsAsClosed(source, null);

        // Assert
        await using var newContext = await dbContextFactory.Object.CreateDbContextAsync();
        await act.Should().NotThrowAsync();
        var existingChannel = await newContext.Channels.FirstAsync();
        existingChannel.Status.Should().Be(Channel.ChannelStatus.Open);
    }

    [Fact]
    public async Task MarkClosedChannelsAsClosed_ChannelsIsClosed()
    {
        // Arrange
        var logger = new Mock<ILogger<ChannelMonitorJob>>();
        var dbContextFactory = SetupDbContextFactory();
        await using var context = await dbContextFactory.Object.CreateDbContextAsync();

        var channel1 = new Channel()
        {
            Id = 4,
            ChanId = 2,
            FundingTx = "a2dffe0545ae0ce9091949477a9a7d91bb9478eb054fd9fa142e73562287ca4e",
            Status = Channel.ChannelStatus.Open,
            SourceNodeId = 3
        };
        var channel2 = new Channel()
        {
            Id = 2,
            ChanId = 3,
            FundingTx = "03c63200efc79c6675bd9e3f051a7b4d7e512a7594d1fec64a40e6f1f93a2b2927",
            Status = Channel.ChannelStatus.Open,
            SourceNodeId = 3
        };
        await context.Channels.AddAsync(channel1);
        await context.Channels.AddAsync(channel2);
        await context.SaveChangesAsync();

        var channelMonitorJob = new ChannelMonitorJob(logger.Object, dbContextFactory.Object, null, null, null);

        var source = new Node() { Id = 3 };
        var channelsList = new List<Lnrpc.Channel>() { new Lnrpc.Channel() { ChanId = 2, Active = true } };
        // Act
        var act = () => channelMonitorJob.MarkClosedChannelsAsClosed(source, channelsList);

        // Assert
        await using var newContext = await dbContextFactory.Object.CreateDbContextAsync();
        await act.Should().NotThrowAsync();
        var existingChannel1 = await newContext.Channels.FirstAsync();
        existingChannel1.Status.Should().Be(Channel.ChannelStatus.Open);
        var existingChannel2 = await newContext.Channels.LastAsync();
        existingChannel2.Status.Should().Be(Channel.ChannelStatus.Closed);
    }
}