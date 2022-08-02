using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using System.Security.Cryptography;
using FundsManager.Data;
using FundsManager.Helpers;
using Microsoft.EntityFrameworkCore;
using Channel = FundsManager.Data.Models.Channel;

namespace FundsManager.Services
{
    public interface ILightningService
    {
        /// <summary>
        /// Opens a channel based on a presigned psbt with inputs, this method waits for I/O on the blockchain, therefore it can last its execution for minutes
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="presignedPSBT"></param>
        /// <returns></returns>
        public Task OpenChannel(ChannelOperationRequest channelOperationRequest, PSBT presignedPSBT);

        /// <summary>
        /// Generates a template PSBT with Sighash_NONE and some UTXOs from the wallet related to the request without signing
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="destinationAddress"></param>
        /// <returns></returns>
        public Task<PSBT?> GenerateTemplatePSBT(ChannelOperationRequest channelOperationRequesd);

        /// <summary>
        /// Based on a channel operation request of type close, requests the close of a channel to LND without acking the request.
        /// This method waits for I/O on the blockchain, therefore it can last its execution for minutes
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="forceClose"></param>
        /// <returns></returns>
        public Task<bool> CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false);

        /// <summary>
        /// Gets the wallet balance
        /// </summary>
        /// <param name="wallet"></param>
        /// <returns></returns>
        public Task<GetBalanceResponse?> GetWalletBalance(Wallet wallet);

        /// <summary>
        /// Gets the wallet balance
        /// </summary>
        /// <param name="wallet"></param>
        /// <param name="derivationFeature"></param>
        /// <returns></returns>
        public Task<BitcoinAddress?> GetUnusedAddress(Wallet wallet,
            NBXplorer.DerivationStrategy.DerivationFeature derivationFeature);

