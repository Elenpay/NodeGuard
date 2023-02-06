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

using AutoFixture;
using AutoMapper;
using Microsoft.Extensions.Logging;
using FundsManager.Data;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.TestHelpers;
using NBXplorer;
using NBXplorer.Models;
using FluentAssertions;
using FundsManager.Helpers;
using Google.Protobuf;
using Lnrpc;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Unmockable.Exceptions;

namespace FundsManager.Services
{
    public class LightningServiceTests
    {
        ILogger<LightningService> logger = new Mock<ILogger<LightningService>>().Object;
    
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
                .WithMessage("Specified argument was out of the range of valid values. (Parameter 'Invalid request. Requested $Close on $Open method')");
        }

        [Fact]
        public async void OpenChannel_ChannelOperationRequestNotFound()
        {
            // Arrange
            var dbContextFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
            var channelOperationRequestRepository = new Mock<IChannelOperationRequestRepository>();

            channelOperationRequestRepository
                .Setup(x => x.GetById(It.IsAny<int>()))
                .ReturnsAsync(null as ChannelOperationRequest);

            var lightningService = new LightningService(logger, channelOperationRequestRepository.Object, null, dbContextFactory.Object, null, null, null, null, null, null);

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
            Environment.SetEnvironmentVariable("DEFAULT_DERIVATION_PATH", "m/48'/1'/1'");
            var operationRequest = new ChannelOperationRequest
            {
                Wallet = CreateWallet.CreateTestWallet() 
            };
            
            // Act
            var result = LightningService.GetDerivationStrategyBase(operationRequest);
            
            // Assert
            result.Should().NotBeNull();
        }
        
        [Fact]
        public async void GetCloseAddress_NoCloseAddress()
        {
            // Arrange
            Environment.SetEnvironmentVariable("DEFAULT_DERIVATION_PATH", "m/48'/1'/1'");
            var operationRequest = new ChannelOperationRequest
            {
                Wallet = CreateWallet.CreateTestWallet()
            };

            var explorerClient = Interceptor.For<ExplorerClient>()
                .Setup(x => x.GetUnusedAsync(
                    Arg.Ignore<DerivationStrategyBase>(),
                    Arg.Ignore<DerivationFeature>(),
                    Arg.Ignore<int>(),
                    Arg.Ignore<bool>(),
                    Arg.Ignore<CancellationToken>()
                ))
                .Returns(null);
            
            // Act
            var act = async () => await LightningService.GetCloseAddress(operationRequest, operationRequest.Wallet.GetDerivationStrategy()!, explorerClient.As<IUnmockable<ExplorerClient>>());
            
            // Assert
            await act.Should()
                .ThrowAsync<ArgumentException>()
                .WithMessage("Closing address was null for an operation on wallet:0");
            explorerClient.Verify();
        }

        [Fact]
        public async void GetCloseAddress_CloseAddressExists()
        {
            // Arrange
            Environment.SetEnvironmentVariable("DEFAULT_DERIVATION_PATH", "m/48'/1'/1'");
            var operationRequest = new ChannelOperationRequest
            {
                Wallet = CreateWallet.CreateTestWallet()
            };

            var explorerClient = Interceptor.For<ExplorerClient>()
                .Setup(x => x.GetUnusedAsync(
                    Arg.Ignore<DerivationStrategyBase>(),
                    Arg.Ignore<DerivationFeature>(),
                    Arg.Ignore<int>(),
                    Arg.Ignore<bool>(),
                    Arg.Ignore<CancellationToken>()
                ))
                .Returns(new KeyPathInformation());
            
            // Act
            var result = await LightningService.GetCloseAddress(operationRequest,
                operationRequest.Wallet.GetDerivationStrategy()!, explorerClient.As<IUnmockable<ExplorerClient>>());
            
            // Assert
            result.Should().NotBeNull();
            explorerClient.Verify();
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
            var channelOpReqPsbts = new List<ChannelOperationRequestPSBT>();
            channelOpReqPsbts.Add(new ChannelOperationRequestPSBT()
            {
                PSBT = "cHNidP8BAF4BAAAAAdz1PWN8JtwrX5q7aREhJbCmD0I/Xn/m84Znzoo0gPXfAQAAAAD/////AeRcqQAAAAAAIgAgmXpf0mpyCEyKLRK/kCrOwYZpkA3QmJHS6iSocRyj7G4AAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6+zUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn+/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBK2BfqQAAAAAAIgAgVJ3hH2Yg78qcgDmp32ctQUv4oJjoMN3ec6mS0WQX25wBAwQCAAAAAQVpUiECDLxYLw6kodDiOHpRGyavsn3GStmnKi2POJgO1JpkJvAhA50d97FlqJDgPv5UO5W0ngY2C7pY0RIZfxntgg2EDZz7IQPL2Ji2egSgcGTHSj/xC/woKvb/Y0UYit/rjnrxqcih6VOuIgYCDLxYLw6kodDiOHpRGyavsn3GStmnKi2POJgO1JpkJvAYYPOgszAAAIABAACAAQAAgAAAAADHAAAAIgYDnR33sWWokOA+/lQ7lbSeBjYLuljREhl/Ge2CDYQNnPsYH8zk3jAAAIABAACAAQAAgAAAAADHAAAAIgYDy9iYtnoEoHBkx0o/8Qv8KCr2/2NFGIrf64568anIoekY7QIQyDAAAIABAACAAQAAgAAAAADHAAAAAAA="
            }); 
            var operationRequest = new ChannelOperationRequest()
            {
                ChannelOperationRequestPsbts = channelOpReqPsbts
            };

            // Act
            var result = LightningService.GetCombinedPsbt(operationRequest);
            
            // Assert
            result.Should().NotBeNull();
        }
        
