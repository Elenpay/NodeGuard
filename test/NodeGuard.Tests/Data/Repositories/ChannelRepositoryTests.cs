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

using AutoMapper;
using FluentAssertions;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Google.Protobuf;
using Lnrpc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Data.Repositories;

public class ChannelRepositoryTests
{
    /// <summary>
    /// If the channel is not found on the node, it should be allowed to be marked as closed
    /// </summary>
    [Fact]
    public async Task MarkAsClosed_Positive()
    {
        // Arrange

        //Mock lightning client with iunmockable methods
        var channelPoint = new ChannelPoint { FundingTxidBytes = ByteString.CopyFromUtf8("e59fa8edcd772213239daef2834d9021d1aecc591d605b426ae32c4bec5fdd7d"), OutputIndex = 1 };

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
                    RemoteBalance = 900
                }
            }
        };

        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();

        var mockRepository = new Mock<IRepository<Channel>>();
        var lightningClientService = new Mock<ILightningClientService>();

        //Mock update
        mockRepository.Setup(x => x.Update(It.IsAny<Channel>(), It.IsAny<ApplicationDbContext>()))
            .Returns((true, null));
        lightningClientService.Setup(x => x.ListChannels(It.IsAny<Node>(), It.IsAny<Lightning.LightningClient>())).ReturnsAsync(listChannelsResponse);

        var channelRepository = new ChannelRepository(mockRepository.Object,
            new Mock<ILogger<ChannelRepository>>().Object,
            dbContextFactory.Object,
            new Mock<IChannelOperationRequestRepository>().Object,
            new Mock<ISchedulerFactory>().Object,
            new Mock<IMapper>().Object,
            lightningClientService.Object);

        var channel = new Channel
        {
            Id = 1,
            ChanId = 124,
            SatsAmount = 1000,
            Status = Channel.ChannelStatus.Open,
            ChannelOperationRequests = new List<ChannelOperationRequest>
            {
                new ChannelOperationRequest
                {
                    SourceNodeId = 0,
                    SourceNode = new Node
                    {
                        Endpoint = "localhost:10001",
                        ChannelAdminMacaroon = "020230230112031203"
                    },
                }
            }
        };

        //Act

        var result = await channelRepository.MarkAsClosed(channel);

        //Assert

        result.Item1.Should().BeTrue();
        channel.Status.Should().Be(Channel.ChannelStatus.Closed);
        result.Item2.Should().BeNull();
    }

    /// <summary>
    /// If the channel is found, we should do a normal close and not allow mark it as closed
    /// </summary>
    [Fact]
    public async Task MarkAsClosed_Negative_ChannelFound()
    {
        // Arrange

        //Mock lightning client with iunmockable methods
        var channelPoint = new ChannelPoint { FundingTxidBytes = ByteString.CopyFromUtf8("e59fa8edcd772213239daef2834d9021d1aecc591d605b426ae32c4bec5fdd7d"), OutputIndex = 1 };

        var listChannelsResponse = new ListChannelsResponse
        {
            Channels =
            {
                new Lnrpc.Channel
                {
                    Active = true,
                    RemotePubkey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                    ChannelPoint = $"{LightningHelper.DecodeTxId(channelPoint.FundingTxidBytes)}:{channelPoint.OutputIndex}",
                    ChanId = 124,
                    Capacity = 1000,
                    LocalBalance = 100,
                    RemoteBalance = 900
                }
            }
        };

        var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();

        var mockRepository = new Mock<IRepository<Channel>>();

        var lightningClientService = new Mock<ILightningClientService>();
        //Mock update
        mockRepository.Setup(x => x.Update(It.IsAny<Channel>(), It.IsAny<ApplicationDbContext>()))
            .Returns((true, null));

        var channelRepository = new ChannelRepository(mockRepository.Object,
            new Mock<ILogger<ChannelRepository>>().Object,
            dbContextFactory.Object,
            new Mock<IChannelOperationRequestRepository>().Object,
            new Mock<ISchedulerFactory>().Object,
            new Mock<IMapper>().Object,
            lightningClientService.Object);

        var channel = new Channel
        {
            Id = 1,
            ChanId = 124,
            SatsAmount = 1000,
            Status = Channel.ChannelStatus.Open,
            ChannelOperationRequests = new List<ChannelOperationRequest>
            {
                new ChannelOperationRequest
                {
                    SourceNodeId = 0,
                    SourceNode = new Node
                    {
                        Endpoint = "localhost:10001",
                        ChannelAdminMacaroon = "020230230112031203"
                    },
                }
            }
        };

        //Act

        var result = await channelRepository.MarkAsClosed(channel);

        //Assert

        result.Item1.Should().BeFalse();
        channel.Status.Should().Be(Channel.ChannelStatus.Open);
        result.Item2.Should().NotBeNull();
    }
}