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
using Google.Protobuf;
using Lnrpc;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Channel = FundsManager.Data.Models.Channel;

namespace FundsManager.Services
{
    public class LightningServiceTests
    {
        ILogger<LightningService> _logger = new Mock<ILogger<LightningService>>().Object;
        InternalWallet _internalWallet = CreateWallet.CreateInternalWallet();

        [Fact]
        public void CheckArgumentsAreValid_ArgumentNull()
        {
            // Act
            var act = () => LightningService.CheckArgumentsAreValid(null, OperationRequestType.Open);

            // Assert
            act.Should()
                .Throw<ArgumentNullException>()
                .WithMessage("Value cannot be null. (Parameter 'channelOperationRequest')");
        }

        [Fact]
        public void CheckArgumentsAreValid_RequestTypeNotOpen()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                RequestType = OperationRequestType.Close
            };

            // Act
            var act = () => LightningService.CheckArgumentsAreValid(operationRequest, OperationRequestType.Open);

            // Assert
            act.Should()
                .Throw<ArgumentOutOfRangeException>()
                .WithMessage(
                    "Specified argument was out of the range of valid values. (Parameter 'Invalid request. Requested $Close on $Open method')");
        }

        [Fact]
        public async Task OpenChannel_ChannelOperationRequestNotFound()
        {
            // Arrange
            var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
            var channelOperationRequestRepository = new Mock<IChannelOperationRequestRepository>();

            channelOperationRequestRepository
                .Setup(x => x.GetById(It.IsAny<int>()))
                .ReturnsAsync(null as ChannelOperationRequest);

            var lightningService = new LightningService(_logger, channelOperationRequestRepository.Object, null,
                dbContextFactory.Object, null, null, null, new Mock<INBXplorerService>().Object, null);

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
        public void CheckNodesAreValid_SourceNodeNull()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                RequestType = OperationRequestType.Open,
                DestNode = new Node()
            };

            // Act
            var act = () => LightningService.CheckNodesAreValid(operationRequest);

            // Assert
            act.Should()
                .Throw<ArgumentException>()
                .WithMessage("Source or destination null (Parameter 'source')");
        }

        [Fact]
        public void CheckNodesAreValid_DestinationNodeNull()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                RequestType = OperationRequestType.Open,
                SourceNode = new Node()
            };

            // Act
            var act = () => LightningService.CheckNodesAreValid(operationRequest);

            // Assert
            act.Should()
                .Throw<ArgumentException>()
                .WithMessage("Source or destination null (Parameter 'source')");
        }

        [Fact]
        public void CheckNodesAreValid_MacaroonNotSet()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                RequestType = OperationRequestType.Open,
                SourceNode = new Node(),
                DestNode = new Node()
            };

            // Act
            var act = () => LightningService.CheckNodesAreValid(operationRequest);

            // Assert
            act.Should()
                .Throw<UnauthorizedAccessException>()
                .WithMessage("Macaroon not set for source channel");
        }

        [Fact]
        public void CheckNodesAreValid_NodesAreValid()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                RequestType = OperationRequestType.Open,
                SourceNode = new Node()
                {
                    PubKey = "a",
                    ChannelAdminMacaroon = "abc"
                },
                DestNode = new Node()
                {
                    PubKey = "b",
                    ChannelAdminMacaroon = "def"
                }
            };

            // Act
            var result = LightningService.CheckNodesAreValid(operationRequest);

            // Assert
            result.Should().NotBeNull();
            result.Item1.Should().NotBeNull();
            result.Item2.Should().NotBeNull();
        }

        [Fact]
        public void CheckNodesAreValid_SourceEqualsDestination()
        {
            // Arrange
            var node = new Node
            {
                PubKey = "A",
                ChannelAdminMacaroon = "abc"
            };

            var operationRequest = new ChannelOperationRequest
            {
                RequestType = OperationRequestType.Open,
                SourceNode = node,
                DestNode = node
            };

            // Act
            var act = () => LightningService.CheckNodesAreValid(operationRequest);

            // Assert
            act.Should()
                .Throw<ArgumentException>()
                .WithMessage("A node cannot open a channel to itself.");
        }

        [Fact]
        public void GetDerivationStrategyBase_NoDerivationScheme()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                Wallet = new Wallet()
            };

            // Act
            var act = () => LightningService.GetDerivationStrategyBase(operationRequest);

            // Assert
            act.Should()
                .Throw<ArgumentException>()
                .WithMessage("Derivation scheme not found for wallet:0");
        }

        [Fact]
        public void GetDerivationStrategyBase_DerivationSchemeExists()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                Wallet = CreateWallet.MultiSig(_internalWallet)
            };

            // Act
            var result = LightningService.GetDerivationStrategyBase(operationRequest);

            // Assert
            result.Should().NotBeNull();
        }

        [Fact]
        public async Task GetCloseAddress_NoCloseAddress()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                Wallet = CreateWallet.MultiSig(_internalWallet)
            };

            var nbXplorerMock = new Mock<INBXplorerService>();

            nbXplorerMock.Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(),
                    It.IsAny<DerivationFeature>(),
                    It.IsAny<int>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult<KeyPathInformation?>(null));
            // Act
            var act = async () => await LightningService.GetCloseAddress(operationRequest,
                operationRequest.Wallet.GetDerivationStrategy()!,
                nbXplorerMock.Object);

            // Assert
            await act.Should()
                .ThrowAsync<ArgumentException>()
                .WithMessage("Closing address was null for an operation on wallet:0");
        }

        [Fact]
        public async Task GetCloseAddress_CloseAddressExists()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                Wallet = CreateWallet.MultiSig(_internalWallet)
            };

            var nbXplorerMock = GetNBXplorerServiceFullyMocked(new UTXOChanges());

            // Act
            var result = await LightningService.GetCloseAddress(operationRequest,
                operationRequest.Wallet.GetDerivationStrategy()!, nbXplorerMock.Object);

            // Assert
            result.Should().NotBeNull();
        }

        private static Mock<INBXplorerService> GetNBXplorerServiceFullyMocked(UTXOChanges utxoChanges)
        {
            var nbXplorerMock = new Mock<INBXplorerService>();
            //Mock to return a wallet address
            var keyPathInformation = new KeyPathInformation()
                {Address = BitcoinAddress.Create("bcrt1q590shaxaf5u08ml8jwlzghz99dup3z9592vxal", Network.RegTest)};

            nbXplorerMock
                .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), It.IsAny<DerivationFeature>(),
                    It.IsAny<int>(),
                    It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(keyPathInformation);

            nbXplorerMock.Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
                .ReturnsAsync(utxoChanges);


            return nbXplorerMock;
        }

        [Fact]
        public void GetCombinedPsbt_NoCombinedPSBT()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest()
            {
                ChannelOperationRequestPsbts = new List<ChannelOperationRequestPSBT>()
            };

            // Act
            var act = () => LightningService.GetCombinedPsbt(operationRequest);

            // Assert
            act.Should()
                .Throw<ArgumentException>()
                .WithMessage("Invalid PSBT(null) to be used for the channel op request:0 (Parameter 'combinedPSBT')");
        }

        [Fact]
        public void GetCombinedPsbt_CombinedPSBTExists()
        {
            // Arrange
            var channelOpReqPsbts = new List<ChannelOperationRequestPSBT>
            {
                new ChannelOperationRequestPSBT()
                {
                    PSBT =
                        "cHNidP8BAF4BAAAAAdz1PWN8JtwrX5q7aREhJbCmD0I/Xn/m84Znzoo0gPXfAQAAAAD/////AeRcqQAAAAAAIgAgmXpf0mpyCEyKLRK/kCrOwYZpkA3QmJHS6iSocRyj7G4AAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6+zUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn+/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBK2BfqQAAAAAAIgAgVJ3hH2Yg78qcgDmp32ctQUv4oJjoMN3ec6mS0WQX25wBAwQCAAAAAQVpUiECDLxYLw6kodDiOHpRGyavsn3GStmnKi2POJgO1JpkJvAhA50d97FlqJDgPv5UO5W0ngY2C7pY0RIZfxntgg2EDZz7IQPL2Ji2egSgcGTHSj/xC/woKvb/Y0UYit/rjnrxqcih6VOuIgYCDLxYLw6kodDiOHpRGyavsn3GStmnKi2POJgO1JpkJvAYYPOgszAAAIABAACAAQAAgAAAAADHAAAAIgYDnR33sWWokOA+/lQ7lbSeBjYLuljREhl/Ge2CDYQNnPsYH8zk3jAAAIABAACAAQAAgAAAAADHAAAAIgYDy9iYtnoEoHBkx0o/8Qv8KCr2/2NFGIrf64568anIoekY7QIQyDAAAIABAACAAQAAgAAAAADHAAAAAAA="
                }
            };
            var operationRequest = new ChannelOperationRequest()
            {
                ChannelOperationRequestPsbts = channelOpReqPsbts,
                Wallet = CreateWallet.SingleSig(_internalWallet)
            };

            // Act
            var result = LightningService.GetCombinedPsbt(operationRequest);

            // Assert
            result.Should().NotBeNull();
        }

        [Fact]
        public async Task CreateLightningClient_EndpointIsNull()
        {
            // Act
            var act = () => LightningService.CreateLightningClient(null);

            // Assert
            act
                .Should()
                .Throw<ArgumentException>()
                .WithMessage("Endpoint cannot be null");
        }

        [Fact]
        public void CreateLightningClient_ReturnsLightningClient()
        {
            // Act
            var result = LightningService.CreateLightningClient("10.0.0.1");

            // Assert
            result.Should().NotBeNull();
        }

        [Fact]
        public async Task OpenChannel_SuccessLegacyMultiSig()
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
                "cHNidP8BAF4BAAAAATbBST5SSiKvGLoTb4lA6EdP/SSTeOcuOBLAsp3g8ukmAQAAAAD/////AYSRNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15+6mPOhNWykV0mwsneO9FcAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6+zUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn+/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBKwCUNXcAAAAAIgAgs1MYpDJWIIGz/LeRwb5D/c1wgjKmSotvf8QyY3nsEMQiAgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgCMoZhYRPSjEwwlHr+BqCl8E5Meb3hxECwXrRrQ+ltE8CIBEod6GG2AmGCKGtoYRpmrnINYP/OeMeyYBv24X7Z8EiAgEDBAIAAAABBWlSIQLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hAxpTd/JawX43QWk3yFK6wOPpsRK931hHnT2R2BYwsouPU64iBgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI+WNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgMaU3fyWsF+N0FpN8hSusDj6bESvd9YR509kdgWMLKLjxjtAhDIMAAAgAEAAIABAACAAAAAAAAAAAAAAA==";
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

            var wallet = CreateWallet.LegacyMultiSig(_internalWallet);
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
                .Setup(x => x.GetAllManagedByNodeGuard())
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

            var originalCreateLightningClient = LightningService.CreateLightningClient;
            LightningService.CreateLightningClient = (_) => lightningClient;

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
            var channelPoint = new ChannelPoint
            {
                FundingTxidBytes =
                    ByteString.CopyFromUtf8("e59fa8edcd772213239daef2834d9021d1aecc591d605b426ae32c4bec5fdd7d"),
                OutputIndex = 1
            };
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

            // Mock channel repository
            var channelRepository = new Mock<IChannelRepository>();

            channelRepository
                .Setup(x => x.GetByChanId(It.IsAny<ulong>()))
                .ReturnsAsync(() => null);

            //Mock List channels async
            var listChannelsResponse = new ListChannelsResponse
            {
                Channels =
                {
                    new Lnrpc.Channel
                    {
                        Active = true,
                        RemotePubkey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                        ChannelPoint =
                            $"{LightningHelper.DecodeTxId(channelPoint.FundingTxidBytes)}:{channelPoint.OutputIndex}",
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
                channelOperationRequestPsbtRepository.Object,
                channelRepository.Object,
                null,
                GetNBXplorerServiceFullyMocked(utxoChanges).Object,
                null);

            // Act
            var act = async () => await lightningService.OpenChannel(operationRequest);

            // Assert
            await act.Should().NotThrowAsync();

            //TODO Remove hack
            // Cleanup
            LightningService.CreateLightningClient = originalCreateLightningClient;
        }

        [Fact]
        public async Task OpenChannel_SuccessMultiSig()
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
                "cHNidP8BAF4BAAAAAfxbrSOgX+b0TEE/+djT9eYQrMqkbB0oS5eACIYo69ilAQAAAAD/////AYSRNXcAAAAAIgAgknIr2R4V8Bi4hnqXM/qI2ZXEy9MNhs8bc7M8k6KHNCAAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjQAAAACA3rcaovFO7X83IvRJXhrQefWfwPOD5bJ72dtvXpkhIAOv6z6lwqcNxKoocpZKXi/xFyrzRmob/tA5tiZlSX/FRhDtAhDIMAAAgAEAAIAAAAAAAAEBKwCUNXcAAAAAIgAg0KnQhQLDgpnwn8miRsBXVMFC0ribpYvNSiY/lUGGzM4iAgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssUcwRAIgQMEU4f0gB9/Sgiw79s3Ug0BO201upuwiKqoUv6/svesCIGmKmt82DHfnLJsbKD7e2y4xEbc/Z1L/kMMkf4zQXuZLAgEDBAIAAAABBWlSIQLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssSEDAmf/CxGXSG9xiPljcG/e5CXFnnukFn0pJ64Q9U2aNL8hA2/OK1mLPmSxkVJC5GJuM8/inCj45Y6pksEvbHlmsVWpU64iBgLYVMVgz+bATgvrRDQbanlASVXtiUwPt9yCgkQfv2kssRgfzOTeMAAAgAEAAIABAACAAAAAAAAAAAAiBgMCZ/8LEZdIb3GI+WNwb97kJcWee6QWfSknrhD1TZo0vxhg86CzMAAAgAEAAIABAACAAAAAAAAAAAAiBgNvzitZiz5ksZFSQuRibjPP4pwo+OWOqZLBL2x5ZrFVqRjtAhDIMAAAgAEAAIAAAAAAAAAAAAAAAAAAAA==";
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

            var wallet = CreateWallet.MultiSig(_internalWallet);
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
                .Setup(x => x.GetAllManagedByNodeGuard())
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

            var originalCreateLightningClient = LightningService.CreateLightningClient;
            LightningService.CreateLightningClient = (_) => lightningClient;

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
            var channelPoint = new ChannelPoint
            {
                FundingTxidBytes =
                    ByteString.CopyFromUtf8("e59fa8edcd772213239daef2834d9021d1aecc591d605b426ae32c4bec5fdd7d"),
                OutputIndex = 1
            };
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

            // Mock channel repository
            var channelRepository = new Mock<IChannelRepository>();

            channelRepository
                .Setup(x => x.GetByChanId(It.IsAny<ulong>()))
                .ReturnsAsync(() => null);

            //Mock List channels async
            var listChannelsResponse = new ListChannelsResponse
            {
                Channels =
                {
                    new Lnrpc.Channel
                    {
                        Active = true,
                        RemotePubkey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                        ChannelPoint =
                            $"{LightningHelper.DecodeTxId(channelPoint.FundingTxidBytes)}:{channelPoint.OutputIndex}",
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
                channelOperationRequestPsbtRepository.Object,
                channelRepository.Object,
                null,
                GetNBXplorerServiceFullyMocked(utxoChanges).Object,
                null);

            // Act
            var act = async () => await lightningService.OpenChannel(operationRequest);

            // Assert
            await act.Should().NotThrowAsync();

            //TODO Remove hack
            LightningService.CreateLightningClient = originalCreateLightningClient;
        }

        [Fact]
        public async Task OpenChannel_SuccessSingleSigBip39()
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
            //Took from Sparrow regtest
            var userSignedPSBT =
                "cHNidP8BAFICAAAAAfv+UUX9fRDASjtuFRFJIOTNu2JGP03ryLL4FmuJvHKaAAAAAAD9////ATKGAQAAAAAAFgAUPLrJoO1BWDB57NBnuvQEvhhC5jZGxgMATwEENYfPAzJGy4aAAAAAnFf4C7iwqBd3OhhHLTSnqy798jxPlzUZXIwdz3bSN5ICxVWbhbTl5YhGHd2pqRshzBM912xQzhNfks/rTi8W86cQH8zk3lQAAIABAACAAAAAgAABAHEBAAAAAaYagZjaGB68XpvC2hF/r0P9m0w1q/mjN5MSmCarzrj1AAAAAAD/////AqCGAQAAAAAAFgAUWTWFizT3fume3E4IphGbacJMSeOb9ZsCAAAAABYAFLvKMZ7jyVFpEKSfu9/YXU5aWPwKAAAAAAEBH6CGAQAAAAAAFgAUWTWFizT3fume3E4IphGbacJMSeMBAwQBAAAAIgYCpX8qwQjecohk9qD6kv9PzNztGlU+nLvdsQGihjqNMX0YH8zk3lQAAIABAACAAAAAgAAAAAAAAAAAACICAlSCuCZkq7Cc8MJOHGG41ZsFq9DlVmZtyIRsRN3/oeNgGB/M5N5UAACAAQAAgAAAAIAAAAAAAQAAAAA=";
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

            var wallet = CreateWallet.BIP39Singlesig();
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
                .Setup(x => x.GetAllManagedByNodeGuard())
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

            var originalCreateLightningClient = LightningService.CreateLightningClient;
            LightningService.CreateLightningClient = (_) => lightningClient;

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
            var channelPoint = new ChannelPoint
            {
                FundingTxidBytes =
                    ByteString.CopyFromUtf8("e59fa8edcd772213239daef2834d9021d1aecc591d605b426ae32c4bec5fdd7d"),
                OutputIndex = 1
            };
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

            // Mock channel repository
            var channelRepository = new Mock<IChannelRepository>();

            channelRepository
                .Setup(x => x.GetByChanId(It.IsAny<ulong>()))
                .ReturnsAsync(() => null);

            //Mock List channels async
            var listChannelsResponse = new ListChannelsResponse
            {
                Channels =
                {
                    new Lnrpc.Channel
                    {
                        Active = true,
                        RemotePubkey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                        ChannelPoint =
                            $"{LightningHelper.DecodeTxId(channelPoint.FundingTxidBytes)}:{channelPoint.OutputIndex}",
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
                channelOperationRequestPsbtRepository.Object,
                channelRepository.Object,
                null,
                GetNBXplorerServiceFullyMocked(utxoChanges).Object,
                null);

            // Act
            var act = async () => await lightningService.OpenChannel(operationRequest);

            // Assert
            await act.Should().NotThrowAsync();

            //TODO Remove hack
            LightningService.CreateLightningClient = originalCreateLightningClient;
        }

        [Fact]
        public async Task OpenChannel_SuccessSingleSig()
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
                "cHNidP8BAFIBAAAAAeh7YDXyZE11vXb0yRqCkrxY7VpHH1WVMHwaCWYMv/pCAQAAAAD/////AUjf9QUAAAAAFgAULTCtUNMojFQZ8oa6fpbXbDhK2EYAAAAATwEENYfPA325Ro0AAAABg9H86IDUttPPFss+9te+0DByQgbeD7RPXNuVH9mh1qIDnMEWyKA+kvyG038on8+HxI+9AD8r6ZI1dNIDSGC8824Q7QIQyDAAAIABAACAAQAAAAABAR8A4fUFAAAAABYAFOk69QEyo0x+Xs/zV62OLrHh9eszAQMEAgAAAAAA";
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

            var wallet = CreateWallet.SingleSig(_internalWallet);
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
                .Setup(x => x.GetAllManagedByNodeGuard())
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

            var originalCreateLightningClient = LightningService.CreateLightningClient;
            LightningService.CreateLightningClient = (_) => lightningClient;

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
            var channelPoint = new ChannelPoint
            {
                FundingTxidBytes =
                    ByteString.CopyFromUtf8("e59fa8edcd772213239daef2834d9021d1aecc591d605b426ae32c4bec5fdd7d"),
                OutputIndex = 1
            };
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

            // Mock channel repository
            var channelRepository = new Mock<IChannelRepository>();

            channelRepository
                .Setup(x => x.GetByChanId(It.IsAny<ulong>()))
                .ReturnsAsync(() => null);

            //Mock List channels async
            var listChannelsResponse = new ListChannelsResponse
            {
                Channels =
                {
                    new Lnrpc.Channel
                    {
                        Active = true,
                        RemotePubkey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                        ChannelPoint =
                            $"{LightningHelper.DecodeTxId(channelPoint.FundingTxidBytes)}:{channelPoint.OutputIndex}",
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
                channelOperationRequestPsbtRepository.Object,
                channelRepository.Object,
                null,
                GetNBXplorerServiceFullyMocked(utxoChanges).Object,
                null);

            // Act
            var act = async () => await lightningService.OpenChannel(operationRequest);

            // Assert
            await act.Should().NotThrowAsync();

            //TODO Remove hack
            LightningService.CreateLightningClient = originalCreateLightningClient;
        }

        [Fact]
        public async Task CloseChannel_Succeeds()
        {
            // Arrange
            var operationRequest = new ChannelOperationRequest
            {
                RequestType = OperationRequestType.Close,
                ChannelId = 1,
                SourceNode = new Node()
                {
                    PubKey = "03b48034270e522e4033afdbe43383d66d426638927b940d09a8a7a0de4d96e807",
                    ChannelAdminMacaroon = "abc",
                    Endpoint = "10.0.0.1"
                },
            };

            var options = new DbContextOptionsBuilder<ApplicationDbContext>().Options;
            var context = new ApplicationDbContext(options);
            var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
            var channelRepository = new Mock<IChannelRepository>();
            var channelOperationRequestRepository = new Mock<IChannelOperationRequestRepository>();
            dbContextFactory
                .Setup(x => x.CreateDbContextAsync(default))
                .ReturnsAsync(context);
            channelRepository
                .Setup(x => x.GetById(It.IsAny<int>()))
                .ReturnsAsync(new Channel()
                {
                    FundingTx = "abc",
                    FundingTxOutputIndex = 0
                });
            channelRepository
                .Setup(x => x.Update(It.IsAny<Channel>()))
                .Returns((true, null));
            channelOperationRequestRepository
                .Setup(x => x.Update(It.IsAny<ChannelOperationRequest>()))
                .Returns((true, null));

            var noneUpdate = new CloseStatusUpdate();
            var closePendingUpdate = new CloseStatusUpdate
            {
                ClosePending = new PendingUpdate()
            };
            var chanCloseUpdate = new CloseStatusUpdate
            {
                ChanClose = new ChannelCloseUpdate()
                {
                    Success = true,
                }
            };

            var lightningClient = Interceptor.For<Lightning.LightningClient>()
                .Setup(x => x.CloseChannel(
                    Arg.Ignore<CloseChannelRequest>(),
                    Arg.Ignore<Metadata>(),
                    null,
                    Arg.Ignore<CancellationToken>()
                ))
                .Returns(MockHelpers.CreateAsyncServerStreamingCall(new List<CloseStatusUpdate>()
                {
                    noneUpdate,
                    closePendingUpdate,
                    chanCloseUpdate,
                }));

            var originalCreateLightningClient = LightningService.CreateLightningClient;
            LightningService.CreateLightningClient = (_) => lightningClient;

            var lightningService = new LightningService(_logger,
                channelOperationRequestRepository.Object,
                null,
                null,
                null,
                channelRepository.Object,
                null,
                null,
                null);

            // Act
            var act = async () => await lightningService.CloseChannel(operationRequest);

            // Assert
            await act.Should().NotThrowAsync();

            //TODO Remove hack
            LightningService.CreateLightningClient = originalCreateLightningClient;
        }
    }
}