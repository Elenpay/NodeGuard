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
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Humanizer;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace FundsManager.Services;

public interface ICoinSelectionService
{
    /// <summary>
    /// Gets the UTXOs for a wallet that are not locked in other transactions
    /// </summary>
    /// <param name="derivationStrategy"></param>
    public Task<List<UTXO>> GetAvailableUTXOsAsync(DerivationStrategyBase derivationStrategy);

    /// <summary>
    /// Locks the UTXOs for using in a specific transaction
    /// </summary>
    /// <param name="selectedUTXOs"></param>
    /// <param name="channelOperationRequest"></param>
    public Task LockUTXOs(List<UTXO> selectedUTXOs, IBitcoinRequest bitcoinRequest, IBitcoinRequestRepository bitcoinRequestRepository);

    public Task<(List<ICoin> coins, List<UTXO> selectedUTXOs)> GetTxInputCoins(
        List<UTXO> availableUTXOs,
        IBitcoinRequest request,
        DerivationStrategyBase derivationStrategy);
}

public class CoinSelectionService: ICoinSelectionService
{
    private readonly ILogger<BitcoinService> _logger;
    private readonly IMapper _mapper;
    private readonly IFMUTXORepository _fmutxoRepository;
    private readonly INBXplorerService _nbXplorerService;

    public CoinSelectionService(
        ILogger<BitcoinService> logger,
        IMapper mapper,
        IFMUTXORepository fmutxoRepository,
        INBXplorerService nbXplorerService
    )
    {
        _logger = logger;
        _mapper = mapper;
        _fmutxoRepository = fmutxoRepository;
        _nbXplorerService = nbXplorerService;
    }

    public async Task LockUTXOs(List<UTXO> selectedUTXOs, IBitcoinRequest bitcoinRequest, IBitcoinRequestRepository bitcoinRequestRepository)
    {
        // We "lock" the PSBT to the channel operation request by adding to its UTXOs collection for later checking
        var utxos = selectedUTXOs.Select(x => _mapper.Map<UTXO, FMUTXO>(x)).ToList();

        var addUTXOSOperation = await bitcoinRequestRepository.AddUTXOs(bitcoinRequest, utxos);
        if (!addUTXOSOperation.Item1)
        {
            _logger.LogError(
                $"Could not add the following utxos({utxos.Humanize()}) to op request:{bitcoinRequest.Id}");
        }
    }

    public async Task<List<UTXO>> GetAvailableUTXOsAsync(DerivationStrategyBase derivationStrategy)
    {
        var lockedUTXOs = await _fmutxoRepository.GetLockedUTXOs();
        var utxoChanges = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);
        utxoChanges.RemoveDuplicateUTXOs();

        var availableUTXOs = new List<UTXO>();
        foreach (var utxo in utxoChanges.Confirmed.UTXOs)
        {
            var fmUtxo = _mapper.Map<UTXO, FMUTXO>(utxo);

            if (lockedUTXOs.Contains(fmUtxo))
            {
                _logger.LogInformation("Removing UTXO: {Utxo} from UTXO set as it is locked", fmUtxo.ToString());
            }
            else
            {
                availableUTXOs.Add(utxo);
            }
        }

        return availableUTXOs;
    }

    /// <summary>
    /// Gets UTXOs confirmed from the wallet of the request
    /// </summary>
    /// <param name="channelOperationRequest"></param>
    /// <param name="nbxplorerClient"></param>
    /// <param name="derivationStrategy"></param>
    /// <returns></returns>
    public async Task<(List<ICoin> coins, List<UTXO> selectedUTXOs)> GetTxInputCoins(
        List<UTXO> availableUTXOs,
        IBitcoinRequest request,
        DerivationStrategyBase derivationStrategy)
    {
        var utxoChanges = await _nbXplorerService.GetUTXOsAsync(derivationStrategy, default);
        utxoChanges.RemoveDuplicateUTXOs();

        var satsAmount = request.SatsAmount;

        var selectedUTXOs = await LightningHelper.SelectUTXOsByOldest(request.Wallet, satsAmount, availableUTXOs, _logger);
        var coins = await LightningHelper.SelectCoins(request.Wallet, selectedUTXOs);

        return (coins, selectedUTXOs);
    }
}