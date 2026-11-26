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
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services.Spark;

namespace NodeGuard.Services;

public interface ISwapOutWarningService
{
    /// <summary>
    /// The warning to show before opening a <paramref name="channelSats"/> channel from the node, when the automatic
    /// swap-out would take its outbound (see <see cref="SwapOutForecast"/>). Null when it would not, or when the node's
    /// balance can't be read within <see cref="SwapOutWarningService.BalanceTimeout"/>. The text is HTML.
    /// </summary>
    Task<string?> ForChannelOpenAsync(int sourceNodeId, long channelSats);
}

public class SwapOutWarningService : ISwapOutWarningService
{
    public static readonly TimeSpan BalanceTimeout = TimeSpan.FromSeconds(5);

    private readonly INodeRepository _nodeRepository;
    private readonly ILightningService _lightningService;
    private readonly SparkSettings _sparkSettings;

    public SwapOutWarningService(INodeRepository nodeRepository, ILightningService lightningService, SparkSettings sparkSettings)
    {
        _nodeRepository = nodeRepository;
        _lightningService = lightningService;
        _sparkSettings = sparkSettings;
    }

    public async Task<string?> ForChannelOpenAsync(int sourceNodeId, long channelSats)
    {
        var node = await _nodeRepository.GetById(sourceNodeId, false);
        if (node == null || !SwapOutForecast.SwapsOutAutomatically(node)) return null;

        // The node's LND gets no deadline of its own: an unreachable one must not hold the button
        Lnrpc.ChannelBalanceResponse? balance;
        try
        {
            balance = await _lightningService.ChannelBalanceAsync(node).WaitAsync(BalanceTimeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
        if (balance == null) return null;

        var localBalanceSats = (long)(balance.LocalBalance?.Sat ?? 0) + (long)(balance.PendingOpenLocalBalance?.Sat ?? 0);
        if (SwapOutForecast.SatsSwappedAfterOpen(node, localBalanceSats, channelSats, _sparkSettings) is not { } swappedSats) return null;

        var balanceAfterBtc = new Money(localBalanceSats + channelSats).ToUnit(MoneyUnit.BTC);
        var thresholdBtc = new Money(node.MinimumBalanceThresholdSats).ToUnit(MoneyUnit.BTC);
        var swappedBtc = new Money(swappedSats).ToUnit(MoneyUnit.BTC);
        return $"Opening this channel takes {node.Name}'s local balance to {balanceAfterBtc:f8} BTC, above its {thresholdBtc:f8} BTC swap-out threshold. " +
               $"Automatic liquidity management would swap out up to {swappedBtc:f8} BTC of this channel's outbound liquidity: you would pay swap fees and lose that outbound. Open it anyway?";
    }
}
