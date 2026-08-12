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
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using Humanizer;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

namespace NodeGuard.Services;

public interface ICoinSelectionService
{
    /// <summary>
    /// Gets the UTXOs for a wallet that are not locked in other transactions
    /// </summary>
    /// <param name="derivationStrategy"></param>
    public Task<List<UTXO>> GetAvailableUTXOsAsync(DerivationStrategyBase derivationStrategy);

    /// <summary>
    /// Gets the UTXOs for a wallet that are not locked in other transactions, but with a limit
    /// </summary>
    /// <param name="derivationStrategy"></param>
    /// <param name="strategy"></param>
    /// <param name="limit"></param>
    /// <param name="amount"></param>
    /// <param name="tolerance"></param>
    /// <param name="closestTo"></param>
    public Task<List<UTXO>> GetAvailableUTXOsAsync(DerivationStrategyBase derivationStrategy, CoinSelectionStrategy strategy, int limit, long amount, long closestTo);

    /// <summary>
    /// Gets the UTXOs that are not locked in other transactions related to the outpoints
    /// </summary>
    /// <param name="derivationStrategy"></param>
    /// <param name="outPoints"></param>
    public Task<List<UTXO>> GetUTXOsByOutpointAsync(DerivationStrategyBase derivationStrategy, List<OutPoint> outPoints);

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
    
    /// <summary>
    /// Gets the frozen UTXOs
    /// </summary>
    public Task<List<string>> GetFrozenUTXOs();

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
    private readonly IUTXOTagRepository _utxoTagRepository;

    public CoinSelectionService(
        ILogger<BitcoinService> logger,
        IMapper mapper,
        IFMUTXORepository fmutxoRepository,
        INBXplorerService nbXplorerService,
        IChannelOperationRequestRepository channelOperationRequestRepository,
        IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository,
        IUTXOTagRepository utxoTagRepository
    )
    {
        _logger = logger;
        _mapper = mapper;
        _fmutxoRepository = fmutxoRepository;
        _nbXplorerService = nbXplorerService;
        _channelOperationRequestRepository = channelOperationRequestRepository;
        _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
        _utxoTagRepository = utxoTagRepository;
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
        if (!getUTXOsOperation.Item1 || getUTXOsOperation.Item2 == null)
        {
            _logger.LogError(
                $"Could not get utxos from {requestType.ToString()} request:{bitcoinRequest.Id}");
            return new();
        }

        // TODO: Convert from fmutxo to utxo by calling nbxplorer api with the list of txids
        var lockedUTXOsList = getUTXOsOperation.Item2.Select(utxo => $"{utxo.TxId}-{utxo.OutputIndex}");
        var utxos = await _nbXplorerService.GetUTXOsAsync(bitcoinRequest.Wallet.GetDerivationStrategy());
        utxos.RemoveDuplicateUTXOs();
        return utxos.Confirmed.UTXOs.Where(utxo => lockedUTXOsList.Contains(utxo.Outpoint.ToString())).ToList();
    }

    private async Task<List<string>> GetLockedFrozenOutpoints()
    {
        var lockedUTXOs = await _fmutxoRepository.GetLockedUTXOs();
        var listLocked = lockedUTXOs.Select(utxo => $"{utxo.TxId}-{utxo.OutputIndex}").ToList();
        var listFrozen = await GetFrozenUTXOs();
        var frozenAndLockedOutpoints = new List<string>();
        frozenAndLockedOutpoints.AddRange(listLocked);
        frozenAndLockedOutpoints.AddRange(listFrozen);
        return frozenAndLockedOutpoints;
    }

    /// <summary>
    /// Outpoints that must never be offered for coin selection: locked, frozen and dust UTXOs.
    /// </summary>
    private async Task<List<string>> GetIgnoredOutpoints(UTXOChanges utxos)
    {
        var ignoredOutpoints = await GetLockedFrozenOutpoints();
        ignoredOutpoints.AddRange(utxos.Confirmed.UTXOs
            .Concat(utxos.Unconfirmed.UTXOs)
            .Where(utxo => ((Money)utxo.Value).Satoshi <= Constants.MINIMUM_UTXO_VALUE_SATS)
            .Select(utxo => utxo.Outpoint.ToString()));
        return ignoredOutpoints;
    }

    private async Task<List<UTXO>> FilterLockedFrozenUTXOs(UTXOChanges? utxoChanges)
    {
        var frozenAndLockedOutpoints = await GetLockedFrozenOutpoints();

        utxoChanges.RemoveDuplicateUTXOs();

        var availableUTXOs = new List<UTXO>();
        foreach (var utxo in utxoChanges.Confirmed.UTXOs)
        {

            if (frozenAndLockedOutpoints.Contains(utxo.Outpoint.ToString()))
            {
                _logger.LogInformation("Removing UTXO: {Utxo} from UTXO set as it is locked", utxo.Outpoint.ToString());
            }
            else if (((Money)utxo.Value).Satoshi <= Constants.MINIMUM_UTXO_VALUE_SATS)
            {
                _logger.LogInformation("Removing UTXO: {Utxo} from UTXO set as it is dust ({Sats} sats <= {MinSats} sats)",
                    utxo.Outpoint.ToString(), ((Money)utxo.Value).Satoshi, Constants.MINIMUM_UTXO_VALUE_SATS);
            }
            else
            {
                availableUTXOs.Add(utxo);
            }
        }

        return availableUTXOs;
    }
    
