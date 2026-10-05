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
using Lnrpc;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using NodeGuard.Services.Spark;
using NodeGuard.TestHelpers;
using Quartz;

namespace NodeGuard.Jobs;

public class AutoLiquidityManagementJobTests
{
    private Mock<ILogger<AutoLiquidityManagementJob>> _loggerMock;
    private Mock<INodeRepository> _nodeRepositoryMock;
    private Mock<ISwapOutRepository> _swapOutRepositoryMock;
    private Mock<ISwapsService> _swapsServiceMock;
    private Mock<IFortySwapService> _fortySwapServiceMock;
    private Mock<ILightningService> _lightningServiceMock;
    private Mock<IWalletRepository> _walletRepositoryMock;
    private Mock<INBXplorerService> _nbXplorerServiceMock;
    private Mock<IAuditService> _auditServiceMock;
    private Mock<ISparkWalletService> _sparkWalletMock;
    private AutoLiquidityManagementJob _autoLiquidityManagementJob;

    public AutoLiquidityManagementJobTests()
    {
        _loggerMock = new Mock<ILogger<AutoLiquidityManagementJob>>();
        _nodeRepositoryMock = new Mock<INodeRepository>();
        _swapOutRepositoryMock = new Mock<ISwapOutRepository>();
        _swapsServiceMock = new Mock<ISwapsService>();
        _fortySwapServiceMock = new Mock<IFortySwapService>();
        _lightningServiceMock = new Mock<ILightningService>();
        _walletRepositoryMock = new Mock<IWalletRepository>();
        _nbXplorerServiceMock = new Mock<INBXplorerService>();
        _auditServiceMock = new Mock<IAuditService>();
        _sparkWalletMock = new Mock<ISparkWalletService>();
        _sparkWalletMock.SetupGet(x => x.Status).Returns(new SparkWalletStatus(SparkWalletState.Ready, "02spark"));

        _autoLiquidityManagementJob = new AutoLiquidityManagementJob(
            _loggerMock.Object,
            _nodeRepositoryMock.Object,
            _swapOutRepositoryMock.Object,
            _swapsServiceMock.Object,
            _fortySwapServiceMock.Object,
            _lightningServiceMock.Object,
            _walletRepositoryMock.Object,
            _nbXplorerServiceMock.Object,
            _auditServiceMock.Object,
            _sparkWalletMock.Object
        );
    }

    private Node CreateTestNode(
        bool autoLiquidityEnabled = true,
        bool isDisabled = false,
        int? destinationWalletId = 1,
        long minSwapSats = 1_000_000,
        long maxSwapSats = 25_000_000,
        int maxSwapsInFlight = 5,
        decimal maxSwapFeeRatio = 0.01m,
        long minBalanceThresholdSats = 100_000_000,
        long swapBudgetSats = 50_000_000,
        TimeSpan? swapBudgetRefreshInterval = null,
        DateTimeOffset? swapBudgetStartDatetime = null)
    {
        return new Node
        {
            Id = 1,
            Name = "TestNode",
            PubKey = "test-pubkey",
            IsNodeDisabled = isDisabled,
            AutoLiquidityManagementEnabled = autoLiquidityEnabled,
            FundsDestinationWalletId = destinationWalletId,
            SwapMinAmountSats = minSwapSats,
            SwapMaxAmountSats = maxSwapSats,
            MaxSwapsInFlight = maxSwapsInFlight,
            MaxSwapRoutingFeeRatio = maxSwapFeeRatio,
            MinimumBalanceThresholdSats = minBalanceThresholdSats,
            SwapBudgetSats = swapBudgetSats,
            SwapBudgetRefreshInterval = swapBudgetRefreshInterval ?? TimeSpan.FromDays(1),
            SwapBudgetStartDatetime = swapBudgetStartDatetime
        };
    }

    private Wallet CreateTestWallet()
    {
        // Create a simple hot wallet with a single key for testing
        var internalWallet = CreateWallet.CreateInternalWallet();
        var wallet = CreateWallet.SingleSig(internalWallet);
        wallet.Id = 1; // Match the node's FundsDestinationWalletId
        return wallet;
    }

    #region Budget Refresh Tests

