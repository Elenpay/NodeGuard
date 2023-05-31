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
    public Task LockUTXOs(List<UTXO> selectedUTXOs, IBitcoinRequest bitcoinRequest, BitcoinRequestType requestType);

    /// <summary>
    /// Gets the locked UTXOs from a request
    /// </summary>
    /// <param name="bitcoinRequest"></param>
    /// <param name="requestType"></param>
    public Task<List<UTXO>> GetLockedUTXOsForRequest(IBitcoinRequest bitcoinRequest, BitcoinRequestType requestType);

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
    private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
    private readonly IWalletWithdrawalRequestRepository _walletWithdrawalRequestRepository;

    public CoinSelectionService(
        ILogger<BitcoinService> logger,
        IMapper mapper,
        IFMUTXORepository fmutxoRepository,
        INBXplorerService nbXplorerService,
        IChannelOperationRequestRepository channelOperationRequestRepository,
        IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository
    )
    {
        _logger = logger;
        _mapper = mapper;
        _fmutxoRepository = fmutxoRepository;
        _nbXplorerService = nbXplorerService;
        _channelOperationRequestRepository = channelOperationRequestRepository;
        _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
    }

    private IBitcoinRequestRepository GetRepository(BitcoinRequestType requestType)
    {
       return requestType switch
       {
           BitcoinRequestType.ChannelOperation => _channelOperationRequestRepository,
           BitcoinRequestType.WalletWithdrawal => _walletWithdrawalRequestRepository,
           _ => throw new NotImplementedException()
       };
    }

    public async Task LockUTXOs(List<UTXO> selectedUTXOs, IBitcoinRequest bitcoinRequest, BitcoinRequestType requestType)
    {
        // We "lock" the PSBT to the channel operation request by adding to its UTXOs collection for later checking
        var utxos = selectedUTXOs.Select(x => _mapper.Map<UTXO, FMUTXO>(x)).ToList();

        var addUTXOsOperation = await GetRepository(requestType).AddUTXOs(bitcoinRequest, utxos);
        if (!addUTXOsOperation.Item1)
        {
            _logger.LogError(
                $"Could not add the following utxos({utxos.Humanize()}) to op request:{bitcoinRequest.Id}");
        }
    }

    public async Task<List<UTXO>> GetLockedUTXOsForRequest(IBitcoinRequest bitcoinRequest, BitcoinRequestType requestType)
    {
        var getUTXOsOperation = await GetRepository(requestType).GetUTXOs(bitcoinRequest);
        if (!getUTXOsOperation.Item1)
        {
            _logger.LogError(
                $"Could not get utxos from {requestType.ToString()} request:{bitcoinRequest.Id}");
            return new();
        }

        // TODO: Convert from fmutxo to utxo by calling nbxplorer api with the list of txids
        var lockedUTXOsList = getUTXOsOperation.Item2.Select(utxo => utxo.TxId);
        var utxos = await _nbXplorerService.GetUTXOsAsync(bitcoinRequest.Wallet.GetDerivationStrategy());
        return utxos.Confirmed.UTXOs.Where(utxo => lockedUTXOsList.Contains(utxo.Outpoint.Hash.ToString())).ToList();
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
        var satsAmount = request.SatsAmount;

        var selectedUTXOs = await LightningHelper.SelectUTXOsByOldest(request.Wallet, satsAmount, availableUTXOs, _logger);
        var coins = await LightningHelper.SelectCoins(request.Wallet, selectedUTXOs);

        return (coins, selectedUTXOs);
    }
}