    public async Task<List<string>> GetFrozenUTXOs()
    {
        var frozenUTXOs = await _utxoTagRepository.GetByKeyValue(Constants.IsFrozenTag, "true");
        var manuallyFrozenUTXOs = await _utxoTagRepository.GetByKeyValue(Constants.IsManuallyFrozenTag, "true");
        var manuallyUnfrozenUTXOs = await _utxoTagRepository.GetByKeyValue(Constants.IsManuallyFrozenTag, "false");
        var listFrozen = frozenUTXOs.Select(utxo => utxo.Outpoint).ToList();
        var listManuallyFrozen = manuallyFrozenUTXOs.Select(utxo => utxo.Outpoint).ToList();
        var listManuallyUnfrozen = manuallyUnfrozenUTXOs.Select(utxo => utxo.Outpoint).ToList();

        // Merge manually frozen and frozen UTXOs and remove manually unfrozen UTXOs
        List<string> frozenUTXOsList =
            listFrozen
            .Union(listManuallyFrozen)
            .Except(listManuallyUnfrozen)
            .ToList();
        
        return frozenUTXOsList;
    }

    public async Task<List<UTXO>> GetAvailableUTXOsAsync(DerivationStrategyBase derivationStrategy)
    {
        var utxoChanges = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);
        return await FilterLockedFrozenUTXOs(utxoChanges);
    }

    public async Task<List<UTXO>> GetAvailableUTXOsAsync(DerivationStrategyBase derivationStrategy, CoinSelectionStrategy strategy, int limit, long amount, long closestTo)
    {
        UTXOChanges utxoChanges;
        if (Constants.NBXPLORER_ENABLE_CUSTOM_BACKEND)
        {
            try
            {
                // Tell the backend which UTXOs to skip (locked, frozen and dust), otherwise it
                // counts them towards the requested amount and the local filter below strips them
                // afterwards, returning a selection that falls short of that amount
                var allUtxos = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);
                allUtxos.RemoveDuplicateUTXOs();
                var ignoreOutpoints = await GetIgnoredOutpoints(allUtxos);

                utxoChanges = await _nbXplorerService.GetUTXOsByLimitAsync(derivationStrategy, strategy, limit, amount, closestTo, ignoreOutpoints);
            }
            catch (Exception e)
            {
                // Skip the custom backend entirely and degrade to the plain UTXO listing, same as
                // when NBXPLORER_ENABLE_CUSTOM_BACKEND is off: the strategy/amount are no longer
                // applied server-side, but the local filter below still strips locked, frozen and
                // dust UTXOs, so none of them can leak through
                _logger.LogWarning(e,
                    "UTXO selection through the custom NBXplorer backend failed for strategy {Strategy}, falling back to the plain UTXO listing",
                    strategy);
                utxoChanges = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);
            }
        }
        else
        {
            utxoChanges = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);
        }
        return await FilterLockedFrozenUTXOs(utxoChanges);
    }

    /// <summary>
    /// Gets UTXOs confirmed from the wallet of the request
    /// </summary>
    /// <param name="channelOperationRequest"></param>
    /// <param name="nbxplorerClient"></param>
    /// <param name="derivationStrategy"></param>
    /// <returns></returns>
    public Task<(List<ICoin> coins, List<UTXO> selectedUTXOs)> GetTxInputCoins(
        List<UTXO> availableUTXOs,
        IBitcoinRequest request,
        DerivationStrategyBase derivationStrategy)
    {
        var satsAmount = request.SatsAmount;

        var selectedUTXOs = Constants.ENABLE_COIN_SELECTION_BY_CLOSEST
            ? UTXOSelectionAlgorithms.SelectUTXOsByClosest(request.Wallet, satsAmount, availableUTXOs, _logger)
            : UTXOSelectionAlgorithms.SelectUTXOsByOldest(request.Wallet, satsAmount, availableUTXOs, _logger);

        var coins = request.Wallet.ToCoins(selectedUTXOs);

        return Task.FromResult((coins, selectedUTXOs));
    }

    public async Task<List<UTXO>> GetUTXOsByOutpointAsync(DerivationStrategyBase derivationStrategy, List<OutPoint> outPoints)
    {
        var utxos = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);
        utxos.RemoveDuplicateUTXOs();
        return utxos.Confirmed.UTXOs.Where(utxo => outPoints.Contains(utxo.Outpoint)).ToList();
    }
}
