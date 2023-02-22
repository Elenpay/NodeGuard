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

using Microsoft.Extensions.Logging;
using FundsManager.Data;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.TestHelpers;
using NBXplorer.Models;
using FluentAssertions;
using FundsManager.Helpers;
using FundsManager.Services.ServiceHelpers;
using Google.Protobuf;
using Lnrpc;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace FundsManager.Services
{
    public class LightningServiceTests
    {
        ILogger<LightningService> _logger = new Mock<ILogger<LightningService>>().Object;

        [Fact]
        public async void OpenChannel_ChannelOperationRequestNotFound()
        {
            // Arrange
            var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
            var channelOperationRequestRepository = new Mock<IChannelOperationRequestRepository>();

            channelOperationRequestRepository
                .Setup(x => x.GetById(It.IsAny<int>()))
                .ReturnsAsync(null as ChannelOperationRequest);

            var lightningService = new LightningService(_logger, channelOperationRequestRepository.Object, null,
                dbContextFactory.Object, null, null, null, null, null, null, new Mock<INBXplorerService>().Object);

            var operationRequest = new ChannelOperationRequest
            {
                RequestType = OperationRequestType.Open
            };

            // Act
            var act = async () => await lightningService.OpenChannel(operationRequest);

            // Assert
            await act.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("ChannelOperationRequest not found");
        } 

        [Fact]
        public async void OpenChannel_Success()
        {
            // Arrange
            Environment.SetEnvironmentVariable("NBXPLORER_URI", "http://10.0.0.2:38762");
            var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: "ChannelOpenDb")
                .Options;
            var context = new ApplicationDbContext(options);
            dbContextFactory.Setup(x => x.CreateDbContextAsync(default)).ReturnsAsync(context);
            var channelOperationRequestRepository = new Mock<IChannelOperationRequestRepository>();
            var nodeRepository = new Mock<INodeRepository>();

            var channelOpReqPsbts = new List<ChannelOperationRequestPSBT>();
            var userSignedPSBT =
                "cHNidP8BAF4BAAAAAexSK/NStWhBu7utoQ5D5FNqMhhdhA34/nEgMU0XjbJlAAAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15+6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6+zUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn+/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQiAgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgLWbNccdr2EVATngtvTKY4H71ihdlTx65jsLnb7l/YLMCIDcxNFGRQcJ9NS+naFO+MRgWUnuoGWmjfPUnL7bs8Li9AgEDBAIAAAABBWlSIQLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPU64iBgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI+WNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgMaU3fyWsF+N0FpN8hSusDj6bESvd9YR509kdgWMLKLjxjtAhDIMAAAgAEAAIABAACAAAAAAAAAAAAAAA==";
            channelOpReqPsbts.Add(new ChannelOperationRequestPSBT()
            {
                PSBT = userSignedPSBT,
            });

            var destinationNode = new Node()
            {
                PubKey = "03485d8dcdd149c87553eeb80586eb2bece874d412e9f117304446ce189955d375",
                ChannelAdminMacaroon = "def",
                Endpoint = "10.0.0.2"
            };

            var wallet = CreateWallet.CreateTestWallet();
            var operationRequest = new ChannelOperationRequest
            {
                RequestType = OperationRequestType.Open,
                SourceNode = new Node()
                {
                    PubKey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                    ChannelAdminMacaroon = "abc",
                    Endpoint = "10.0.0.1"
                },
                DestNode = destinationNode,
                Wallet = wallet,
                ChannelOperationRequestPsbts = channelOpReqPsbts,
            };

            channelOperationRequestRepository
                .Setup(x => x.GetById(It.IsAny<int>()))
                .ReturnsAsync(operationRequest);

            var nodes = new List<Node> {destinationNode};

            nodeRepository
                .Setup(x => x.GetAllManagedByFundsManager())
                .Returns(Task.FromResult(nodes));

            var lightningClient = Interceptor.For<Lightning.LightningClient>()
                .Setup(x => x.GetNodeInfoAsync(
                    Arg.Ignore<NodeInfoRequest>(),
                    Arg.Ignore<Metadata>(),
                    null,
                    Arg.Ignore<CancellationToken>()
                ))
                .Returns(MockHelpers.CreateAsyncUnaryCall(
                    new NodeInfo()
                    {
                        Node = new LightningNode()
                        {
                            Addresses =
                            {
                                new NodeAddress()
                                {
                                    Network = "tcp",
                                    Addr = "10.0.0.2"
                                }
                            }
                        }
                    }));

            LightningHelper.CreateLightningClient = (_) => lightningClient;

            lightningClient
                .Setup(x => x.ConnectPeerAsync(
                    Arg.Ignore<ConnectPeerRequest>(),
                    Arg.Ignore<Metadata>(),
                    null,
                    Arg.Ignore<CancellationToken>()
                ))
                .Returns(MockHelpers.CreateAsyncUnaryCall(new ConnectPeerResponse()));

            var noneUpdate = new OpenStatusUpdate();
            var chanPendingUpdate = new OpenStatusUpdate
            {
                ChanPending = new PendingUpdate()
            };
            var channelPoint = new ChannelPoint{ FundingTxidBytes = ByteString.CopyFromUtf8("e59fa8edcd772213239daef2834d9021d1aecc591d605b426ae32c4bec5fdd7d"), OutputIndex = 1};
            var chanOpenUpdate = new OpenStatusUpdate
            {
                ChanOpen = new ChannelOpenUpdate()
                {
                    ChannelPoint = channelPoint
                }
            };

            var psbtFundUpdate = new OpenStatusUpdate
            {
                PsbtFund = new ReadyForPsbtFunding()
                {
                    Psbt = ByteString.FromBase64(userSignedPSBT)
                }
            };
            lightningClient
                .Setup(x => x.OpenChannel(
                    Arg.Ignore<OpenChannelRequest>(),
                    Arg.Ignore<Metadata>(),
                    null,
                    Arg.Ignore<CancellationToken>()
                ))
                .Returns(MockHelpers.CreateAsyncServerStreamingCall(
                    new List<OpenStatusUpdate>()
                    {
                        noneUpdate,
                        chanPendingUpdate,
                        psbtFundUpdate,
                        chanOpenUpdate
                    }));

            channelOperationRequestRepository
                .Setup(x => x.Update(It.IsAny<ChannelOperationRequest>()))
                .Returns((true, ""));

            var userSignedPsbtParsed = PSBT.Parse(userSignedPSBT, Network.RegTest);
            var utxoChanges = new UTXOChanges();
            var input = userSignedPsbtParsed.Inputs[0];
            var utxoList = new List<UTXO>()
            {
                new UTXO()
                {
                    Outpoint = input.PrevOut,
                    Index = 0,
                    ScriptPubKey = input.WitnessUtxo.ScriptPubKey,
                    KeyPath = new KeyPath("0/0"),
                },
            };

            utxoChanges.Confirmed = new UTXOChange() {UTXOs = utxoList};


            // LightningService.SignPSBT = (_, _, _, _, _, _, _) => Task.FromResult(finalizedPsbt);

            var channelOperationRequestPsbtRepository = new Mock<IChannelOperationRequestPSBTRepository>();
            channelOperationRequestPsbtRepository
                .Setup(x => x.AddAsync(It.IsAny<ChannelOperationRequestPSBT>()))
                .ReturnsAsync((true, ""));

            lightningClient
                .Setup(x => x.FundingStateStep(
                    Arg.Ignore<FundingTransitionMsg>(),
                    Arg.Ignore<Metadata>(),
                    null,
                    default))
                .Returns(new FundingStateStepResp());

            lightningClient
                .Setup(x => x.FundingStateStepAsync(
                    Arg.Ignore<FundingTransitionMsg>(),
                    Arg.Ignore<Metadata>(),
                    null,
                    default))
                .Returns(MockHelpers.CreateAsyncUnaryCall(new FundingStateStepResp()));
            
            //Mock List channels async
            var listChannelsResponse = new ListChannelsResponse
            {
                Channels = {new Lnrpc.Channel
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
            
            lightningClient
                .Setup(x => x.ListChannelsAsync(
                    Arg.Ignore<ListChannelsRequest>(),
                    Arg.Ignore<Metadata>(),
                    null,
                    default))
                .Returns(MockHelpers.CreateAsyncUnaryCall(listChannelsResponse));

            var lightningService = new LightningService(_logger,
                channelOperationRequestRepository.Object,
                nodeRepository.Object,
                dbContextFactory.Object,
                null,
                null,
                null,
                channelOperationRequestPsbtRepository.Object,
                null,
                null,
                LightningServiceHelperTests.GetNBXplorerServiceFullyMocked(utxoChanges).Object);

            // Act
            var act = async () => await lightningService.OpenChannel(operationRequest);

            // Assert
            await act.Should().NotThrowAsync();
        }
    }
}