    [Fact]
    public async Task ProcessNodeAsync_RefreshesBudget_WhenPeriodExpired()
    {
        // Arrange
        var oldStartDate = DateTimeOffset.UtcNow.AddDays(-2);
        var node = CreateTestNode(swapBudgetStartDatetime: oldStartDate);

        var channelBalance = new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = 50_000_000 } // Below threshold, won't proceed with swap
        };
        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(channelBalance);

        // Act
        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        // Assert - Should return BalanceBelowThreshold and update budget start date
        result.Should().Be(ManageNodeLiquidityResult.BalanceBelowThreshold);
        _nodeRepositoryMock.Verify(x => x.Update(It.Is<Node>(n =>
            n.SwapBudgetStartDatetime.HasValue &&
            n.SwapBudgetStartDatetime.Value > oldStartDate)), Times.Once);
    }

    [Fact]
    public async Task ProcessNodeAsync_DoesNotRefreshBudget_WhenPeriodNotExpired()
    {
        // Arrange
        var recentStartDate = DateTimeOffset.UtcNow.AddHours(-12);
        var node = CreateTestNode(swapBudgetStartDatetime: recentStartDate);

        var channelBalance = new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = 50_000_000 } // Below threshold
        };
        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(channelBalance);

        // Act
        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        // Assert - Should return BalanceBelowThreshold and not update budget start date
        result.Should().Be(ManageNodeLiquidityResult.BalanceBelowThreshold);
        _nodeRepositoryMock.Verify(x => x.Update(It.IsAny<Node>()), Times.Never);
    }

    #endregion

    #region Balance Threshold Tests

    [Fact]
    public async Task ProcessNodeAsync_SkipsSwap_WhenBalanceBelowThreshold()
    {
        // Arrange
        var node = CreateTestNode(minBalanceThresholdSats: 100_000_000);

        var channelBalance = new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = 50_000_000 } // Below 100M threshold
        };
        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(channelBalance);

        // Act
        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        // Assert
        result.Should().Be(ManageNodeLiquidityResult.BalanceBelowThreshold);
    }

    #endregion

    #region Optimistic Locking Tests

    [Fact]
    public async Task ProcessNodeAsync_SkipsSwap_WhenMaxSwapsInFlightReached()
    {
        // Arrange
        var node = CreateTestNode(maxSwapsInFlight: 2);

        var channelBalance = new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = 150_000_000 } // Above threshold
        };
        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(channelBalance);

        // Mock 2 in-flight swaps (max reached)
        var inFlightSwaps = new List<SwapOut>
        {
            new SwapOut { Id = 1, Status = SwapOutStatus.Pending },
            new SwapOut { Id = 2, Status = SwapOutStatus.Pending }
        };
        _swapOutRepositoryMock.Setup(x => x.GetInFlightSwapsByNode(node.Id))
            .ReturnsAsync(inFlightSwaps);

        // Act
        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        // Assert
        result.Should().Be(ManageNodeLiquidityResult.MaxSwapsInFlightReached);
    }


    #endregion

    #region Budget Tracking Tests

    [Fact]
    public async Task ProcessNodeAsync_SkipsSwap_WhenBudgetExhausted()
    {
        // Arrange
        var node = CreateTestNode(swapBudgetSats: 10_000_000); // 10M budget

        var channelBalance = new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = 150_000_000 } // Above threshold
        };
        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(channelBalance);

        _swapOutRepositoryMock.Setup(x => x.GetInFlightSwapsByNode(node.Id))
            .ReturnsAsync(new List<SwapOut>());

        // Mock consumed budget = 10M (budget exhausted)
        _swapOutRepositoryMock.Setup(x => x.GetConsumedFeesSince(node.Id, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new Money(10_000_000, MoneyUnit.Satoshi));

        // Act
        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        // Assert
        result.Should().Be(ManageNodeLiquidityResult.BudgetExhausted);
    }

    #endregion

    #region Swap Amount Calculation Tests

    [Fact]
    public async Task ProcessNodeAsync_SkipsSwap_WhenExcessBalanceBelowMinimum()
    {
        // Arrange
        var node = CreateTestNode(
            minSwapSats: 10_000_000, // 10M min
            minBalanceThresholdSats: 100_000_000,
            swapBudgetSats: 50_000_000); // Plenty of fee budget

        // Only 5M excess (105M - 100M threshold), less than 10M minimum
        var channelBalance = new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = 105_000_000 }
        };
        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(channelBalance);

        _swapOutRepositoryMock.Setup(x => x.GetInFlightSwapsByNode(node.Id))
            .ReturnsAsync(new List<SwapOut>());

        _swapOutRepositoryMock.Setup(x => x.GetConsumedFeesSince(node.Id, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new Money(0, MoneyUnit.Satoshi));

        // Act
        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        // Assert
        result.Should().Be(ManageNodeLiquidityResult.ExcessBalanceBelowMinimum);
    }

    #endregion

    #region Happy Path Test

    [Fact]
    public async Task ProcessNodeAsync_HappyPath_CreatesSwapSuccessfully()
    {
        // Arrange - Perfect conditions for a swap
        var node = CreateTestNode(
            minSwapSats: 1_000_000,
            maxSwapSats: 25_000_000,
            maxSwapsInFlight: 5,
            maxSwapFeeRatio: 0.01m,
            minBalanceThresholdSats: 100_000_000,
            swapBudgetSats: 50_000_000,
            swapBudgetStartDatetime: DateTimeOffset.UtcNow.AddHours(-1));
        var wallet = CreateTestWallet();

        // Balance above threshold with 50M excess
        var channelBalance = new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = 150_000_000 }
        };
        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(channelBalance);

        // No in-flight swaps
        _swapOutRepositoryMock.Setup(x => x.GetInFlightSwapsByNode(node.Id))
            .ReturnsAsync(new List<SwapOut>());

        // No consumed budget yet
        _swapOutRepositoryMock.Setup(x => x.GetConsumedFeesSince(node.Id, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new Money(0, MoneyUnit.Satoshi));

        _walletRepositoryMock.Setup(x => x.GetById(node.FundsDestinationWalletId!.Value))
            .ReturnsAsync(wallet);

        var keyPathInfo = new KeyPathInformation
        {
            Address = new NBitcoin.Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, NBitcoin.Network.RegTest)
        };
        _nbXplorerServiceMock.Setup(x => x.GetUnusedAsync(
            It.IsAny<DerivationStrategyBase>(),
            It.IsAny<DerivationFeature>(),
            It.IsAny<int>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(keyPathInfo);

        var swapResponse = new SwapResponse
        {
            Id = "0102030405",
            HtlcAddress = "bcrt1qtest",
            Amount = 25_000_000,
            ServerFee = 30_000,
            OnchainFee = 15_000,
            OffchainFee = 5_000,
            Status = SwapOutStatus.Pending
        };
        _swapsServiceMock.Setup(x => x.CreateSwapOutAsync(
            node, It.Is<SwapOut>(s => s.Provider == SwapProvider.Loop), It.IsAny<SwapOutRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapOutCreation(swapResponse, true, null));

        // Act
        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        // Assert
        result.Should().Be(ManageNodeLiquidityResult.Success);

        // Verify swap was created and recorded with correct parameters
        _swapsServiceMock.Verify(x => x.CreateSwapOutAsync(node, It.Is<SwapOut>(s =>
            s.NodeId == node.Id &&
            s.DestinationWalletId == wallet.Id &&
            s.Provider == SwapProvider.Loop &&
            s.SatsAmount == 25_000_000 &&
            s.IsManual == false),
            It.Is<SwapOutRequest>(r => r.Address == keyPathInfo.Address.ToString() && r.Amount == 25_000_000),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManageNodeLiquidity_WhenTheSwapCannotBeRecorded_ReturnsError()
    {
        // Arrange - Perfect conditions for a swap
        var node = CreateTestNode(
            minSwapSats: 1_000_000,
            maxSwapSats: 25_000_000,
            maxSwapsInFlight: 5,
            maxSwapFeeRatio: 0.01m,
            minBalanceThresholdSats: 100_000_000,
            swapBudgetSats: 50_000_000,
            swapBudgetStartDatetime: DateTimeOffset.UtcNow.AddHours(-1));
        var wallet = CreateTestWallet();

        // Balance above threshold with 50M excess
        var channelBalance = new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = 150_000_000 }
        };
        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(channelBalance);

        // No in-flight swaps
        _swapOutRepositoryMock.Setup(x => x.GetInFlightSwapsByNode(node.Id))
            .ReturnsAsync(new List<SwapOut>());

        // No consumed budget yet
        _swapOutRepositoryMock.Setup(x => x.GetConsumedFeesSince(node.Id, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new Money(0, MoneyUnit.Satoshi));

        _walletRepositoryMock.Setup(x => x.GetById(node.FundsDestinationWalletId!.Value))
            .ReturnsAsync(wallet);

        var keyPathInfo = new KeyPathInformation
        {
            Address = new NBitcoin.Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, NBitcoin.Network.RegTest)
        };
        _nbXplorerServiceMock.Setup(x => x.GetUnusedAsync(
            It.IsAny<DerivationStrategyBase>(),
            It.IsAny<DerivationFeature>(),
            It.IsAny<int>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(keyPathInfo);

        var swapResponse = new SwapResponse
        {
            Id = "0102030405",
            HtlcAddress = "bcrt1qtest",
            Amount = 25_000_000,
            ServerFee = 30_000,
            OnchainFee = 15_000,
            OffchainFee = 5_000,
            Status = SwapOutStatus.Pending
        };
        _swapsServiceMock.Setup(x => x.CreateSwapOutAsync(
            node, It.Is<SwapOut>(s => s.Provider == SwapProvider.Loop), It.IsAny<SwapOutRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapOutCreation(swapResponse, false, "database is down"));

        // Act
        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        // Assert: the provider has the swap but NodeGuard could not record it
        result.Should().Be(ManageNodeLiquidityResult.Error);
    }

    [Fact]
    public async Task ManageNodeLiquidity_WithFortySwapWeight_CreatesSwapOutWithFortySwapProvider()
    {
        // Arrange - Node configured to only use FortySwap (Loop weight 0, FortySwap weight 100)
        var node = CreateTestNode(
            minSwapSats: 1_000_000,
            maxSwapSats: 25_000_000,
            maxSwapsInFlight: 5,
            maxSwapFeeRatio: 0.01m,
            minBalanceThresholdSats: 100_000_000,
            swapBudgetSats: 50_000_000,
            swapBudgetStartDatetime: DateTimeOffset.UtcNow.AddHours(-1));
        node.LoopSwapWeight = 0;
        node.FortySwapWeight = 100;
        var wallet = CreateTestWallet();

        // Balance above threshold with 50M excess
        var channelBalance = new ChannelBalanceResponse
        {
            LocalBalance = new Amount { Sat = 150_000_000 }
        };
        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(channelBalance);

        // No in-flight swaps
        _swapOutRepositoryMock.Setup(x => x.GetInFlightSwapsByNode(node.Id))
            .ReturnsAsync(new List<SwapOut>());

        // No consumed budget yet
        _swapOutRepositoryMock.Setup(x => x.GetConsumedFeesSince(node.Id, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new Money(0, MoneyUnit.Satoshi));

        _walletRepositoryMock.Setup(x => x.GetById(node.FundsDestinationWalletId!.Value))
            .ReturnsAsync(wallet);

        var keyPathInfo = new KeyPathInformation
        {
            Address = new NBitcoin.Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, NBitcoin.Network.RegTest)
        };
        _nbXplorerServiceMock.Setup(x => x.GetUnusedAsync(
            It.IsAny<DerivationStrategyBase>(),
            It.IsAny<DerivationFeature>(),
            It.IsAny<int>(),
            It.IsAny<bool>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(keyPathInfo);

        var swapResponse = new SwapResponse
        {
            Id = "fortyswap-test-id",
            HtlcAddress = "bcrt1qtest",
            Amount = 25_000_000,
            ServerFee = 30_000,
            OnchainFee = 15_000,
            OffchainFee = 5_000,
            Status = SwapOutStatus.Pending
        };
        _swapsServiceMock.Setup(x => x.CreateSwapOutAsync(
            node, It.Is<SwapOut>(s => s.Provider == SwapProvider.FortySwap), It.IsAny<SwapOutRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapOutCreation(swapResponse, true, null));

        // Act
        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        // Assert
        result.Should().Be(ManageNodeLiquidityResult.Success);

        // Verify swap was created with FortySwap provider (not Loop)
        _swapsServiceMock.Verify(x => x.CreateSwapOutAsync(node, It.Is<SwapOut>(s =>
            s.NodeId == node.Id &&
            s.DestinationWalletId == wallet.Id &&
            s.Provider == SwapProvider.FortySwap &&
            s.SatsAmount == 25_000_000 &&
            s.IsManual == false),
            It.Is<SwapOutRequest>(r => r.Address == keyPathInfo.Address.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Spark Provider Tests

    /// <summary>A node with 50M sats above its threshold and no swap in flight: the job will swap 25M.</summary>
    private (Node Node, KeyPathInformation Address) ArrangeSwappableNode(int loopWeight, int fortySwapWeight, int sparkWeight)
    {
        var node = CreateTestNode(swapBudgetStartDatetime: DateTimeOffset.UtcNow.AddHours(-1));
        node.LoopSwapWeight = loopWeight;
        node.FortySwapWeight = fortySwapWeight;
        node.SparkSwapWeight = sparkWeight;

        _lightningServiceMock.Setup(x => x.ChannelBalanceAsync(node))
            .ReturnsAsync(new ChannelBalanceResponse { LocalBalance = new Amount { Sat = 150_000_000 } });
        _swapOutRepositoryMock.Setup(x => x.GetInFlightSwapsByNode(node.Id)).ReturnsAsync(new List<SwapOut>());
        _swapOutRepositoryMock.Setup(x => x.GetConsumedFeesSince(node.Id, It.IsAny<DateTimeOffset>()))
            .ReturnsAsync(new Money(0, MoneyUnit.Satoshi));
        _swapOutRepositoryMock.Setup(x => x.GetAllPending()).ReturnsAsync(new List<SwapOut>());
        _walletRepositoryMock.Setup(x => x.GetById(node.FundsDestinationWalletId!.Value)).ReturnsAsync(CreateTestWallet());

        var address = new KeyPathInformation
        {
            Address = new NBitcoin.Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, NBitcoin.Network.RegTest)
        };
        _nbXplorerServiceMock.Setup(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), It.IsAny<DerivationFeature>(),
                It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(address);
        _swapsServiceMock.Setup(x => x.CreateSwapOutAsync(node, It.IsAny<SwapOut>(), It.IsAny<SwapOutRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SwapOutCreation(new SwapResponse { Id = "swap-1", HtlcAddress = string.Empty, Status = SwapOutStatus.Pending }, true, null));

        return (node, address);
    }

    [Fact]
    public async Task ManageNodeLiquidity_WithSparkWeight_CreatesSwapOutWithSparkProvider()
    {
        var (node, address) = ArrangeSwappableNode(loopWeight: 0, fortySwapWeight: 0, sparkWeight: 100);

        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        result.Should().Be(ManageNodeLiquidityResult.Success);
        _swapsServiceMock.Verify(x => x.CreateSwapOutAsync(node,
            It.Is<SwapOut>(s => s.Provider == SwapProvider.Spark && s.SatsAmount == 25_000_000 && !s.IsManual),
            It.Is<SwapOutRequest>(r => r.Address == address.Address.ToString()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManageNodeLiquidity_WithOnlySparkWeight_WhileASparkSwapIsInFlight_SkipsWithoutReservingAnAddress()
    {
        var (node, _) = ArrangeSwappableNode(loopWeight: 0, fortySwapWeight: 0, sparkWeight: 100);
        _swapOutRepositoryMock.Setup(x => x.GetAllPending())
            .ReturnsAsync(new List<SwapOut> { new() { Id = 9, Provider = SwapProvider.Spark, Status = SwapOutStatus.Pending } });

        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        result.Should().Be(ManageNodeLiquidityResult.NoProviderAvailable);
        _nbXplorerServiceMock.Verify(x => x.GetUnusedAsync(It.IsAny<DerivationStrategyBase>(), It.IsAny<DerivationFeature>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _swapsServiceMock.Verify(x => x.CreateSwapOutAsync(It.IsAny<Node>(), It.IsAny<SwapOut>(), It.IsAny<SwapOutRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManageNodeLiquidity_WhenSparkIsUnavailable_UsesTheOtherWeightedProviders()
    {
        var (node, _) = ArrangeSwappableNode(loopWeight: 0, fortySwapWeight: 50, sparkWeight: 50);
        _sparkWalletMock.SetupGet(x => x.Status)
            .Returns(new SparkWalletStatus(SparkWalletState.Unavailable, Reason: "the signer is not the expected one"));

        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        result.Should().Be(ManageNodeLiquidityResult.Success);
        _swapsServiceMock.Verify(x => x.CreateSwapOutAsync(node, It.Is<SwapOut>(s => s.Provider == SwapProvider.FortySwap),
            It.IsAny<SwapOutRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManageNodeLiquidity_WithAllWeightsAtZero_StillDefaultsToLoop()
    {
        var (node, _) = ArrangeSwappableNode(loopWeight: 0, fortySwapWeight: 0, sparkWeight: 0);

        var result = await _autoLiquidityManagementJob.ManageNodeLiquidity(node, CancellationToken.None);

        result.Should().Be(ManageNodeLiquidityResult.Success);
        _swapsServiceMock.Verify(x => x.CreateSwapOutAsync(node, It.Is<SwapOut>(s => s.Provider == SwapProvider.Loop),
            It.IsAny<SwapOutRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}
