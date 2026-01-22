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

using NBitcoin;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Result of managing node liquidity operation
/// </summary>
public enum ManageNodeLiquidityResult
{
    Success,
    BalanceBelowThreshold,
    MaxSwapsInFlightReached,
    BudgetExhausted,
    ExcessBalanceBelowMinimum,
    Error
}

/// <summary>
/// Job for monitoring node balances and managing liquidity automatically based on node configuration.
/// This includes swap outs, swap ins, and channel operations.
/// </summary>
[DisallowConcurrentExecution]
public class AutoLiquidityManagementJob : IJob
{
    private readonly ILogger<AutoLiquidityManagementJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly ISwapOutRepository _swapOutRepository;
    private readonly ISwapsService _swapsService;
    private readonly IFortySwapService _fortySwapService;
    private readonly ILightningService _lightningService;
    private readonly IWalletRepository _walletRepository;
    private readonly INBXplorerService _nbXplorerService;
    private readonly IAuditService _auditService;

    public AutoLiquidityManagementJob(
        ILogger<AutoLiquidityManagementJob> logger,
        INodeRepository nodeRepository,
        ISwapOutRepository swapOutRepository,
        ISwapsService swapsService,
        IFortySwapService fortySwapService,
        ILightningService lightningService,
        IWalletRepository walletRepository,
        INBXplorerService nbXplorerService,
        IAuditService auditService)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _swapOutRepository = swapOutRepository;
        _swapsService = swapsService;
        _fortySwapService = fortySwapService;
        _lightningService = lightningService;
        _walletRepository = walletRepository;
        _nbXplorerService = nbXplorerService;
        _auditService = auditService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}...", nameof(AutoLiquidityManagementJob));

        try
        {
            // Get all enabled nodes with auto liquidity management enabled
            var eligibleNodes = await _nodeRepository.GetAllWithAutoLiquidityEnabled();

            _logger.LogInformation("Found {Count} nodes with automatic liquidity management enabled", eligibleNodes.Count);

            foreach (var node in eligibleNodes)
            {
                try
                {
                    var result = await ManageNodeLiquidity(node, context.CancellationToken);
                    _logger.LogInformation("Automatic liquidity management job for node {NodeName} resulted in: {Result}",
                        node.Name, result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing automatic swap for node {NodeName} ({NodePubKey})",
                        node.Name, node.PubKey);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in {JobName}", nameof(AutoLiquidityManagementJob));
        }

        _logger.LogInformation("{JobName} ended", nameof(AutoLiquidityManagementJob));
    }

    /// <summary>
    /// Selects a swap provider based on configured weights.
    /// Uses weighted random selection - higher weight = higher probability.
    /// </summary>
    private SwapProvider SelectSwapProvider(Node node)
    {
        var loopWeight = node.LoopSwapWeight;
        var fortySwapWeight = node.FortySwapWeight;
        var totalWeight = loopWeight + fortySwapWeight;

        if (totalWeight == 0)
        {
            _logger.LogWarning("Node {NodeName} has total weight of 0, defaulting to Loop", node.Name);
            return SwapProvider.Loop;
        }

        // Weighted random selection
        var random = new Random();
        var randomValue = random.Next(0, totalWeight);

        var selectedProvider = randomValue < loopWeight ? SwapProvider.Loop : SwapProvider.FortySwap;

        _logger.LogDebug("Selected {Provider} for node {NodeName} (Loop weight: {LoopWeight}, 40swap weight: {FortySwapWeight}, random: {Random})",
            selectedProvider, node.Name, loopWeight, fortySwapWeight, randomValue);

        return selectedProvider;
    }

    public async Task<ManageNodeLiquidityResult> ManageNodeLiquidity(Node node, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing node {NodeName} ({NodePubKey})", node.Name, node.PubKey);

        // Check if budget period needs to be refreshed
        var now = DateTimeOffset.UtcNow;
        if (!node.SwapBudgetStartDatetime.HasValue ||
            now - node.SwapBudgetStartDatetime.Value >= node.SwapBudgetRefreshInterval)
        {
            _logger.LogInformation("Refreshing swap budget for node {NodeName}", node.Name);
            node.SwapBudgetStartDatetime = now;
            _nodeRepository.Update(node);
        }

        // Get node balance
        var channelBalance = await _lightningService.ChannelBalanceAsync(node);
        if (channelBalance == null)
        {
            _logger.LogWarning("Could not get channel balance for node {NodeName}", node.Name);
            return ManageNodeLiquidityResult.Error;
        }

        var totalBalanceSats = (long)(channelBalance.LocalBalance?.Sat ?? 0);
        var totalBalanceBtc = new Money(totalBalanceSats, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);
        var thresholdBtc = new Money(node.MinimumBalanceThresholdSats, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);
        _logger.LogDebug("Node {NodeName} has balance: {Balance} BTC, threshold: {Threshold} BTC",
            node.Name, totalBalanceBtc, thresholdBtc);

        // Check if balance exceeds threshold
        if (totalBalanceSats <= node.MinimumBalanceThresholdSats)
        {
            _logger.LogDebug("Node {NodeName} balance {Balance} BTC does not exceed threshold {Threshold} BTC",
                node.Name, totalBalanceBtc, thresholdBtc);
            return ManageNodeLiquidityResult.BalanceBelowThreshold;
        }

        // Get in-flight swaps for this node (optimistic locking)
        var inFlightSwaps = await _swapOutRepository.GetInFlightSwapsByNode(node.Id);
        var inFlightCount = inFlightSwaps.Count;

        _logger.LogDebug("Node {NodeName} has {InFlightCount} swaps in flight, max: {MaxSwapsInFlight}",
            node.Name, inFlightCount, node.MaxSwapsInFlight);

        if (inFlightCount >= node.MaxSwapsInFlight)
        {
            _logger.LogInformation("Node {NodeName} has reached max swaps in flight ({MaxSwapsInFlight})",
                node.Name, node.MaxSwapsInFlight);
            return ManageNodeLiquidityResult.MaxSwapsInFlightReached;
        }

        // Calculate how much we can swap
        var excessBalance = totalBalanceSats - node.MinimumBalanceThresholdSats;

        // Get consumed fee budget in current period
        var consumedBudgetMoney = await _swapOutRepository.GetConsumedFeesSince(
            node.Id,
            node.SwapBudgetStartDatetime ?? now);

        var remainingFeeBudget = node.SwapBudgetSats - consumedBudgetMoney.Satoshi;
        var excessBalanceBtc = new Money(excessBalance, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);
        var remainingFeeBudgetBtc = new Money(remainingFeeBudget, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);

        _logger.LogDebug("Node {NodeName} - Excess balance: {Excess} BTC, Remaining fee budget: {Budget} BTC, Consumed fees: {Consumed} BTC",
            node.Name, excessBalanceBtc, remainingFeeBudgetBtc, consumedBudgetMoney.ToDecimal(MoneyUnit.BTC));

        if (remainingFeeBudget <= 0)
        {
            _logger.LogInformation("Node {NodeName} has exhausted swap fee budget for this period", node.Name);
            return ManageNodeLiquidityResult.BudgetExhausted;
        }

        // Determine swap amount (bounded by min, max, and excess balance)
        // We can only swap what's above the threshold (excess balance)
        var maxPossibleSwap = Math.Min(excessBalance, node.SwapMaxAmountSats);

        // If we don't have enough to meet the minimum swap size, skip
        if (maxPossibleSwap < node.SwapMinAmountSats)
        {
            var maxPossibleSwapBtc = new Money(maxPossibleSwap, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);
            var minSwapBtc = new Money(node.SwapMinAmountSats, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);
            _logger.LogDebug("Node {NodeName} - max possible swap {MaxPossible} BTC is below minimum {Min} BTC. Excess: {Excess} BTC",
                node.Name, maxPossibleSwapBtc, minSwapBtc, excessBalanceBtc);
            return ManageNodeLiquidityResult.ExcessBalanceBelowMinimum;
        }

        // Swap the maximum we can, ensuring it meets the minimum
        var swapAmount = Math.Max(node.SwapMinAmountSats, maxPossibleSwap);
        var swapAmountBtc = new Money(swapAmount, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);

        _logger.LogDebug("Node {NodeName} - Initiating swap for {Amount} BTC", node.Name, swapAmountBtc);

        // Get destination address from wallet
        var destinationAddress = await GetDestinationAddressAsync(node, cancellationToken);
        if (destinationAddress == null)
        {
            _logger.LogError("Could not get destination address for node {NodeName}", node.Name);
            return ManageNodeLiquidityResult.Error;
        }

        // Create swap out request
        _logger.LogInformation("Initiating automatic swap out for node {NodeName} - Amount: {Amount} BTC",
            node.Name, swapAmountBtc);

        // Select swap provider based on weights
        var selectedProvider = SelectSwapProvider(node);
        _logger.LogInformation("Using {Provider} for swap out on node {NodeName}", selectedProvider, node.Name);

        try
        {
            var swapRequest = new SwapOutRequest
            {
                Amount = swapAmount,
                Address = destinationAddress,
                MaxRoutingFeesPercent = node.MaxSwapRoutingFeeRatio * 100, // Convert to percentage
                MaxServiceFeesPercent = Constants.SWAP_MAX_SERVICE_FEES_PERCENT,
                MaxMinerFees = Constants.SWAP_MAX_MINER_FEES_SATS,
                SweepConfTarget = Constants.SWEEP_CONF_TARGET,
                PrepayAmtSat = Constants.SWAP_PREPAY_AMOUNT_SATS,
                SwapPublicationDeadlineMinutes = 30,
            };

            var swapResponse = await _swapsService.CreateSwapOutAsync(
                        node,
                        selectedProvider,
                        swapRequest,
                        cancellationToken);

            // Create SwapOut record
            var swapOut = new SwapOut
            {
                NodeId = node.Id,
                DestinationWalletId = node.FundsDestinationWalletId!.Value,
                Provider = selectedProvider,
                ProviderId = swapResponse.Id,
                SatsAmount = swapAmount,
                ServiceFeeSats = swapResponse.ServerFee,
                OnChainFeeSats = swapResponse.OnchainFee,
                LightningFeeSats = swapResponse.OffchainFee,
                Status = swapResponse.Status,
                IsManual = false, // Automatic swap
                CreationDatetime = DateTimeOffset.UtcNow,
                UpdateDatetime = DateTimeOffset.UtcNow,
            };

            var (success, error) = await _swapOutRepository.AddAsync(swapOut);
            if (!success)
            {
                _logger.LogError("Failed to save swap out record for node {NodeName}: {Error}",
                    node.Name, error);
                return ManageNodeLiquidityResult.Error;
            }

            _logger.LogInformation("Successfully initiated swap out {SwapId} for node {NodeName}",
                swapOut.ProviderId, node.Name);

            // Audit successful swap out initiation
            await _auditService.LogSystemAsync(
                AuditActionType.SwapOutInitiated,
                AuditEventType.Success,
                AuditObjectType.SwapOut,
                swapOut.ProviderId,
                new
                {
                    NodeId = node.Id,
                    NodeName = node.Name,
                    Provider = selectedProvider.ToString(),
                    AmountSats = swapAmount,
                    DestinationAddress = destinationAddress,
                    DestinationWalletId = node.FundsDestinationWalletId,
                    ProviderId = swapResponse.Id,
                    IsAutomatic = true
                });

            return ManageNodeLiquidityResult.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create swap out for node {NodeName}", node.Name);
            return ManageNodeLiquidityResult.Error;
        }
    }

    private async Task<string?> GetDestinationAddressAsync(Node node, CancellationToken cancellationToken)
    {
        if (!node.FundsDestinationWalletId.HasValue)
        {
            _logger.LogError("Node {NodeName} has no funds destination wallet configured", node.Name);
            return null;
        }

        try
        {
            // Get the destination wallet
            var wallet = await _walletRepository.GetById(node.FundsDestinationWalletId.Value);
            if (wallet == null)
            {
                _logger.LogError("Could not find wallet with ID {WalletId} for node {NodeName}",
                    node.FundsDestinationWalletId.Value, node.Name);
                return null;
            }

            // Get the derivation strategy for the wallet
            var derivationStrategy = wallet.GetDerivationStrategy();
            if (derivationStrategy == null)
            {
                _logger.LogError("Could not get derivation strategy for wallet {WalletName}", wallet.Name);
                return null;
            }

            // Get an unused deposit address from NBXplorer
            var keyPathInfo = await _nbXplorerService.GetUnusedAsync(
                derivationStrategy,
                NBXplorer.DerivationStrategy.DerivationFeature.Deposit,
                skip: 0,
                reserve: true, // Reserve the address so it won't be reused
                cancellation: cancellationToken);

            if (keyPathInfo?.Address == null)
            {
                _logger.LogError("Could not get unused address from NBXplorer for wallet {WalletName}", wallet.Name);
                return null;
            }

            _logger.LogDebug("Generated deposit address {Address} for node {NodeName} using wallet {WalletName}",
                keyPathInfo.Address, node.Name, wallet.Name);

            return keyPathInfo.Address.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting destination address for node {NodeName}", node.Name);
            return null;
        }
    }
}
