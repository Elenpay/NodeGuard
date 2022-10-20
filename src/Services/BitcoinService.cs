using AutoMapper;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Hangfire;
using Humanizer;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;

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

        public BitcoinService(ILogger<BitcoinService> logger,
            IFMUTXORepository fmutxoRepository,
            IMapper mapper,
            IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository,
            IWalletWithdrawalRequestPsbtRepository walletWithdrawalRequestPsbtRepository,
            INodeRepository nodeRepository)
        {
            _logger = logger;
            _fmutxoRepository = fmutxoRepository;
            _mapper = mapper;
            _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
            _walletWithdrawalRequestPsbtRepository = walletWithdrawalRequestPsbtRepository;
            _nodeRepository = nodeRepository;
        }

        public async Task<(decimal, long)> GetWalletConfirmedBalance(Wallet wallet)
        {
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));

            var (_, explorerClient) = LightningHelper.GenerateNetwork();

            var balance = await explorerClient.GetBalanceAsync(wallet.GetDerivationStrategy());
            var confirmedBalanceMoney = (Money)balance.Confirmed;

            return (confirmedBalanceMoney.ToUnit(MoneyUnit.BTC), confirmedBalanceMoney.Satoshi);
        }

        public async Task<(PSBT?, bool)> GenerateTemplatePSBT(WalletWithdrawalRequest walletWithdrawalRequest)
        {
            if (walletWithdrawalRequest == null) throw new ArgumentNullException(nameof(walletWithdrawalRequest));

            (PSBT?, bool) result = (null, false);
            if (walletWithdrawalRequest.Status != WalletWithdrawalRequestStatus.Pending
                && walletWithdrawalRequest.Status != WalletWithdrawalRequestStatus.PSBTSignaturesPending)
            {
                _logger.LogError("PSBT Generation cancelled, operation is not in pending state");
                return (null, false);
            }

            var (nbXplorerNetwork, nbxplorerClient) = LightningHelper.GenerateNetwork();

            if (!(await nbxplorerClient.GetStatusAsync()).IsFullySynched)
            {
                _logger.LogError("Error, nbxplorer not fully synched");
                return (null, false);
            }

            var derivationStrategy = walletWithdrawalRequest.Wallet.GetDerivationStrategy();

            if (derivationStrategy == null)
            {
                _logger.LogError("Error while getting the derivation strategy scheme for wallet:{}",
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
                var currentUtxos = await nbxplorerClient.GetUTXOsAsync(derivationStrategy);
                if (parsedTemplatePSBT.Inputs.All(
                        x => currentUtxos.Confirmed.UTXOs.Select(x => x.Outpoint).Contains(x.PrevOut)))
                {
                    return (parsedTemplatePSBT, false);
                }
                else
                {
                    //We mark the request as failed since we would need to invalidate existing PSBTs
                    _logger.LogError(
                        "Marking the withdrawal request:{} as failed since the original UTXOs are no longer valid",
                        walletWithdrawalRequest.Id);

                    walletWithdrawalRequest.Status = WalletWithdrawalRequestStatus.Failed;

                    var updateResult = _walletWithdrawalRequestRepository.Update(walletWithdrawalRequest);

                    if (!updateResult.Item1)
                    {
                        _logger.LogError("Error while updating withdrawal request:{}", walletWithdrawalRequest.Id);
                    }

                    return (null, false);
                }
            }

            var utxoChanges = await nbxplorerClient.GetUTXOsAsync(derivationStrategy);
            utxoChanges.RemoveDuplicateUTXOs();

            var lockedUtxOs = await _fmutxoRepository.GetLockedUTXOs(ignoredWalletWithdrawalRequestId: walletWithdrawalRequest.Id);

            //If the request is a full funds withdrawal, calculate the amount to the existing balance
            if (walletWithdrawalRequest.WithdrawAllFunds)
            {
                var balanceResponse = await nbxplorerClient.GetBalanceAsync(derivationStrategy);

                walletWithdrawalRequest.Amount = ((Money)balanceResponse.Confirmed).ToUnit(MoneyUnit.BTC);

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
                    "Cannot generate base template PSBT for withdrawal request:{}, no UTXOs found for the wallet:{}",
                    walletWithdrawalRequest.Id, walletWithdrawalRequest.Wallet.Id);

                return (null, true); //true means no UTXOS
            }

            try
            {
                //We got enough inputs to fund the TX so time to build the PSBT

                var txBuilder = nbXplorerNetwork.CreateTransactionBuilder();

                var feeRateResult = await LightningHelper.GetFeeRateResult(nbXplorerNetwork, nbxplorerClient);

                var changeAddress = await nbxplorerClient.GetUnusedAsync(derivationStrategy, DerivationFeature.Change);

                if (changeAddress == null)
                {
                    _logger.LogError("Change address was not found for wallet:{}", walletWithdrawalRequest.Wallet.Id);
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
                    _logger.LogError("Error while saving template PSBT to wallet withdrawal request:{}", walletWithdrawalRequest.Id);
                }
            }

            return result;
        }

        public async Task PerformWithdrawal(WalletWithdrawalRequest walletWithdrawalRequest)
        {
            if (walletWithdrawalRequest == null) throw new ArgumentNullException(nameof(walletWithdrawalRequest));

            if (walletWithdrawalRequest.Status != WalletWithdrawalRequestStatus.PSBTSignaturesPending)
            {
                _logger.LogError("Invalid status for broadcasting the tx from wallet withdrawal request:{} status:{}",
                    walletWithdrawalRequest.Id,
                    walletWithdrawalRequest.Status);

                throw new ArgumentException("Invalid status");
            }

            //Update
            walletWithdrawalRequest = await _walletWithdrawalRequestRepository.GetById(walletWithdrawalRequest.Id) ?? throw new InvalidOperationException();

            var signedPSBTStrings = walletWithdrawalRequest.WalletWithdrawalRequestPSBTs.Where(x =>
                    !x.IsFinalisedPSBT && !x.IsInternalWalletPSBT && !x.IsTemplatePSBT)
                .Select(x => x.PSBT).ToList();

            var combinedPSBT = LightningHelper.CombinePSBTs(signedPSBTStrings, _logger);

            try
            {
                if (combinedPSBT == null)
                {
                    var invalidPsbtNullToBeUsedForTheRequest =
                        $"Invalid combined PSBT(null) to be used for the wallet withdrawal request:{walletWithdrawalRequest.Id}";
                    _logger.LogError(invalidPsbtNullToBeUsedForTheRequest);

                    throw new ArgumentException(invalidPsbtNullToBeUsedForTheRequest, nameof(combinedPSBT));
                }

                var (network, nbxplorerClient) = LightningHelper.GenerateNetwork();

                var derivationStrategyBase = walletWithdrawalRequest.Wallet.GetDerivationStrategy();

                //Check if the FundsManager Internal Wallet needs to sign on his own
                if (!walletWithdrawalRequest.AreAllRequiredSignaturesCollected &&
                    walletWithdrawalRequest.Wallet.RequiresInternalWalletSigning)
                {
                    var UTXOs = await nbxplorerClient.GetUTXOsAsync(derivationStrategyBase);
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

                    var privateKeysForUsedUTXOs = txInKeyPathDictionary.ToDictionary(x => x.Key.PrevOut,
                        x =>
                            walletWithdrawalRequest.Wallet.InternalWallet.GetAccountKey(network)
                                .Derive(x.Value).PrivateKey);

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
                }

                //PSBT finalisation
                var finalisedPSBT = combinedPSBT.Finalize();

                finalisedPSBT.AssertSanity();

                if (!finalisedPSBT.CanExtractTransaction())
                {
                    var cannotFinalisedCombinedPsbtForWithdrawalRequestId =
                        $"Cannot finalised combined PSBT for withdrawal request id:{walletWithdrawalRequest.Id}";
                    _logger.LogError(cannotFinalisedCombinedPsbtForWithdrawalRequestId);

                    throw new ArgumentException(cannotFinalisedCombinedPsbtForWithdrawalRequestId,
                        nameof(finalisedPSBT));
                }

                var tx = finalisedPSBT.ExtractTransaction();

                var transactionCheckResult = tx.Check();
                if (transactionCheckResult != TransactionCheckResult.Success)
                {
                    _logger.LogError("Invalid tx check reason:{}", transactionCheckResult.Humanize());
                }

                var node = (await _nodeRepository.GetAllManagedByFundsManager()).FirstOrDefault();

                if (node == null)
                {
                    var noManagedFoundFoundForWithdrawalRequestId =
                        $"No managed node found for withdrawal request id:{walletWithdrawalRequest.Id}";
                    _logger.LogError(noManagedFoundFoundForWithdrawalRequestId);
                    throw new ArgumentException(noManagedFoundFoundForWithdrawalRequestId, nameof(node));
                }

                _logger.LogInformation("Publishing tx for withdrawal request id:{} by node:{} txId:{}",
                    walletWithdrawalRequest.Id,
                    node.Name,
                    tx.GetHash().ToString());

                //TODO Review why LND gives EOF, nbxplorer works flawlessly
                var broadcastAsyncResult = await nbxplorerClient.BroadcastAsync(tx);

                if (!broadcastAsyncResult.Success)
                {
                    _logger.LogError("Failed TX broadcast for withdrawal request id:{} rpcCode:{}, rpcCodeMessage:{}, rpcMessage:{}",
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
                    _logger.LogError("Error while updating the txId of withdrawal request:{}", walletWithdrawalRequest.Id);
                }

                //We track the destination address
                var trackedSourceAddress = TrackedSource.Create(BitcoinAddress.Create(
                    walletWithdrawalRequest.DestinationAddress,
                    CurrentNetworkHelper.GetCurrentNetwork()));

                await nbxplorerClient.TrackAsync(trackedSourceAddress);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while publishing withdrawal request id:{}", walletWithdrawalRequest.Id);
                throw;
            }
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
                        var (network, nbxplorerclient) = LightningHelper.GenerateNetwork();

                        var getTxResult = await nbxplorerclient.GetTransactionAsync(uint256.Parse(walletWithdrawalRequest.TxId));

                        var confirmationBlocks =
                            int.Parse(Environment.GetEnvironmentVariable("TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS") ??
                                      throw new InvalidOperationException());

                        if (getTxResult.Confirmations >= confirmationBlocks)
                        {
                            walletWithdrawalRequest.Status = WalletWithdrawalRequestStatus.OnChainConfirmed;

                            var updateResult = _walletWithdrawalRequestRepository.Update(walletWithdrawalRequest);

                            if (!updateResult.Item1)
                            {
                                _logger.LogError("Error while updating wallet withdrawal:{} status:{}",
                                    walletWithdrawalRequest.Id,
                                    walletWithdrawalRequest.Status);
                            }
                            else
                            {
                                _logger.LogInformation("Updating wallet withdrawal:{} to status:{}",
                                    walletWithdrawalRequest.Id, walletWithdrawalRequest.Status);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error while monitoring TxId:{}", walletWithdrawalRequest.TxId);
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
        /// Generates a template PSBT for others approvers to sign for wallet withdrawal requests, the fundsmanager will sign here if required(n==m)
        /// </summary>
        /// <param name="walletWithdrawalRequest"></param>
        /// <returns>A PSBT and a boolean indicating if there was enough utxos available</returns>
        Task<(PSBT?, bool)> GenerateTemplatePSBT(WalletWithdrawalRequest walletWithdrawalRequest);

        /// <summary>
        /// Broadcast a withdrawal request tx through nbxplorer
        /// </summary>
        /// <param name="walletWithdrawalRequest"></param>
        /// <returns></returns>
        [AutomaticRetry(LogEvents = true, Attempts = 10, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        Task PerformWithdrawal(WalletWithdrawalRequest walletWithdrawalRequest);

        /// <summary>
        /// Background job that checks the status of the txIds to update the withdrawal status
        /// </summary>
        /// <returns></returns>
        Task MonitorWithdrawals();
    }
}