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
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Unmockable;

// ReSharper disable All

namespace FundsManager.Services
{
    /// <summary>
    /// Service for bitcoin-related things (e.g. Nbxplorer)
    /// </summary>
    public class BitcoinService : IBitcoinService

    {
        private readonly ILogger<BitcoinService> _logger;
        private readonly IFMUTXORepository _fmutxoRepository;
        private readonly IMapper _mapper;
        private readonly IWalletWithdrawalRequestRepository _walletWithdrawalRequestRepository;
        private readonly IWalletWithdrawalRequestPsbtRepository _walletWithdrawalRequestPsbtRepository;
        private readonly INodeRepository _nodeRepository;
        private readonly IRemoteSignerService _remoteSignerService;
        private readonly INBXplorerService _nbXplorerService;

        public BitcoinService(ILogger<BitcoinService> logger,
            IFMUTXORepository fmutxoRepository,
            IMapper mapper,
            IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository,
            IWalletWithdrawalRequestPsbtRepository walletWithdrawalRequestPsbtRepository,
            INodeRepository nodeRepository,
            IRemoteSignerService remoteSignerService,
            INBXplorerService nbXplorerService
            )
        {
            _logger = logger;
            _fmutxoRepository = fmutxoRepository;
            _mapper = mapper;
            _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
            _walletWithdrawalRequestPsbtRepository = walletWithdrawalRequestPsbtRepository;
            _nodeRepository = nodeRepository;
            _remoteSignerService = remoteSignerService;
            _nbXplorerService = nbXplorerService;
        }

        public async Task<(decimal, long)> GetWalletConfirmedBalance(Wallet wallet)
        {
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));

            var balance = await _nbXplorerService.GetBalanceAsync(wallet.GetDerivationStrategy(), default);
            var confirmedBalanceMoney = (Money) balance.Confirmed;