        [Fact]
        public void CreateLightningClient_EndpointIsNull()
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
        public async void OpenChannel_Success()
        {
            // Arrange
            Environment.SetEnvironmentVariable("DEFAULT_DERIVATION_PATH", "m/48'/1'/1'");
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
            var psbt =
                "cHNidP8BAF4BAAAAAY2SvXNoGcttP+4LFeTIdsPXuJWUF3XjjJ6F6hqBrO7wAAAAAAD/////AehANXcAAAAAIgAgg+aofANl6wKKByTgFl5yBnqUK8f7sn4ULhAAIJb1C0cAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6+zUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn+/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBK2RDNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15+6mPOhNWykV0mwsneO9FcBAwQCAAAAAQVpUiEDJ0KjWjE9mM+OzBPgqobBDCvXwdbEERr2wUJD2ruduAYhA4E0C5IanGt26wU7pzZoyLG+FhLBa8k8HdwR/2T0uCpKIQPdbg/RGEZcyAU+sjVNkti/8LzgNP3puKHGjP/73jrTdlOuIgYDJ0KjWjE9mM+OzBPgqobBDCvXwdbEERr2wUJD2ruduAYYH8zk3jAAAIABAACAAQAAgAEAAAAAAAAAIgYDgTQLkhqca3brBTunNmjIsb4WEsFryTwd3BH/ZPS4KkoY7QIQyDAAAIABAACAAQAAgAEAAAAAAAAAIgYD3W4P0RhGXMgFPrI1TZLYv/C84DT96bihxoz/+94603YYYPOgszAAAIABAACAAQAAgAEAAAAAAAAAAAA=";
            channelOpReqPsbts.Add(new ChannelOperationRequestPSBT()
            {
                PSBT = psbt,
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
            
            var explorerClient = Interceptor.For<ExplorerClient>()
                .Setup(x => x.GetUnusedAsync(
                    Arg.Ignore<DerivationStrategyBase>(),
                    Arg.Ignore<DerivationFeature>(),
                    Arg.Ignore<int>(),
                    Arg.Ignore<bool>(),
                    Arg.Ignore<CancellationToken>()
                ))
                .Returns(new KeyPathInformation() { Address = BitcoinAddress.Create("bcrt1q590shaxaf5u08ml8jwlzghz99dup3z9592vxal", Network.RegTest) });
            
            LightningHelper.GenerateNetwork = () => (Network.RegTest, explorerClient);

            var nodes = new List<Node>();
            nodes.Add(destinationNode);
            
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
                                Addresses = { new NodeAddress()
                                {
                                    Network = "tcp",
                                    Addr = "10.0.0.2"
                                } }
                            }
                        }));
            
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
            var chanOpenUpdate = new OpenStatusUpdate
            {
                ChanOpen = new ChannelOpenUpdate()
                {
                    ChannelPoint = new ChannelPoint()
                }
            };
            var psbt2 =
                "cHNidP8BAF4BAAAAAY2SvXNoGcttP+4LFeTIdsPXuJWUF3XjjJ6F6hqBrO7wAAAAAAD/////AehANXcAAAAAIgAgg+aofANl6wKKByTgFl5yBnqUK8f7sn4ULhAAIJb1C0cAAAAATwEENYfPAy8RJCyAAAAB/DvuQjoBjOttImoGYyiO0Pte4PqdeQqzcNAw4Ecw5sgDgI4uHNSCvdBxlpQ8WoEz0WmvhgIra7A4F3FkTsB0RNcQH8zk3jAAAIABAACAAQAAgE8BBDWHzwNWrAP0gAAAAfkIrkpmsP+hqxS1WvDOSPKnAiXLkBCQLWkBr5C5Po+BAlGvFeBbuLfqwYlbP19H/+/s2DIaAu8iKY+J0KIDffBgEGDzoLMwAACAAQAAgAEAAIBPAQQ1h88DfblGjYAAAAH1InDHaHo6+zUe9PG5owwQ87bTkhcGg66pSIwTmhHJmAMiI4UjOOpn+/2Nw1KrJiXnmid2RiEja/HAITCQ00ienxDtAhDIMAAAgAEAAIABAACAAAEBK2RDNXcAAAAAIgAguNLINpkV//IIFd1ti2ig15+6mPOhNWykV0mwsneO9FciAgMnQqNaMT2Yz47ME+CqhsEMK9fB1sQRGvbBQkPau524BkcwRAIgDyqyP4zAWCDmH1AU1oTR7+xC7qigIyxNL1PH/OsNB4cCIFPySCKzpvXsmSoB7XlhDWf+96DmZC+jRck5Eo/+hw14AgEDBAIAAAABBWlSIQMnQqNaMT2Yz47ME+CqhsEMK9fB1sQRGvbBQkPau524BiEDgTQLkhqca3brBTunNmjIsb4WEsFryTwd3BH/ZPS4KkohA91uD9EYRlzIBT6yNU2S2L/wvOA0/em4ocaM//veOtN2U64iBgMnQqNaMT2Yz47ME+CqhsEMK9fB1sQRGvbBQkPau524BhgfzOTeMAAAgAEAAIABAACAAQAAAAAAAAAiBgOBNAuSGpxrdusFO6c2aMixvhYSwWvJPB3cEf9k9LgqShjtAhDIMAAAgAEAAIABAACAAQAAAAAAAAAiBgPdbg/RGEZcyAU+sjVNkti/8LzgNP3puKHGjP/73jrTdhhg86CzMAAAgAEAAIABAACAAQAAAAAAAAAAAA==";
            var psbtFundUpdate = new OpenStatusUpdate
            {
                PsbtFund = new ReadyForPsbtFunding()
                {
                    Psbt = ByteString.FromBase64(psbt2)
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

            var psbt3 = "cHNidP8BAIkBAAAAAYPmRU9UfOb7jPxzNbVTsZHVsj/vIRBCItB7L0L8sxLhAAAAAAD/////AiyiNHcAAAAAIgAgU9+5n7rk7vLG5T/1ayQ8XHRllAmukKFB/lVCj6HYCp0gTgAAAAAAACIAIAjA2QfEQoCuNRq9fovDi71fO4wPu9U0KS7hbkX+YHKiAAAAAAABASvI8jR3AAAAACIAIJffgj8CDyfDGIRCy1ydaXmsoDphWy2Wd68mRGjxQ1RXAQcAAQj8BABHMEQCIClOzdLaGSnwhiNgaCrNIaLOgTePaJchHw6iStN/otXwAiB4wz5Zi8OHMrZnC3uul9sP5O6PDbmnHe45QGrAoZmN/QJHMEQCIAg0m3d7SlNkN7Azuoa2Zo0/uEpMVWW0T4Et8mBgbMAPAiBtjA4gj2zjEf8AUBL+ZkWWbwXLzHUwvOHILrpGRmdUDAFpUiECWxgQF6MCi5XmifnPoWwG/WNb5ri4Jn8OAP/yDdj8h6YhAwezdUNwfq5E79kVGVbgkZPLL1y/Vr9VRZ5scZsZEfImIQMzdluf+gycXqYHZth1NVXJ3DBc4xW+0/ziWD7b3krnB1OuAAAA";
            var finalizedPsbt = PSBT.Parse(psbt3, Network.RegTest);
            // var channelfundingTx = fundedPsbt.GetGlobalTransaction();
            // var input = channelfundingTx.Inputs.FirstOrDefault();
            // var utxoChanges = new UTXOChanges();
            // var key = wallet.Keys.FirstOrDefault();
            // var fixture = new Fixture();
            // var utxoList = new List<UTXO>()
            // {
            //     new UTXO()
            //     {
            //         Value = new Money(10),
            //         Outpoint = input.PrevOut,
            //         Index = fixture.Create<int>(),
            //         ScriptPubKey = new Script(),
            //         KeyPath = new KeyPath(key.Path) 
            //     }
            // };
            //
            // utxoChanges.Confirmed = new UTXOChange() { UTXOs = utxoList };
            // explorerClient
            //     .Setup(x => x.GetUTXOsAsync(Arg.Ignore<DerivationStrategyBase>(), Arg.Ignore<CancellationToken>()))
            //     .Returns(utxoChanges);
             
            LightningService.SignPSBT = (_, _, _, _, _, _, _) => Task.FromResult(finalizedPsbt);

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
            
            var lightningService = new LightningService(logger, channelOperationRequestRepository.Object, nodeRepository.Object, dbContextFactory.Object, null, null, null, channelOperationRequestPsbtRepository.Object, null, null);
            
            // Act
            var act = async () => await lightningService.OpenChannel(operationRequest);
            
            // Assert
            await act.Should().NotThrowAsync();
        }
    }
}