        /// <summary>
        /// Gets the info about a node in the lightning network graph
        /// </summary>
        /// <param name="pubkey"></param>
        /// <returns></returns>
        public Task<LightningNode?> GetNodeInfo(string pubkey);
    }

    /// <summary>d
    public class LightningService : ILightningService
    {
        private readonly ILogger<LightningService> _logger;
        private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
        private readonly INodeRepository _nodeRepository;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public LightningService(ILogger<LightningService> logger,
            IChannelOperationRequestRepository channelOperationRequestRepository,
            INodeRepository nodeRepository,
            IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _logger = logger;
            _channelOperationRequestRepository = channelOperationRequestRepository;
            _nodeRepository = nodeRepository;
            _dbContextFactory = dbContextFactory;
        }

        public async Task OpenChannel(ChannelOperationRequest channelOperationRequest, PSBT presignedPSBT)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            if (channelOperationRequest.RequestType != OperationRequestType.Open)
            {
                const string aNodeCannotOpenAChannelToHimself = "A node cannot open a channel to himself.";
                _logger.LogError(aNodeCannotOpenAChannelToHimself);
                throw new ArgumentException(aNodeCannotOpenAChannelToHimself);
            }

            await using var context = await _dbContextFactory.CreateDbContextAsync();

            var source = channelOperationRequest.SourceNode;
            var destination = channelOperationRequest.DestNode;

            if (source.PubKey == destination?.PubKey)
            {
                const string aNodeCannotOpenAChannelToHimself = "A node cannot open a channel to himself.";
                _logger.LogError(aNodeCannotOpenAChannelToHimself);
                throw new ArgumentException(aNodeCannotOpenAChannelToHimself);
            }

            //Setup of grpc lnd api client (Lightning.proto)
            //Hack to allow self-signed https grpc calls
            var httpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            using var grpcChannel = GrpcChannel.ForAddress($"https://{source.Endpoint}",
                new GrpcChannelOptions { HttpHandler = httpHandler });

            var client = new Lightning.LightningClient(grpcChannel);

            //TODO Return a human-readable error string for the UI
            var result = false;

            //32 bytes of secure randomness for the pending channel id (lnd)

            var pendingChannelId = RandomNumberGenerator.GetBytes(32);

            var pendingChannelIdHex = Convert.ToHexString(pendingChannelId);

            var (network, nbxplorerClient) = GenerateNetwork(_logger);

            //Derivation strategy for the multisig address based on its wallet
            var (derivationStrategyBase, internalWalletDerivationStrategy) = GetDerivationStrategy(channelOperationRequest, network);

            if (derivationStrategyBase == null)
            {
                var derivationNull = $"Derivation scheme not found for wallet:{channelOperationRequest.Wallet.Id}";
                _logger.LogError(derivationNull);

                throw new ArgumentException(
                    derivationNull);
            }

            var closeAddress = await nbxplorerClient.GetUnusedAsync(derivationStrategyBase, DerivationFeature.Deposit, 0, true);

            if (closeAddress == null)
            {
                var closeAddressNull = $"Closing address was null for an operation on wallet:{channelOperationRequest.Wallet.Id}";
                _logger.LogError(closeAddressNull);

                throw new ArgumentException(
                    closeAddressNull);
            }

            _logger.LogInformation("Channel open request for  request id:{} from node:{} to node:{}",
                channelOperationRequest.Id,
                source.Name,
                destination.Name);
            try
            {
                //We prepare the request (shim) with the base PSBT we had presigned with the UTXOs to fund the channel
                var openChannelRequest = new OpenChannelRequest
                {
                    FundingShim = new FundingShim
                    {
                        PsbtShim = new PsbtShim
                        {
                            BasePsbt = ByteString.FromBase64(presignedPSBT.ToBase64()),
                            NoPublish = false,
                            PendingChanId = ByteString.CopyFrom(pendingChannelId)
                        }
                    },
                    LocalFundingAmount = channelOperationRequest.SatsAmount,
                    CloseAddress = closeAddress?.Address.ToString(),
                    Private = channelOperationRequest.IsChannelPrivate,
                    NodePubkey = ByteString.CopyFrom(Convert.FromHexString(destination.PubKey)),
                };

                //We launch a openstatusupdate stream for all the events when calling OpenChannel api method from LND
                var openStatusUpdateStream = client.OpenChannel(openChannelRequest,
                    new Metadata { { "macaroon", source.ChannelAdminMacaroon } }
                );

                await foreach (var response in openStatusUpdateStream.ResponseStream.ReadAllAsync())
                {
                    switch (response.UpdateCase)
                    {
                        case OpenStatusUpdate.UpdateOneofCase.None:
                            break;

                        case OpenStatusUpdate.UpdateOneofCase.ChanPending:
                            //Channel funding tx on mempool and pending status on lnd

                            _logger.LogInformation("Channel pending for channel operation request id:{} for pending channel id:{}",
                                channelOperationRequest.Id, pendingChannelIdHex);

                            channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmationPending;
                            _channelOperationRequestRepository.Update(channelOperationRequest);

                            break;

                        case OpenStatusUpdate.UpdateOneofCase.ChanOpen:
                            _logger.LogInformation("Channel opened for channel operation request request id:{} channel point:{}",
                                channelOperationRequest.Id, response.ChanOpen.ChannelPoint.ToString());

                            channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmed;
                            _channelOperationRequestRepository.Update(channelOperationRequest);

                            var channel = new Channel
                            {
                                ChannelId = pendingChannelIdHex,
                                CreationDatetime = DateTimeOffset.Now,
                                FundingTx = Convert.ToHexString(response.ChanOpen.ChannelPoint.FundingTxidBytes
                                        .ToByteArray()
                                        .Reverse()//Endianness of the txidbytes is different we need to reverse
                                        .ToArray())
                                    .ToLower(),
                                FundingTxOutputIndex = response.ChanOpen.ChannelPoint.OutputIndex,
                                BtcCloseAddress = closeAddress?.Address.ToString(),
                                SatsAmount = channelOperationRequest.SatsAmount,
                                UpdateDatetime = DateTimeOffset.Now,
                            };

                            await context.AddAsync(channel);

                            var addChannelResult = (await context.SaveChangesAsync()) > 0;

                            if (addChannelResult == false)
                            {
                                _logger.LogError(
                                    "Channel for channel operation request id:{} could not be created, reason:{}",
                                    channelOperationRequest.Id,
                                    "Could not persist to db");
                            }

                            channelOperationRequest.ChannelId = channel.Id;
                            channelOperationRequest.DestNode = null;

                            var channelUpdate = _channelOperationRequestRepository.Update(channelOperationRequest);

                            if (channelUpdate.Item1 == false)
                            {
                                _logger.LogError(
                                    "Could not assign channel id to channel operation request:{} reason:{}",
                                    channelOperationRequest.Id,
                                    channelUpdate.Item2);
                            }

                            result = true;

                            break;

                        case OpenStatusUpdate.UpdateOneofCase.PsbtFund:

                            //We got the funded PSBT, we need to tweak the tx outputs and mimick lnd-cli calls
                            var hexPSBT = Convert.ToHexString((response.PsbtFund.Psbt.ToByteArray()));
                            if (PSBT.TryParse(hexPSBT, network,
                                    out var fundedPSBT))
                            {
                                fundedPSBT.AssertSanity();

                                //We ensure to SigHash.None
                                fundedPSBT.Settings.SigningOptions = new SigningOptions
                                {
                                    SigHash = SigHash.None
                                };

                                var channelfundingTx = fundedPSBT.GetGlobalTransaction();

                                //We manually fix the change (it was wrong from the Base template due to nbitcoin requiring a change on a PSBT)

                                var totalIn = new Money(0L);

                                foreach (var input in fundedPSBT.Inputs)
                                {
                                    totalIn += (input.GetTxOut()?.Value);
                                }

                                var totalOut = new Money(channelOperationRequest.SatsAmount, MoneyUnit.Satoshi);
                                var totalFees = presignedPSBT.GetFee();
                                channelfundingTx.Outputs[0].Value = totalIn - totalOut - totalFees;

                                await nbxplorerClient.GetUnusedAsync(
                                    internalWalletDerivationStrategy, DerivationFeature.Deposit);

                                //We get the UTXO keyPath / derivation path from nbxplorer

                                var UTXOs = await nbxplorerClient.GetUTXOsAsync(derivationStrategyBase);
                                UTXOs.RemoveDuplicateUTXOs();

                                var OutpointKeyPathDictionary = UTXOs.Confirmed.UTXOs.ToDictionary(x => x.Outpoint, x => x.KeyPath);

                                var txInKeyPathDictionary =
                                    channelfundingTx.Inputs.Where(x => OutpointKeyPathDictionary.ContainsKey(x.PrevOut))
                                        .ToDictionary(x => x,
                                            x => OutpointKeyPathDictionary[x.PrevOut]);

                                if (!txInKeyPathDictionary.Any())
                                {
                                    const string errorKeypathsForTheUtxosUsedInThisTxAreNotFound = "Error, keypaths for the UTXOs used in this tx are not found";

                                    _logger.LogError(errorKeypathsForTheUtxosUsedInThisTxAreNotFound);

                                    CancelPendingChannel(source, client, pendingChannelId);

                                    throw new ArgumentException(
                                        errorKeypathsForTheUtxosUsedInThisTxAreNotFound);
                                }

                                var privateKeysForUsedUTXOs = txInKeyPathDictionary.ToDictionary(x => x.Key.PrevOut, x =>
                                    channelOperationRequest.Wallet.InternalWallet.GetAccountKey(network)
                                        .Derive(x.Value).PrivateKey);

                                //We merge fundedPSBT with the other PSBT with the change fixed

                                var changeFixedPSBT = channelfundingTx.CreatePSBT(network).UpdateFrom(fundedPSBT);

                                //We need to SIGHASH_ALL all inputs/outputs as fundsmanager to protect the tx from tampering by adding a signature
                                var partialSigsCount = changeFixedPSBT.Inputs.Sum(x => x.PartialSigs.Count);
                                foreach (var input in changeFixedPSBT.Inputs)
                                {
                                    if (privateKeysForUsedUTXOs.TryGetValue(input.PrevOut, out var key))
                                    {
                                        input.Sign(key);
                                    }
                                }
                                //We check that the partial signatures number has changed, otherwise finalize inmediately
                                var partialSigsCountAfterSignature = changeFixedPSBT.Inputs.Sum(x => x.PartialSigs.Count);

                                if (partialSigsCountAfterSignature == 0 ||
                                    partialSigsCountAfterSignature <= partialSigsCount)
                                {
                                    var invalidNoOfPartialSignatures = $"Invalid expected number of partial signatures after signing for the channel operation request:{channelOperationRequest.Id}";
                                    _logger.LogError(invalidNoOfPartialSignatures);

                                    CancelPendingChannel(source, client, pendingChannelId);

                                    throw new ArgumentException(
                                        invalidNoOfPartialSignatures);
                                }

                                //Sanity check
                                changeFixedPSBT.AssertSanity();

                                channelfundingTx = changeFixedPSBT.GetGlobalTransaction();

                                //Just a check of the tx based on the changeFixedPSBT
                                var checkTx = channelfundingTx.Check();

                                if (checkTx == TransactionCheckResult.Success)
                                {
                                    //We tell lnd to verify the psbt
                                    client.FundingStateStep(
                                         new FundingTransitionMsg
                                         {
                                             PsbtVerify = new FundingPsbtVerify
                                             {
                                                 FundedPsbt = ByteString.CopyFrom(Convert.FromHexString(changeFixedPSBT.ToHex())),
                                                 PendingChanId = ByteString.CopyFrom(pendingChannelId)
                                             }
                                         }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } });

                                    //PSBT marked as verified so time to finalize the PSBT and broadcast the tx

                                    var finalizedPSBT = changeFixedPSBT.Finalize();

                                    //Saving the PSBT in the ChannelOperationRequest collection of PSBTs

                                    channelOperationRequest = await _channelOperationRequestRepository.GetById(channelOperationRequest.Id);

                                    if (channelOperationRequest?.ChannelOperationRequestPsbts != null)
                                    {
                                        channelOperationRequest.ChannelOperationRequestPsbts.Add(new ChannelOperationRequestPSBT
                                        {
                                            IsFinalisedPSBT = true,
                                            CreationDatetime = DateTimeOffset.Now,
                                            PSBT = finalizedPSBT.ToBase64(),
                                        });

                                        var psbtUpdate = _channelOperationRequestRepository.Update(channelOperationRequest);

                                        if (!psbtUpdate.Item1)
                                        {
                                            _logger.LogError("Error while saving the finalised PSBT for channel operation request with id:{}", channelOperationRequest.Id);
                                        }
                                    }

                                    var fundingStateStepResp = await client.FundingStateStepAsync(new FundingTransitionMsg
                                    {
                                        PsbtFinalize = new FundingPsbtFinalize
                                        {
                                            PendingChanId = ByteString.CopyFrom(pendingChannelId),
                                            //FinalRawTx = ByteString.CopyFrom(Convert.FromHexString(finalTxHex)),
                                            SignedPsbt = ByteString.CopyFrom(Convert.FromHexString(finalizedPSBT.ToHex()))
                                        },
                                    }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } });
                                }
                                else
                                {
                                    CancelPendingChannel(source, client, pendingChannelId);
                                }
                            }
                            else
                            {
                                CancelPendingChannel(source, client, pendingChannelId);
                            }
                            break;

                        default:
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Channel open request failed for channel operation request:{} from node:{} to node:{}",
                    channelOperationRequest.Id,
                    source.Name,
                    destination.Name);
                result = false;

                CancelPendingChannel(source, client, pendingChannelId);
                throw;
            }
        }

        /// <summary>
        /// Cancels a pending channel from LND PSBT-based funding of channels
        /// </summary>
        /// <param name="source"></param>
        /// <param name="client"></param>
        /// <param name="pendingChannelId"></param>
        private void CancelPendingChannel(Node source, Lightning.LightningClient client, byte[] pendingChannelId)
        {
            try
            {
                if (pendingChannelId != null)
                {
                    var cancelRequest = new FundingShimCancel
                    {
                        PendingChanId = ByteString.CopyFrom(pendingChannelId)
                    };

                    var cancelResult = client.FundingStateStep(new FundingTransitionMsg
                    {
                        ShimCancel = cancelRequest,
                    },
                            new Metadata { { "macaroon", source.ChannelAdminMacaroon } }
                        );
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while cancelling pending channel with id:{} (hex)", Convert.ToHexString(pendingChannelId));
            }
        }

        public async Task<PSBT?> GenerateTemplatePSBT(ChannelOperationRequest channelOperationRequest)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            PSBT? result = null;

            if (channelOperationRequest.RequestType != OperationRequestType.Open)
            {
                _logger.LogWarning("PSBT Generation cancelled, operation type is not open");

                return null;
            }

            if (channelOperationRequest.Status != ChannelOperationRequestStatus.Pending)
            {
                _logger.LogWarning("PSBT Generation cancelled, operation is not in pending state");
                return null;
            }

            var (nbXplorerNetwork, nbxplorerClient) = GenerateNetwork(_logger);

            if (!(await nbxplorerClient.GetStatusAsync()).IsFullySynched)
            {
                _logger.LogError("Error, nbxplorer not fully synched");
                return null;
            }

            //UTXOs -> they need to be tracked first on nbxplorer to get results!!
            var derivationStrategy = channelOperationRequest.Wallet.GetDerivationStrategy();

            if (derivationStrategy == null)
            {
                return null;
            }

            var multisigCoins = await GetTxInputCoins(channelOperationRequest, nbxplorerClient, derivationStrategy);

            if (multisigCoins == null || !multisigCoins.Any())
            {
                _logger.LogError(
                    "Cannot generate base template PSBT for channel operation request:{}, no UTXOs found for the wallet:{}",
                    channelOperationRequest.IsChannelPrivate,
                    channelOperationRequest.WalletId);

                return null;
            }

            try
            {
                //We got enough inputs to fund the TX so time to build the PSBT, the funding address of the channel will be added later by LND

                var txBuilder = nbXplorerNetwork.CreateTransactionBuilder();

                var feeRateResult = await GetFeeRateResult(nbXplorerNetwork, nbxplorerClient);

                var changeAddress = nbxplorerClient.GetUnused(derivationStrategy, DerivationFeature.Change);

                var builder = txBuilder;
                builder.AddCoins(multisigCoins);

                builder.SetSigningOptions(SigHash.None)
                    .SendAllRemainingToChange()
                    .SetChange(changeAddress.Address)
                    .SendEstimatedFees(feeRateResult.FeeRate);

                result = builder.BuildPSBT(false);

                //TODO Remove hack when https://github.com/MetacoSA/NBitcoin/issues/1112 is fixed
                result.Settings.SigningOptions = new SigningOptions(SigHash.None);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while generating base PSBT");
                throw;
            }
            return result;
        }

        /// <summary>
        /// Returns the fee rate (sat/vb) for a tx, 4 is the value in regtest
        /// </summary>
        /// <param name="nbXplorerNetwork"></param>
        /// <param name="nbxplorerClient"></param>
        /// <returns></returns>
        private static async Task<GetFeeRateResult> GetFeeRateResult(Network nbXplorerNetwork, ExplorerClient nbxplorerClient)
        {
            GetFeeRateResult feeRateResult;
            if (nbXplorerNetwork == Network.RegTest)
            {
                feeRateResult = new GetFeeRateResult
                {
                    BlockCount = 1,
                    FeeRate = new FeeRate(4M)
                };
            }
            else
            {
                feeRateResult = await nbxplorerClient.GetFeeRateAsync(1);
            }

            return feeRateResult;
        }

        /// <summary>
        /// Gets the derivation strategy for the request
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="nbXplorerNetwork"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        private (DerivationStrategyBase?, DerivationStrategyBase?) GetDerivationStrategy(ChannelOperationRequest channelOperationRequest,
            Network nbXplorerNetwork)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            var walletKeys = channelOperationRequest?.Wallet?.Keys;

            if (channelOperationRequest.Wallet != null)
            {
                var multiSigDerivationStrategy = channelOperationRequest.Wallet.GetDerivationStrategy();

                var internalWalletXPUB = new BitcoinExtPubKey(channelOperationRequest.Wallet.InternalWallet.GetXPUB(nbXplorerNetwork), nbXplorerNetwork);
                var internalWalletDerivationStrategy = new DirectDerivationStrategy(internalWalletXPUB, true);

                return (multiSigDerivationStrategy, internalWalletDerivationStrategy);
            }

            return (null, null);
        }

        /// <summary>
        /// Generates the ExplorerClient for using nbxplorer based on a bitcoin networy type
        /// </summary>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static (Network nbXplorerNetwork, ExplorerClient nbxplorerClient) GenerateNetwork(ILogger logger)
        {
            var nbxplorerUri = Environment.GetEnvironmentVariable("NBXPLORER_URI") ??
                               throw new ArgumentNullException("Environment.GetEnvironmentVariable(\"NBXPLORER_URI\")");

            //Nbxplorer api client

            var nbXplorerNetwork = CurrentNetworkHelper.GetCurrentNetwork();

            var provider = new NBXplorerNetworkProvider(nbXplorerNetwork.ChainName);
            var nbxplorerClient = new ExplorerClient(provider.GetFromCryptoCode(nbXplorerNetwork.NetworkSet.CryptoCode),
                new Uri(nbxplorerUri));
            return (nbXplorerNetwork, nbxplorerClient);
        }

        /// <summary>
        /// Gets UTXOs confirmed from the wallet of the request
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="nbxplorerClient"></param>
        /// <param name="derivationStrategy"></param>
        /// <returns></returns>
        private async Task<List<ScriptCoin>> GetTxInputCoins(ChannelOperationRequest channelOperationRequest, ExplorerClient nbxplorerClient,
            DerivationStrategyBase derivationStrategy)
        {
            var utxoChanges = await nbxplorerClient.GetUTXOsAsync(derivationStrategy);
            utxoChanges.RemoveDuplicateUTXOs();

            if (utxoChanges == null || !utxoChanges.Confirmed.UTXOs.Any())
            {
                _logger.LogError("PSBT cannot be generated, no UTXOs are available for walletId:{}",
                    channelOperationRequest.WalletId);
                return new List<ScriptCoin>();
            }

            var utxosStack = new Stack<UTXO>(utxoChanges.Confirmed.UTXOs.OrderByDescending(x => x.Confirmations));

            //FIFO Algorithm to match the amount, oldest UTXOs are first taken

            var totalUTXOsConfirmedSats = utxosStack.Sum(x => ((Money)x.Value).Satoshi);

            if (totalUTXOsConfirmedSats < channelOperationRequest.SatsAmount)
            {
                _logger.LogError(
                    "Error, the total UTXOs set balance for walletid:{} ({} sats) is less than the channel amount request ({}sats)",
                    channelOperationRequest.WalletId, totalUTXOsConfirmedSats, channelOperationRequest.SatsAmount);
                return new List<ScriptCoin>();
            }

            var utxosSatsAmountAccumulator = 0M;

            var selectedUTXOs = new List<UTXO>();

            while (channelOperationRequest.SatsAmount >= utxosSatsAmountAccumulator)
            {
                if (utxosStack.TryPop(out var utxo))
                {
                    selectedUTXOs.Add(utxo);
                    utxosSatsAmountAccumulator += ((Money)utxo.Value).Satoshi;
                }
            }

            //UTXOS to Enumerable of ICOINS

            var coins = selectedUTXOs.Select(x => x.AsCoin(derivationStrategy).ToScriptCoin(x.ScriptPubKey))
                .ToList();
            return coins;
        }

        public async Task<bool> CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            if (channelOperationRequest.RequestType != OperationRequestType.Close)
                return false;

            var result = true;

            _logger.LogInformation("Channel close request for request id:{}",
                channelOperationRequest.Id);
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            try
            {
                if (channelOperationRequest.ChannelId != null)
                {
                    var channel = context.Channels.SingleOrDefault(x => x.Id == channelOperationRequest.ChannelId);

                    if (channel != null && channelOperationRequest.SourceNode.ChannelAdminMacaroon != null)
                    {
                        using var grpcChannel = GrpcChannel.ForAddress($"https://{channelOperationRequest.SourceNode.Endpoint}",
                            new GrpcChannelOptions
                            {
                                HttpHandler = new HttpClientHandler
                                {
                                    ServerCertificateCustomValidationCallback =
                                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                                }
                            });

                        var client = new Lightning.LightningClient(grpcChannel);

                        //Time to close the channel
                        var closeChannelResult = client.CloseChannel(new CloseChannelRequest
                        {
                            ChannelPoint = new ChannelPoint
                            {
                                FundingTxidStr = channel.FundingTx,
                                OutputIndex = channel.FundingTxOutputIndex
                            },
                            Force = forceClose,
                        }, new Metadata { { "macaroon", channelOperationRequest.SourceNode.ChannelAdminMacaroon } });

                        _logger.LogInformation("Channel close request:{} triggered",
                            channelOperationRequest.Id);

                        //This is is I/O bounded to the blockchain block time
                        await foreach (var response in closeChannelResult.ResponseStream.ReadAllAsync())
                        {
                            switch (response.UpdateCase)
                            {
                                case CloseStatusUpdate.UpdateOneofCase.None:
                                    break;

                                case CloseStatusUpdate.UpdateOneofCase.ClosePending:
                                    var closePendingTxid = Convert.ToHexString(response.ClosePending.Txid.ToByteArray().Reverse().ToArray()).ToLower();

                                    _logger.LogInformation(
                                        "Channel close request in status:{} for channel operation request:{} for channel:{} closing txId:{}",
                                        ChannelOperationRequestStatus.OnChainConfirmed,
                                        channelOperationRequest.Id,
                                        channel.Id,
                                        closePendingTxid);

                                    channelOperationRequest.Status =
                                        ChannelOperationRequestStatus.OnChainConfirmationPending;

                                    var onChainPendingUpdate = _channelOperationRequestRepository.Update(channelOperationRequest);

                                    if (onChainPendingUpdate.Item1 == false)
                                    {
                                        _logger.LogError("Error while updating channel operation request id:{} to status:{}", channelOperationRequest.Id, ChannelOperationRequestStatus.OnChainConfirmationPending);
                                    }
                                    break;

                                case CloseStatusUpdate.UpdateOneofCase.ChanClose:

                                    //TODO Review why chanclose.success it is false for confirmed closings of channels
                                    var chanCloseClosingTxid = Convert.ToHexString(response.ChanClose.ClosingTxid.ToByteArray().Reverse().ToArray()).ToLower();
                                    _logger.LogInformation(
                                        "Channel close request in status:{} for channel operation request:{} for channel:{} closing txId:{}",
                                        ChannelOperationRequestStatus.OnChainConfirmed,
                                        channelOperationRequest.Id,
                                        channel.Id,
                                        chanCloseClosingTxid);

                                    channelOperationRequest.Status =
                                        ChannelOperationRequestStatus.OnChainConfirmed;

                                    var onChainConfirmedUpdate = _channelOperationRequestRepository.Update(channelOperationRequest);

                                    if (onChainConfirmedUpdate.Item1 == false)
                                    {
                                        _logger.LogError(
                                            "Error while updating channel operation request id:{} to status:{}",
                                            channelOperationRequest.Id,
                                            ChannelOperationRequestStatus.OnChainConfirmationPending);
                                    }

                                    channel.Status = Channel.ChannelStatus.Closed;

                                    //Null to avoid creation of entities
                                    channel.ChannelOperationRequests = null;

                                    context.Update(channel);

                                    var closedChannelUpdateResult = await context.SaveChangesAsync() > 0;

                                    if (!closedChannelUpdateResult)
                                    {
                                        _logger.LogError("Error while setting to closed status a closed channel with id:{}", channel.Id);
                                    }

                                    result = true;

                                    break;

                                default:
                                    throw new ArgumentOutOfRangeException();
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Channel close request failed for channel operation request:{}",
                    channelOperationRequest.Id);
                result = false;
            }

            return result;
        }

        public async Task<GetBalanceResponse?> GetWalletBalance(Wallet wallet)
        {
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));
            var client = GenerateNetwork(_logger);
            GetBalanceResponse? getBalanceResponse = null;
            try
            {
                getBalanceResponse = await client.nbxplorerClient.GetBalanceAsync(wallet.GetDerivationStrategy());
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while getting wallet balance for wallet:{}", wallet.Id);
            }

            return getBalanceResponse;
        }

        public async Task<BitcoinAddress?> GetUnusedAddress(Wallet wallet,
            DerivationFeature derivationFeature)
        {
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));

            var client = GenerateNetwork(_logger);
            KeyPathInformation? keyPathInformation = null;
            try
            {
                keyPathInformation = await client.nbxplorerClient.GetUnusedAsync(wallet.GetDerivationStrategy(), derivationFeature);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while getting wallet balance for wallet:{}", wallet.Id);
            }

            var result = keyPathInformation?.Address ?? null;

            return result;
        }

        public async Task<LightningNode?> GetNodeInfo(string pubkey)
        {
            if (string.IsNullOrWhiteSpace(pubkey))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(pubkey));

            LightningNode? result = null;

            var node = (await _nodeRepository.GetAllManagedByFundsManager()).FirstOrDefault();
            if (node == null)
                node = (await _nodeRepository.GetAllManagedByFundsManager()).LastOrDefault();

            if (node == null)
            {
                _logger.LogError("No managed node found on the system");
                return result;
            }

            using var grpcChannel = GrpcChannel.ForAddress($"https://{node.Endpoint}",
                new GrpcChannelOptions
                {
                    HttpHandler = new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    }
                });

            var client = new Lightning.LightningClient(grpcChannel);
            try
            {
                var nodeInfo = await client.GetNodeInfoAsync(new NodeInfoRequest
                {
                    PubKey = pubkey,
                    IncludeChannels = false
                }, new Metadata { { "macaroon", node.ChannelAdminMacaroon } });

                result = nodeInfo?.Node;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while obtaining node info for node with pubkey:{}", pubkey);
            }

            return result;
        }
    }
}