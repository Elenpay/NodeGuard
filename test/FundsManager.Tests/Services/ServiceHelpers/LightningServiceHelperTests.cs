using FluentAssertions;
using FundsManager.Data;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace FundsManager.Services.ServiceHelpers;

public class LightningServiceHelperTests
{
    ILogger<LightningService> _logger = new Mock<ILogger<LightningService>>().Object;
    public static Mock<INBXplorerService> GetNBXplorerServiceFullyMocked(UTXOChanges utxoChanges)
    {
        var nbXplorerMock = new Mock<INBXplorerService>();
        //Mock to return a wallet address
        var keyPathInformation = new KeyPathInformation()
            { Address = BitcoinAddress.Create("bcrt1q590shaxaf5u08ml8jwlzghz99dup3z9592vxal", Network.RegTest) };

        nbXplorerMock
            .Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), It.IsAny<DerivationFeature>(),
                It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(keyPathInformation);

        nbXplorerMock.Setup(x => x.GetUTXOsAsync(It.IsAny<DerivationStrategyBase>(), default))
            .ReturnsAsync(utxoChanges);


        return nbXplorerMock;
    }
    
    [Fact]
    public void CheckArgumentsAreValid_ArgumentNull()
    {
        // Act
        var act = () => LightningServiceHelper.CheckArgumentsAreValid(null, OperationRequestType.Open);

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
        var act = () => LightningServiceHelper.CheckArgumentsAreValid(operationRequest, OperationRequestType.Open);

        // Assert
        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithMessage(
                "Specified argument was out of the range of valid values. (Parameter 'Invalid request. Requested $Close on $Open method')");
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
        var act = () => LightningServiceHelper.CheckNodesAreValid(operationRequest);

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
        var act = () => LightningServiceHelper.CheckNodesAreValid(operationRequest);

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
        var act = () => LightningServiceHelper.CheckNodesAreValid(operationRequest);

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
        var result = LightningServiceHelper.CheckNodesAreValid(operationRequest);

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
        var act = () => LightningServiceHelper.CheckNodesAreValid(operationRequest);

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
        var act = () => LightningServiceHelper.GetDerivationStrategyBase(operationRequest);

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
            Wallet = CreateWallet.CreateTestWallet()
        };

        // Act
        var result = LightningServiceHelper.GetDerivationStrategyBase(operationRequest);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetCloseAddress_NoCloseAddress()
    {
        // Arrange
        var operationRequest = new ChannelOperationRequest
        {
            Wallet = CreateWallet.CreateTestWallet()
        };

        var nbXplorerMock = new Mock<INBXplorerService>();

        nbXplorerMock.Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(),
                It.IsAny<DerivationFeature>(),
                It.IsAny<int>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<KeyPathInformation?>(null));
        // Act
        var act = async () => await LightningServiceHelper.GetCloseAddress(operationRequest,
            operationRequest.Wallet.GetDerivationStrategy()!,
            nbXplorerMock.Object);

        // Assert
        await act.Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("Closing address was null for an operation on wallet:0");
    }

    [Fact]
    public async void GetCloseAddress_CloseAddressExists()
    {
        // Arrange
        var operationRequest = new ChannelOperationRequest
        {
            Wallet = CreateWallet.CreateTestWallet()
        };

        var nbXplorerMock = GetNBXplorerServiceFullyMocked(new UTXOChanges());

        // Act
        var result = await LightningServiceHelper.GetCloseAddress(operationRequest,
            operationRequest.Wallet.GetDerivationStrategy()!, nbXplorerMock.Object);

        // Assert
        result.Should().NotBeNull();
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
        var act = () => LightningServiceHelper.GetCombinedPsbt(operationRequest);

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
            ChannelOperationRequestPsbts = channelOpReqPsbts
        };

        // Act
        var result = LightningServiceHelper.GetCombinedPsbt(operationRequest);

        // Assert
        result.Should().NotBeNull();
    }
}