            return (confirmedBalanceMoney.ToUnit(MoneyUnit.BTC), confirmedBalanceMoney.Satoshi);
        }

        public async Task<(PSBT?, bool)> GenerateTemplatePSBT(WalletWithdrawalRequest walletWithdrawalRequest)
        {
            if (walletWithdrawalRequest == null) throw new ArgumentNullException(nameof(walletWithdrawalRequest));

            walletWithdrawalRequest = await _walletWithdrawalRequestRepository.GetById(walletWithdrawalRequest.Id);

            (PSBT?, bool) result = (null, false);
            if (walletWithdrawalRequest.Status != WalletWithdrawalRequestStatus.Pending
                && walletWithdrawalRequest.Status != WalletWithdrawalRequestStatus.PSBTSignaturesPending)
            {
                _logger.LogError("PSBT Generation cancelled, operation is not in pending state");
                return (null, false);
            }

            var isFullySynched = (await _nbXplorerService.GetStatusAsync()).IsFullySynched;
            if (!isFullySynched)
            {
                _logger.LogError("Error, nbxplorer not fully synched");
                return (null, false);
            }

            var derivationStrategy = walletWithdrawalRequest.Wallet.GetDerivationStrategy();

            if (derivationStrategy == null)
            {
                _logger.LogError("Error while getting the derivation strategy scheme for wallet: {WalletId}",
                    walletWithdrawalRequest.Wallet.Id);
                return (null, false);
            }

            //If there is already a PSBT as template with the inputs as still valid UTXOs we avoid generating the whole process again to
            //avoid non-deterministic issues (e.g. Input order and other potential errors)
            var templatePSBT = walletWithdrawalRequest.WalletWithdrawalRequestPSBTs.Where(x => x.IsTemplatePSBT)
                .OrderBy(x => x.Id)
                .LastOrDefault(); //We take the oldest

            if (templatePSBT != null && PSBT.TryParse(templatePSBT.PSBT, CurrentNetworkHelper.GetCurrentNetwork(),
                    out var parsedTemplatePSBT))
            {
                var currentUtxos = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);
                if (parsedTemplatePSBT.Inputs.All(
                        x => currentUtxos.Confirmed.UTXOs.Select(x => x.Outpoint).Contains(x.PrevOut)))
                {
                    return (parsedTemplatePSBT, false);
                }
                else
                {
                    //We mark the request as failed since we would need to invalidate existing PSBTs
                    _logger.LogError(
                        "Marking the withdrawal request: {RequestId} as failed since the original UTXOs are no longer valid",
                        walletWithdrawalRequest.Id);

                    walletWithdrawalRequest.Status = WalletWithdrawalRequestStatus.Failed;

                    var updateResult = _walletWithdrawalRequestRepository.Update(walletWithdrawalRequest);

                    if (!updateResult.Item1)
                    {
                        _logger.LogError("Error while updating withdrawal request: {RequestId}",
                            walletWithdrawalRequest.Id);
                    }

                    return (null, false);
                }
            }

            var utxoChanges = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);
            utxoChanges.RemoveDuplicateUTXOs();

            var lockedUtxOs =
                await _fmutxoRepository.GetLockedUTXOs(ignoredWalletWithdrawalRequestId: walletWithdrawalRequest.Id);

            //If the request is a full funds withdrawal, calculate the amount to the existing balance
            if (walletWithdrawalRequest.WithdrawAllFunds)
            {
                var balanceResponse = await _nbXplorerService.GetBalanceAsync(derivationStrategy);

                walletWithdrawalRequest.Amount = ((Money) balanceResponse.Confirmed).ToUnit(MoneyUnit.BTC);

                var update = _walletWithdrawalRequestRepository.Update(walletWithdrawalRequest);
                if (!update.Item1)
                {
                    _logger.LogError("Error while setting update amount for a full withdrawal of the wallet funds");
                }
            }

            var (scriptCoins, selectedUTXOs) = await LightningHelper.SelectCoins(walletWithdrawalRequest.Wallet,
                walletWithdrawalRequest.SatsAmount,
                utxoChanges,
                lockedUtxOs,
                _logger, _mapper);

            if (scriptCoins == null || !scriptCoins.Any())
            {
                _logger.LogError(
                    "Cannot generate base template PSBT for withdrawal request: {RequestId}, no UTXOs found for the wallet: {WalletId}",
                    walletWithdrawalRequest.Id, walletWithdrawalRequest.Wallet.Id);

                return (null, true); //true means no UTXOS
            }

            var nbXplorerNetwork = CurrentNetworkHelper.GetCurrentNetwork();

            try
            {
                //We got enough inputs to fund the TX so time to build the PSBT

                var txBuilder = nbXplorerNetwork.CreateTransactionBuilder();

                var feeRateResult = await LightningHelper.GetFeeRateResult(nbXplorerNetwork, _nbXplorerService);

                var changeAddress = await _nbXplorerService.GetUnusedAsync(derivationStrategy, DerivationFeature.Change, 0, false, default);

                if (changeAddress == null)
                {
                    _logger.LogError("Change address was not found for wallet: {WalletId}",
                        walletWithdrawalRequest.Wallet.Id);
                    return (null, false);
                }

                var builder = txBuilder;
                builder.AddCoins(scriptCoins);

                var amount = new Money(walletWithdrawalRequest.SatsAmount, MoneyUnit.Satoshi);
                var destination = BitcoinAddress.Create(walletWithdrawalRequest.DestinationAddress, nbXplorerNetwork);

                builder.SetSigningOptions(SigHash.All)
                    .SetChange(changeAddress.Address)
                    .SendEstimatedFees(feeRateResult.FeeRate);

                if (walletWithdrawalRequest.WithdrawAllFunds)
                {
                    builder.SendAll(destination);
                }
                else
                {
                    builder.Send(destination, amount);
                    builder.SendAllRemainingToChange();
                }

                result.Item1 = builder.BuildPSBT(false);
                
                //Additional fields to support PSBT signing with a HW or the Remote Signer 
                result = LightningHelper.AddDerivationData(walletWithdrawalRequest.Wallet.Keys, result, selectedUTXOs, scriptCoins, _logger, walletWithdrawalRequest.Wallet.InternalWalletSubDerivationPath);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while generating base PSBT");
            }

            // We "lock" the PSBT to the channel operation request by adding to its UTXOs collection for later checking
            var utxos = selectedUTXOs.Select(x => _mapper.Map<UTXO, FMUTXO>(x)).ToList();

            var addUTXOSOperation = await _walletWithdrawalRequestRepository.AddUTXOs(walletWithdrawalRequest, utxos);
            if (!addUTXOSOperation.Item1)
            {
                _logger.LogError(
                    $"Could not add the following utxos({utxos.Humanize()}) to op request:{walletWithdrawalRequest.Id}");
            }

            // The template PSBT is saved for later reuse

            if (result.Item1 != null)
            {
                var psbt = new WalletWithdrawalRequestPSBT
                {
                    WalletWithdrawalRequestId = walletWithdrawalRequest.Id,
                    CreationDatetime = DateTimeOffset.Now,
                    IsTemplatePSBT = true,
                    UpdateDatetime = DateTimeOffset.Now,
                    PSBT = result.Item1.ToBase64()
                };

                var addPsbtResult = await _walletWithdrawalRequestPsbtRepository.AddAsync(psbt);

                if (addPsbtResult.Item1 == false)
                {
                    _logger.LogError("Error while saving template PSBT to wallet withdrawal request: {RequestId}",
                        walletWithdrawalRequest.Id);
                }
            }

            return result;
        }

        public async Task PerformWithdrawal(WalletWithdrawalRequest walletWithdrawalRequest)
        {
            if (walletWithdrawalRequest == null) throw new ArgumentNullException(nameof(walletWithdrawalRequest));

            if (walletWithdrawalRequest.Status != WalletWithdrawalRequestStatus.PSBTSignaturesPending)
            {
                _logger.LogError(
                    "Invalid status for broadcasting the tx from wallet withdrawal request: {RequestId}, status: {RequestStatus}",
                    walletWithdrawalRequest.Id,
                    walletWithdrawalRequest.Status);

                throw new ArgumentException("Invalid status");
            }

            //Update
            walletWithdrawalRequest = await _walletWithdrawalRequestRepository.GetById(walletWithdrawalRequest.Id) ??
                                      throw new InvalidOperationException();
            
            PSBT? psbtToSign = null;
            //If it is a hot wallet, we dont need to combine the PSBTs
            if(walletWithdrawalRequest.Wallet.IsHotWallet)
            {
                psbtToSign = PSBT.Parse(walletWithdrawalRequest.WalletWithdrawalRequestPSBTs
                        .Single(x => x.IsTemplatePSBT)
                        .PSBT,
                    CurrentNetworkHelper.GetCurrentNetwork());
            }
            else //If it is a cold multisig wallet, we need to combine the PSBTs
            {
                var signedPSBTStrings = walletWithdrawalRequest.WalletWithdrawalRequestPSBTs.Where(x =>
                        !x.IsFinalisedPSBT && !x.IsInternalWalletPSBT && !x.IsTemplatePSBT)
                    .Select(x => x.PSBT).ToList();

                psbtToSign = LightningHelper.CombinePSBTs(signedPSBTStrings, _logger);
            }

            try
            {
                if (psbtToSign == null)
                {
                    var invalidPsbtNullToBeUsedForTheRequest =
                        $"Invalid combined PSBT(null) to be used for the wallet withdrawal request:{walletWithdrawalRequest.Id}";
                    _logger.LogError(invalidPsbtNullToBeUsedForTheRequest);

                    throw new ArgumentException(invalidPsbtNullToBeUsedForTheRequest, nameof(psbtToSign));
                }


                var derivationStrategyBase = walletWithdrawalRequest.Wallet.GetDerivationStrategy();

                PSBT? signedCombinedPSBT = null;
                //Check if the NodeGuard Internal Wallet needs to sign on his own
                if (walletWithdrawalRequest.AreAllRequiredHumanSignaturesCollected &&
                    walletWithdrawalRequest.Wallet.RequiresInternalWalletSigning)
                {
                    //Remote signer
                    if (Constants.ENABLE_REMOTE_SIGNER)
                    {
                        signedCombinedPSBT = await _remoteSignerService.Sign(psbtToSign);
                    }
                    else
                    {
                        signedCombinedPSBT = await SignPSBTWithEmbeddedSigner(walletWithdrawalRequest, _nbXplorerService,
                            derivationStrategyBase, psbtToSign, CurrentNetworkHelper.GetCurrentNetwork(), _logger);
                    }
                    
                }
                else
                {
                    //In this case, the combined PSBT is considered as the signed one
                    signedCombinedPSBT = psbtToSign;
                }

                if (signedCombinedPSBT == null)
                {
                    throw new InvalidOperationException("Signed combined PSBT is null");
                }

                //PSBT finalisation
                var finalisedPSBT = signedCombinedPSBT.Finalize();

                finalisedPSBT.AssertSanity();

                if (!finalisedPSBT.CanExtractTransaction())
                {
                    var cannotFinalisedCombinedPsbtForWithdrawalRequestId =
                        $"Cannot finalise combined PSBT for withdrawal request id:{walletWithdrawalRequest.Id}";
                    _logger.LogError(cannotFinalisedCombinedPsbtForWithdrawalRequestId);

                    throw new ArgumentException(cannotFinalisedCombinedPsbtForWithdrawalRequestId,
                        nameof(finalisedPSBT));
                }

                var tx = finalisedPSBT.ExtractTransaction();

                var transactionCheckResult = tx.Check();
                if (transactionCheckResult != TransactionCheckResult.Success)
                {
                    _logger.LogError("Invalid tx check reason: {Reason}", transactionCheckResult.Humanize());
                }

                var node = (await _nodeRepository.GetAllManagedByFundsManager()).FirstOrDefault();

                if (node == null)
                {
                    var noManagedFoundFoundForWithdrawalRequestId =
                        $"No managed node found for withdrawal request id:{walletWithdrawalRequest.Id}";
                    _logger.LogError(noManagedFoundFoundForWithdrawalRequestId);
                    throw new ArgumentException(noManagedFoundFoundForWithdrawalRequestId, nameof(node));
                }

                _logger.LogInformation(
                    "Publishing tx for withdrawal request id: {RequestId} by node: {NodeName} txId: {TxId}",
                    walletWithdrawalRequest.Id,
                    node.Name,
                    tx.GetHash().ToString());

                //TODO Review why LND gives EOF, nbxplorer works flawlessly
                var broadcastAsyncResult = await _nbXplorerService.BroadcastAsync(tx, default);

                if (!broadcastAsyncResult.Success)
                {
                    _logger.LogError(
                        "Failed TX broadcast for withdrawal request id: {RequestId} rpcCode: {Code}, rpcCodeMessage: {CodeMessage}, rpcMessage: {Message}",
                        walletWithdrawalRequest.Id,
                        broadcastAsyncResult.RPCCode,
                        broadcastAsyncResult.RPCCodeMessage,
                        broadcastAsyncResult.RPCMessage);
                    throw new Exception("Failed tx broadcast");
                }

                walletWithdrawalRequest.TxId = tx.GetHash().ToString();
                walletWithdrawalRequest.Status = WalletWithdrawalRequestStatus.OnChainConfirmationPending;

                var updateTxIdResult = _walletWithdrawalRequestRepository.Update(walletWithdrawalRequest);

                if (updateTxIdResult.Item1 != true)
                {
                    _logger.LogError("Error while updating the txId of withdrawal request: {RequestId}",
                        walletWithdrawalRequest.Id);
                }

                //We track the destination address
                var trackedSourceAddress = TrackedSource.Create(BitcoinAddress.Create(
                    walletWithdrawalRequest.DestinationAddress,
                    CurrentNetworkHelper.GetCurrentNetwork()));

                await _nbXplorerService.TrackAsync(trackedSourceAddress, default);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while publishing withdrawal request id: {RequestId}",
                    walletWithdrawalRequest.Id);
                throw;
            }
        }

        /// <summary>
        /// Signs with a embedded signer and updates the PSBTs while checking that the number of partial signatures is correct
        /// </summary>
        /// <param name="walletWithdrawalRequest"></param>
        /// <param name="nbxplorerClient"></param>
        /// <param name="derivationStrategyBase"></param>
        /// <param name="combinedPSBT"></param>
        /// <param name="network"></param>
        /// <exception cref="ArgumentException"></exception>
        private async Task<PSBT> SignPSBTWithEmbeddedSigner(WalletWithdrawalRequest walletWithdrawalRequest,
            INBXplorerService nbXplorerService, DerivationStrategyBase? derivationStrategyBase, PSBT combinedPSBT,
            Network network, ILogger? logger = null)
        {
            var UTXOs = await nbXplorerService.GetUTXOsAsync(derivationStrategyBase, default);
            UTXOs.RemoveDuplicateUTXOs();

            var OutpointKeyPathDictionary =
                UTXOs.Confirmed.UTXOs.ToDictionary(x => x.Outpoint, x => x.KeyPath);

            var txInKeyPathDictionary =
                combinedPSBT.Inputs.Where(x => OutpointKeyPathDictionary.ContainsKey(x.PrevOut))
                    .ToDictionary(x => x,
                        x => OutpointKeyPathDictionary[x.PrevOut]);

            if (!txInKeyPathDictionary.Any())
            {
                const string errorKeypathsForTheUtxosUsedInThisTxAreNotFound =
                    "Error, keypaths for the UTXOs used in this tx are not found, probably this UTXO is already used as input of another transaction";

                _logger.LogError(errorKeypathsForTheUtxosUsedInThisTxAreNotFound);

                throw new ArgumentException(
                    errorKeypathsForTheUtxosUsedInThisTxAreNotFound);
            }

            Dictionary<NBitcoin.OutPoint,NBitcoin.Key> privateKeysForUsedUTXOs;
            if (walletWithdrawalRequest.Wallet.IsHotWallet)
            {
                try
                {
                    privateKeysForUsedUTXOs = txInKeyPathDictionary.ToDictionary(x => x.Key.PrevOut,
                        x =>
                            walletWithdrawalRequest.Wallet.InternalWallet.GetAccountKey(network)
                                .Derive(UInt32.Parse(walletWithdrawalRequest.Wallet.InternalWalletSubDerivationPath))
                                .Derive(x.Value).PrivateKey);
                }
                catch (Exception e)
                {
                    var errorParsingSubderivationPath =
                        $"Invalid Internal Wallet Subderivation Path for wallet:{walletWithdrawalRequest.WalletId}";
                    logger?.LogError(errorParsingSubderivationPath);

                    throw new ArgumentException(
                        errorParsingSubderivationPath);
                }
            }
            else
            {
                privateKeysForUsedUTXOs = txInKeyPathDictionary.ToDictionary(x => x.Key.PrevOut,
                    x =>
                        walletWithdrawalRequest.Wallet.InternalWallet.GetAccountKey(network)
                            .Derive(x.Value).PrivateKey);
            }

            //We need to SIGHASH_ALL all inputs/outputs as fundsmanager to protect the tx from tampering by adding a signature
            var partialSigsCount = combinedPSBT.Inputs.Sum(x => x.PartialSigs.Count);
            foreach (var input in combinedPSBT.Inputs)
            {
                if (privateKeysForUsedUTXOs.TryGetValue(input.PrevOut, out var key))
                {
                    input.Sign(key);
                }
            }

            //We check that the partial signatures number has changed, otherwise end inmediately
            var partialSigsCountAfterSignature =
                combinedPSBT.Inputs.Sum(x => x.PartialSigs.Count);

            if (partialSigsCountAfterSignature == 0 ||
                partialSigsCountAfterSignature <= partialSigsCount)
            {
                var invalidNoOfPartialSignatures =
                    $"Invalid expected number of partial signatures after signing for the wallet withdrawal request:{walletWithdrawalRequest.Id}";
                _logger.LogError(invalidNoOfPartialSignatures);

                throw new ArgumentException(
                    invalidNoOfPartialSignatures);
            }

            return combinedPSBT;
        }

        public async Task MonitorWithdrawals()
        {
            _logger.LogInformation($"Job {nameof(MonitorWithdrawals)} started");
            var withdrawalsPending = await _walletWithdrawalRequestRepository.GetOnChainPendingWithdrawals();

            foreach (var walletWithdrawalRequest in withdrawalsPending)
            {
                if (!string.IsNullOrEmpty(walletWithdrawalRequest.TxId))
                {
                    try
                    {
                        //Let's check if the minimum amount of confirmations are established

                        var getTxResult = await _nbXplorerService.GetTransactionAsync(uint256.Parse(walletWithdrawalRequest.TxId), default);

                        if (getTxResult.Confirmations >= Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS)
                        {
                            walletWithdrawalRequest.Status = WalletWithdrawalRequestStatus.OnChainConfirmed;

                            var updateResult = _walletWithdrawalRequestRepository.Update(walletWithdrawalRequest);

                            if (!updateResult.Item1)
                            {
                                _logger.LogError(
                                    "Error while updating wallet withdrawal: {RequestId}, status: {RequestStatus}",
                                    walletWithdrawalRequest.Id,
                                    walletWithdrawalRequest.Status);
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "Updating wallet withdrawal: {RequestId} to status: {RequestStatus}",
                                    walletWithdrawalRequest.Id, walletWithdrawalRequest.Status);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error while monitoring TxId: {TxId}", walletWithdrawalRequest.TxId);
                    }
                }
            }

            _logger.LogInformation($"Job {nameof(MonitorWithdrawals)} ended");
        }
    }

    public interface IBitcoinService
    {
        /// <summary>
        /// Gets the confirmed wallet balance
        /// </summary>
        /// <param name="wallet"></param>
        /// <returns>decimal(btc) and long(sats) amount of confirmed balance</returns>
        Task<(decimal, long)> GetWalletConfirmedBalance(Wallet wallet);

        /// <summary>
        /// Generates a template PSBT for others approvers to sign for wallet withdrawal requests, nodeguard will sign here if required(n==m)
        /// </summary>
        /// <param name="walletWithdrawalRequest"></param>
        /// <returns>A PSBT and a boolean indicating if there was enough utxos available</returns>
        Task<(PSBT?, bool)> GenerateTemplatePSBT(WalletWithdrawalRequest walletWithdrawalRequest);

        /// <summary>
        /// Broadcast a withdrawal request tx through nbxplorer
        /// </summary>
        /// <param name="walletWithdrawalRequest"></param>
        /// <returns></returns>
        Task PerformWithdrawal(WalletWithdrawalRequest walletWithdrawalRequest);

        /// <summary>
        /// Background job that checks the status of the txIds to update the withdrawal status
        /// </summary>
        /// <returns></returns>
        Task MonitorWithdrawals();
    }
}