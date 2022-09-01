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
using AutoMapper;
using FundsManager.Data;
using FundsManager.Helpers;
using Hangfire;
using Hangfire.States;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Channel = FundsManager.Data.Models.Channel;
using UTXO = NBXplorer.Models.UTXO;

// ReSharper disable InconsistentNaming

// ReSharper disable IdentifierTypo

namespace FundsManager.Services
{
    /// <summary>
    /// Service to interact with LND
    /// </summary>
    public interface ILightningService
    {
        /// <summary>
        /// Opens a channel based on a presigned psbt with inputs, this method waits for I/O on the blockchain, therefore it can last its execution for minutes
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="presignedPSBTbase64"></param>
        /// <returns></returns>
        // ReSharper disable once IdentifierTypo
        public Task OpenChannel(ChannelOperationRequest channelOperationRequest, string presignedPSBTbase64);

        /// <summary>
        /// Generates a template PSBT with Sighash_NONE and some UTXOs from the wallet related to the request without signing, also returns if there are no utxos available at the request time
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="destinationAddress"></param>
        /// <returns></returns>
        public Task<(PSBT?, bool)> GenerateTemplatePSBT(ChannelOperationRequest channelOperationRequest);

        /// <summary>
        /// Based on a channel operation request of type close, requests the close of a channel to LND without acking the request.
        /// This method waits for I/O on the blockchain, therefore it can last its execution for minutes
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="forceClose"></param>
        /// <returns></returns>
        public Task CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false);

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
            DerivationFeature derivationFeature);

        /// <summary>
        /// Gets the info about a node in the lightning network graph
        /// </summary>
        /// <param name="pubkey"></param>
        /// <returns></returns>
        public Task<LightningNode?> GetNodeInfo(string pubkey);

        /// <summary>
        /// Job for the lifetime of the application that intercepts LND channel opening requests to the managed nodes
        /// </summary>
        /// <returns></returns>
        [AutomaticRetry(LogEvents = true, Attempts = Int32.MaxValue)]
        Task ChannelAcceptorJob();

        /// <summary>
        /// Subtask of ChannelAcceptorJob
        /// </summary>
        /// <param name="node"></param>
        /// <param name="loggerFactory"></param>
        /// <returns></returns>
        [AutomaticRetry(LogEvents = true, Attempts = Int32.MaxValue)]
        Task ProcessNodeChannelAcceptorJob(int nodeId);

        /// <summary>
        /// CRON Job which checks if there is any funds on the LND hot wallet and sweep them to the returning multisig wallet (if assigned) to the node.
        /// </summary>
        /// <returns></returns>
        [AutomaticRetry(LogEvents = true, Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
        Task SweepNodeWalletsJob();

        /// <summary>
        /// Sub-job of SweepNodeWalletsJob
        /// </summary>
        /// <param name="managedNodeId"></param>
        /// <returns></returns>
        [AutomaticRetry(LogEvents = true, Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
        Task SweepNodeWalletJob(int managedNodeId);
    }

    public class LightningService : ILightningService
    {
        private readonly ILogger<LightningService> _logger;
        private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
        private readonly INodeRepository _nodeRepository;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IMapper _mapper;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly IWalletRepository _walletRepository;

        public LightningService(ILogger<LightningService> logger,
            IChannelOperationRequestRepository channelOperationRequestRepository,
            INodeRepository nodeRepository,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IMapper mapper,
            IBackgroundJobClient backgroundJobClient,
            IWalletRepository walletRepository)
        {
            _logger = logger;
            _channelOperationRequestRepository = channelOperationRequestRepository;
            _nodeRepository = nodeRepository;
            _dbContextFactory = dbContextFactory;
            _mapper = mapper;
            _backgroundJobClient = backgroundJobClient;
            _walletRepository = walletRepository;
        }

        public async Task OpenChannel(ChannelOperationRequest channelOperationRequest, string? presignedPSBTbase64)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));
            if (string.IsNullOrWhiteSpace(presignedPSBTbase64))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(presignedPSBTbase64));

            if (!PSBT.TryParse(presignedPSBTbase64, CurrentNetworkHelper.GetCurrentNetwork(), out var presignedPSBT))
            {
                const string thePresignedPsbtCouldNotBeParsed = "The presigned PSBT could not be parsed";
                _logger.LogError(thePresignedPsbtCouldNotBeParsed);
                throw new ArgumentException(thePresignedPsbtCouldNotBeParsed, nameof(presignedPSBTbase64));
            }

            if (channelOperationRequest.RequestType != OperationRequestType.Open)
            {
                const string aNodeCannotOpenAChannelToHimself = "A node cannot open a channel to himself.";
                _logger.LogError(aNodeCannotOpenAChannelToHimself);
                throw new ArgumentException(aNodeCannotOpenAChannelToHimself);
            }

            await using var context = await _dbContextFactory.CreateDbContextAsync();

            var source = channelOperationRequest.SourceNode;
            var destination = channelOperationRequest.DestNode;
            if (source == null || destination == null)
            {
                throw new ArgumentException("Source or destination null", nameof(source));
            }

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

            //32 bytes of secure randomness for the pending channel id (lnd)

            var pendingChannelId = RandomNumberGenerator.GetBytes(32);

            var pendingChannelIdHex = Convert.ToHexString(pendingChannelId);

            var (network, nbxplorerClient) = GenerateNetwork(_logger);

            //Derivation strategy for the multisig address based on its wallet
            var (derivationStrategyBase, _) =
                GetDerivationStrategy(channelOperationRequest, network);

            if (derivationStrategyBase == null)
            {
                var derivationNull = $"Derivation scheme not found for wallet:{channelOperationRequest.Wallet.Id}";
                _logger.LogError(derivationNull);

                throw new ArgumentException(
                    derivationNull);
            }

            var closeAddress =
                await nbxplorerClient.GetUnusedAsync(derivationStrategyBase, DerivationFeature.Deposit, 0, true);

            if (closeAddress == null)
            {
                var closeAddressNull =
                    $"Closing address was null for an operation on wallet:{channelOperationRequest.Wallet.Id}";
                _logger.LogError(closeAddressNull);

                throw new ArgumentException(
                    closeAddressNull);
            }

            _logger.LogInformation("Channel open request for  request id:{} from node:{} to node:{}",
                channelOperationRequest.Id,
                source.Name,
                destination?.Name);
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
                    CloseAddress = closeAddress.Address.ToString(),
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

                            _logger.LogInformation(
                                "Channel pending for channel operation request id:{} for pending channel id:{}",
                                channelOperationRequest.Id, pendingChannelIdHex);

                            channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmationPending;
                            channelOperationRequest.TxId = DecodeTxId(response.ChanPending.Txid);
                            _channelOperationRequestRepository.Update(channelOperationRequest);

                            break;

                        case OpenStatusUpdate.UpdateOneofCase.ChanOpen:
                            _logger.LogInformation(
                                "Channel opened for channel operation request request id:{} channel point:{}",
                                channelOperationRequest.Id, response.ChanOpen.ChannelPoint.ToString());

                            channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmed;
                            _channelOperationRequestRepository.Update(channelOperationRequest);

                            var channel = new Channel
                            {
                                ChannelId = pendingChannelIdHex,
                                CreationDatetime = DateTimeOffset.Now,
                                FundingTx = DecodeTxId(response.ChanOpen.ChannelPoint.FundingTxidBytes),
                                FundingTxOutputIndex = response.ChanOpen.ChannelPoint.OutputIndex,
                                BtcCloseAddress = closeAddress?.Address.ToString(),
                                SatsAmount = channelOperationRequest.SatsAmount,
                                UpdateDatetime = DateTimeOffset.Now,
                                Status = Channel.ChannelStatus.Open
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

                                //We get the UTXO keyPath / derivation path from nbxplorer

                                var UTXOs = await nbxplorerClient.GetUTXOsAsync(derivationStrategyBase);
                                UTXOs.RemoveDuplicateUTXOs();

                                var OutpointKeyPathDictionary =
                                    UTXOs.Confirmed.UTXOs.ToDictionary(x => x.Outpoint, x => x.KeyPath);

                                var txInKeyPathDictionary =
                                    channelfundingTx.Inputs.Where(x => OutpointKeyPathDictionary.ContainsKey(x.PrevOut))
                                        .ToDictionary(x => x,
                                            x => OutpointKeyPathDictionary[x.PrevOut]);

                                if (!txInKeyPathDictionary.Any())
                                {
                                    const string errorKeypathsForTheUtxosUsedInThisTxAreNotFound =
                                        "Error, keypaths for the UTXOs used in this tx are not found, probably this UTXO is already used as input of another transaction";

                                    _logger.LogError(errorKeypathsForTheUtxosUsedInThisTxAreNotFound);

                                    CancelPendingChannel(source, client, pendingChannelId);

                                    throw new ArgumentException(
                                        errorKeypathsForTheUtxosUsedInThisTxAreNotFound);
                                }

                                var privateKeysForUsedUTXOs = txInKeyPathDictionary.ToDictionary(x => x.Key.PrevOut,
                                    x =>
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
                                var partialSigsCountAfterSignature =
                                    changeFixedPSBT.Inputs.Sum(x => x.PartialSigs.Count);

                                if (partialSigsCountAfterSignature == 0 ||
                                    partialSigsCountAfterSignature <= partialSigsCount)
                                {
                                    var invalidNoOfPartialSignatures =
                                        $"Invalid expected number of partial signatures after signing for the channel operation request:{channelOperationRequest.Id}";
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
                                                FundedPsbt =
                                                    ByteString.CopyFrom(Convert.FromHexString(changeFixedPSBT.ToHex())),
                                                PendingChanId = ByteString.CopyFrom(pendingChannelId)
                                            }
                                        }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } });

                                    //PSBT marked as verified so time to finalize the PSBT and broadcast the tx

                                    var finalizedPSBT = changeFixedPSBT.Finalize();

                                    //Saving the PSBT in the ChannelOperationRequest collection of PSBTs

                                    channelOperationRequest =
                                        await _channelOperationRequestRepository.GetById(channelOperationRequest.Id) ?? throw new InvalidOperationException();

                                    if (channelOperationRequest?.ChannelOperationRequestPsbts != null)
                                    {
                                        channelOperationRequest.ChannelOperationRequestPsbts.Add(
                                            new ChannelOperationRequestPSBT
                                            {
                                                IsFinalisedPSBT = true,
                                                CreationDatetime = DateTimeOffset.Now,
                                                PSBT = finalizedPSBT.ToBase64(),
                                            });

                                        var psbtUpdate =
                                            _channelOperationRequestRepository.Update(channelOperationRequest);

                                        if (!psbtUpdate.Item1)
                                        {
                                            _logger.LogError(
                                                "Error while saving the finalised PSBT for channel operation request with id:{}",
                                                channelOperationRequest.Id);
                                        }
                                    }

                                    var fundingStateStepResp = await client.FundingStateStepAsync(
                                        new FundingTransitionMsg
                                        {
                                            PsbtFinalize = new FundingPsbtFinalize
                                            {
                                                PendingChanId = ByteString.CopyFrom(pendingChannelId),
                                                //FinalRawTx = ByteString.CopyFrom(Convert.FromHexString(finalTxHex)),
                                                SignedPsbt =
                                                    ByteString.CopyFrom(Convert.FromHexString(finalizedPSBT.ToHex()))
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

                CancelPendingChannel(source, client, pendingChannelId);

                //TODO Mark as failed (?)
                throw;
            }
        }

        private string DecodeTxId(ByteString TxIdBytes)
        {
            return Convert.ToHexString(TxIdBytes
                    .ToByteArray()
                    .Reverse() //Endianness of the txidbytes is different we need to reverse
                    .ToArray())
                .ToLower();
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
                _logger.LogError(e, "Error while cancelling pending channel with id:{} (hex)",
                    Convert.ToHexString(pendingChannelId));
            }
        }

        public async Task<(PSBT?, bool)> GenerateTemplatePSBT(ChannelOperationRequest channelOperationRequest)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            (PSBT?, bool) result = (null, false);

            if (channelOperationRequest.RequestType != OperationRequestType.Open)
            {
                _logger.LogError("PSBT Generation cancelled, operation type is not open");

                return (null, false);
            }

            if (channelOperationRequest.Status != ChannelOperationRequestStatus.Pending)
            {
                _logger.LogError("PSBT Generation cancelled, operation is not in pending state");
                return (null, false);
            }

            var (nbXplorerNetwork, nbxplorerClient) = GenerateNetwork(_logger);

            if (!(await nbxplorerClient.GetStatusAsync()).IsFullySynched)
            {
                _logger.LogError("Error, nbxplorer not fully synched");
                return (null, false);
            }

            //UTXOs -> they need to be tracked first on nbxplorer to get results!!
            var derivationStrategy = channelOperationRequest.Wallet.GetDerivationStrategy();

            if (derivationStrategy == null)
            {
                _logger.LogError("Error while getting the derivation strategy scheme for wallet:{}",
                    channelOperationRequest.Wallet.Id);
                return (null, false);
            }

            var (multisigCoins, selectedUtxOs) =
                await GetTxInputCoins(channelOperationRequest, nbxplorerClient, derivationStrategy);

            if (multisigCoins == null || !multisigCoins.Any())
            {
                _logger.LogError(
                    "Cannot generate base template PSBT for channel operation request:{}, no UTXOs found for the wallet:{}",
                    channelOperationRequest.IsChannelPrivate,
                    channelOperationRequest.WalletId);

                return (null, true); //true means no UTXOS
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

                result.Item1 = builder.BuildPSBT(false);

                //TODO Remove hack when https://github.com/MetacoSA/NBitcoin/issues/1112 is fixed
                result.Item1.Settings.SigningOptions = new SigningOptions(SigHash.None);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while generating base PSBT");
            }

            // We "lock" the PSBT to the channel operation request by adding to its UTXOs collection for later checking
            var utxos = selectedUtxOs.Select(x => _mapper.Map<UTXO, FMUTXO>(x)).ToList();

            var addUTXOSOperation = await _channelOperationRequestRepository.AddUTXOs(channelOperationRequest, utxos);
            if (!addUTXOSOperation.Item1)
            {
                _logger.LogError(
                    $"Could not add the following utxos({utxos.Humanize()}) to op request:{channelOperationRequest.Id}");
            }

            return result;
        }

        /// <summary>
        /// Returns the fee rate (sat/vb) for a tx, 4 is the value in regtest
        /// </summary>
        /// <param name="nbXplorerNetwork"></param>
        /// <param name="nbxplorerClient"></param>
        /// <returns></returns>
        private static async Task<GetFeeRateResult> GetFeeRateResult(Network nbXplorerNetwork,
            ExplorerClient nbxplorerClient)
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
        private (DerivationStrategyBase?, DerivationStrategyBase?) GetDerivationStrategy(
            ChannelOperationRequest channelOperationRequest,
            Network nbXplorerNetwork)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            if (channelOperationRequest.Wallet != null)
            {
                var multiSigDerivationStrategy = channelOperationRequest.Wallet.GetDerivationStrategy();

                var internalWalletXPUB =
                    new BitcoinExtPubKey(channelOperationRequest.Wallet.InternalWallet.GetXPUB(nbXplorerNetwork),
                        nbXplorerNetwork);
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
        private async Task<(List<ScriptCoin> coins, List<UTXO> selectedUTXOs)> GetTxInputCoins(
            ChannelOperationRequest channelOperationRequest,
            ExplorerClient nbxplorerClient,
            DerivationStrategyBase derivationStrategy)
        {
            var utxoChanges = await nbxplorerClient.GetUTXOsAsync(derivationStrategy);
            utxoChanges.RemoveDuplicateUTXOs();

            //Now it is time to remove from the UTXO SET those who are used in a Request with status from Pending to OnChainPending

            var pendingRequests = await _channelOperationRequestRepository.GetPendingRequests();

            pendingRequests.Remove(channelOperationRequest); //The parameted request is not counted for this

            var pendingUTXOs = pendingRequests.SelectMany(x => x.Utxos).ToList();
            var availableUTXOs = new List<UTXO>();
            foreach (var utxo in utxoChanges.Confirmed.UTXOs)
            {
                var fmUtxo = _mapper.Map<UTXO, FMUTXO>(utxo);

                if (pendingUTXOs.Contains(fmUtxo))
                {
                    _logger.LogInformation("Removing UTXO:{} from UTXO set as it is locked", fmUtxo.ToString());
                }
                else
                {
                    availableUTXOs.Add(utxo);
                }
            }

            if (!availableUTXOs.Any())
            {
                _logger.LogError("PSBT cannot be generated, no UTXOs are available for walletId:{}",
                    channelOperationRequest.WalletId);
                return (new List<ScriptCoin>(), new List<UTXO>());
            }

            var utxosStack = new Stack<UTXO>(availableUTXOs.OrderByDescending(x => x.Confirmations));

            //FIFO Algorithm to match the amount, oldest UTXOs are first taken

            var totalUTXOsConfirmedSats = utxosStack.Sum(x => ((Money)x.Value).Satoshi);

            if (totalUTXOsConfirmedSats < channelOperationRequest.SatsAmount)
            {
                _logger.LogError(
                    "Error, the total UTXOs set balance for walletid:{} ({} sats) is less than the channel amount request ({} sats)",
                    channelOperationRequest.WalletId, totalUTXOsConfirmedSats, channelOperationRequest.SatsAmount);
                return (new List<ScriptCoin>(), new List<UTXO>());
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
            return (coins, selectedUTXOs);
        }

        public async Task CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            if (channelOperationRequest.RequestType != OperationRequestType.Close)
                throw new ArgumentException("Channel Operation Request type is not of type Close");

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
                        using var grpcChannel = GrpcChannel.ForAddress(
                            $"https://{channelOperationRequest.SourceNode.Endpoint}",
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
                                    var closePendingTxid = DecodeTxId(response.ClosePending.Txid);

                                    _logger.LogInformation(
                                        "Channel close request in status:{} for channel operation request:{} for channel:{} closing txId:{}",
                                        ChannelOperationRequestStatus.OnChainConfirmationPending,
                                        channelOperationRequest.Id,
                                        channel.Id,
                                        closePendingTxid);

                                    channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmationPending;
                                    channelOperationRequest.TxId = closePendingTxid;

                                    var onChainPendingUpdate =
                                        _channelOperationRequestRepository.Update(channelOperationRequest);

                                    if (onChainPendingUpdate.Item1 == false)
                                    {
                                        _logger.LogError(
                                            "Error while updating channel operation request id:{} to status:{}",
                                            channelOperationRequest.Id,
                                            ChannelOperationRequestStatus.OnChainConfirmationPending);
                                    }

                                    break;

                                case CloseStatusUpdate.UpdateOneofCase.ChanClose:

                                    //TODO Review why chanclose.success it is false for confirmed closings of channels
                                    var chanCloseClosingTxid = DecodeTxId(response.ChanClose.ClosingTxid);
                                    _logger.LogInformation(
                                        "Channel close request in status:{} for channel operation request:{} for channel:{} closing txId:{}",
                                        ChannelOperationRequestStatus.OnChainConfirmed,
                                        channelOperationRequest.Id,
                                        channel.Id,
                                        chanCloseClosingTxid);

                                    channelOperationRequest.Status =
                                        ChannelOperationRequestStatus.OnChainConfirmed;

                                    var onChainConfirmedUpdate =
                                        _channelOperationRequestRepository.Update(channelOperationRequest);

                                    if (onChainConfirmedUpdate.Item1 == false)
                                    {
                                        _logger.LogError(
                                            "Error while updating channel operation request id:{} to status:{}",
                                            channelOperationRequest.Id,
                                            ChannelOperationRequestStatus.OnChainConfirmed);
                                    }

                                    channel.Status = Channel.ChannelStatus.Closed;

                                    //Null to avoid creation of entities
                                    channel.ChannelOperationRequests = null;

                                    context.Update(channel);

                                    var closedChannelUpdateResult = await context.SaveChangesAsync() > 0;

                                    if (!closedChannelUpdateResult)
                                    {
                                        _logger.LogError(
                                            "Error while setting to closed status a closed channel with id:{}",
                                            channel.Id);
                                    }

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
                throw;
            }
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
                keyPathInformation = await client.nbxplorerClient.GetUnusedAsync(wallet.GetDerivationStrategy(),
                    derivationFeature, reserve: true);
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

        public async Task ChannelAcceptorJob()
        {
            _logger.LogInformation("Starting ChannelAcceptorJob... ");
            try
            {
                var managedNodes = await _nodeRepository.GetAllManagedByFundsManager();

                DeleteOldChannelAcceptorJobs();

                foreach (var managedNode in managedNodes)
                {
                    if (managedNode.ChannelAdminMacaroon != null)
                    {
                        _backgroundJobClient.Enqueue<ILightningService>(x =>
                            x.ProcessNodeChannelAcceptorJob(managedNode.Id));
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on {}", nameof(ChannelAcceptorJob));
                throw;
            }

            _logger.LogInformation("ChannelAcceptorJob ended");
        }

        public async Task ProcessNodeChannelAcceptorJob(int nodeId)
        {
            if (nodeId <= 0) throw new ArgumentOutOfRangeException(nameof(nodeId));

            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddFilter("Grpc", LogLevel.Warning);
            });

            #region Local Functions

            async Task AcceptChannelOpeningRequest(
                AsyncDuplexStreamingCall<ChannelAcceptResponse, ChannelAcceptRequest> resultAcceptor,
                Node node, ChannelAcceptRequest response)
            {
                var openerNodePubKey = Convert.ToHexString(response.NodePubkey.ToByteArray()).ToLower();
                var capacity = response.FundingAmt;
                _logger.LogInformation(
                    "Accepting channel opening request from external node:{} to managed node:{} with capacity:{} with no returning address",
                    openerNodePubKey, node.Name, capacity);
                await resultAcceptor.RequestStream.WriteAsync(new ChannelAcceptResponse
                {
                    Accept = true,
                    PendingChanId = response.PendingChanId
                });
            }

            async Task AcceptChannelOpeningRequestWithUpfrontShutdown(ExplorerClient explorerClient,
                Wallet returningMultisigWallet,
                AsyncDuplexStreamingCall<ChannelAcceptResponse, ChannelAcceptRequest> asyncDuplexStreamingCall, Node node, ChannelAcceptRequest? response)
            {
                if (response != null)
                {
                    var openerNodePubKey = Convert.ToHexString(response.NodePubkey.ToByteArray()).ToLower();
                    var capacity = response.FundingAmt;

                    var address = await explorerClient.GetUnusedAsync(returningMultisigWallet.GetDerivationStrategy(),
                        DerivationFeature.Deposit, 0, false); //Reserve is false to avoid DoS

                    if (address != null)
                    {
                        _logger.LogInformation(
                            "Accepting channel opening request from external node:{} to managed node:{} with capacity:{} with returning address:{}",
                            openerNodePubKey, node.Name, capacity, address.Address.ToString());
                        await asyncDuplexStreamingCall.RequestStream.WriteAsync(new ChannelAcceptResponse
                        {
                            Accept = true,
                            PendingChanId = response.PendingChanId,
                            UpfrontShutdown = address.Address.ToString()
                        });
                    }
                    else
                    {
                        _logger.LogError("Could not find an address for wallet:{} for a returning address",
                            returningMultisigWallet.Id);
                        //Just accept..
                        await AcceptChannelOpeningRequest(asyncDuplexStreamingCall, node, response);
                    }
                }
            }

            #endregion Local Functions

            var node = await _nodeRepository.GetById(nodeId);

            if (node == null)
            {
                _logger.LogInformation("The node:{} is no longer ready to be supported hangfire jobs", node);
                return;
            }

            try
            {
                _logger.LogInformation("Starting {} on node:{}", nameof(ProcessNodeChannelAcceptorJob), node.Name);

                using var grpcChannel = GrpcChannel.ForAddress($"https://{node.Endpoint}",
                    new GrpcChannelOptions
                    {
                        HttpHandler = new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback =
                                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                        },
                        LoggerFactory = loggerFactory,
                    });

                var client = new Lightning.LightningClient(grpcChannel);

                if (!string.IsNullOrEmpty(node.ChannelAdminMacaroon))
                {
                    var resultAcceptor = client.ChannelAcceptor(new Metadata
                    {
                        {
                            "macaroon", node.ChannelAdminMacaroon
                        }
                    });

                    await foreach (var response in resultAcceptor.ResponseStream.ReadAllAsync())
                    {
                        //If the node is null means it is no longer on the system, exit the job
                        node = await _nodeRepository.GetById(nodeId);
                        if (node == null)
                        {
                            _logger.LogInformation("The node:{} is no longer ready to be supported hangfire jobs", nodeId);
                            //Just accept..
                            await resultAcceptor.RequestStream.CompleteAsync(); // Closing the stream
                            return;
                        }

                        //We get the peers to check if they have feature flags 4 / 5 for option_upfront_shutdown_script
                        var peers = await client.ListPeersAsync(new ListPeersRequest(), new Metadata
                            {
                                {
                                    "macaroon", node.ChannelAdminMacaroon ?? throw new InvalidOperationException()
                                }
                            });
                        var openerNodePubKey = Convert.ToHexString(response.NodePubkey.ToByteArray()).ToLower();
                        var peer = peers.Peers.SingleOrDefault(x => x.PubKey == openerNodePubKey);

                        if (peer != null)
                        {
                            //Lets see if option_upfront_shutdown_script is enabled
                            if (peer.Features.ContainsKey(4) ||
                                peer.Features
                                    .ContainsKey(
                                        5)) // 4/5 from bolt9 / bolt2 https://github.com/lightning/bolts/blob/master/09-features.md
                            {
                                var (_, nbxplorerClient) = GenerateNetwork(_logger);

                                //Lets find the node's assigned multisig wallet
                                var returningMultisigWallet = node.ReturningFundsMultisigWallet;

                                if (returningMultisigWallet != null)
                                {
                                    await AcceptChannelOpeningRequestWithUpfrontShutdown(nbxplorerClient,
                                        returningMultisigWallet, resultAcceptor, node, response);
                                }
                                else
                                {
                                    //The node does not have a wallet assigned, lets pick the oldest.

                                    var wallet = (await _walletRepository.GetAvailableWallets()).FirstOrDefault();

                                    if (wallet != null)
                                    {
                                        //Wallet found
                                        await AcceptChannelOpeningRequestWithUpfrontShutdown(nbxplorerClient,
                                            wallet, resultAcceptor, node, response);

                                        node.ReturningFundsMultisigWalletId = wallet.Id;

                                        //We assign the node's returning wallet
                                        var updateResult = _nodeRepository.Update(node);

                                        if (updateResult.Item1 == false)
                                        {
                                            _logger.LogError(
                                                "Error while adding returning node wallet with id:{} to node:{}",
                                                wallet.Id, node.Id);
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogError("No wallets available in the system for {}",
                                            nameof(ChannelAcceptorJob));
                                        //Just accept..
                                        await AcceptChannelOpeningRequest(resultAcceptor, node, response);
                                    }
                                }
                            }
                        }
                        else
                        {
                            //option_upfront_shutdown_script is not enabled, just accept
                            await AcceptChannelOpeningRequest(resultAcceptor, node, response);
                        }
                    }

                    //We shouldn't have reached here, custom hack to throw exception to make the job fail
                    var statusCode = resultAcceptor.GetStatus().StatusCode;
                    if (statusCode != StatusCode.OK)
                    {
                        await resultAcceptor.RequestStream.CompleteAsync();

                        var errorMessage =
                            $"ChannelAcceptor grpc call has exited with statusCode:{statusCode} for node:{node.Name}, detail:{resultAcceptor.GetStatus().Detail}";

                        _logger.LogError(errorMessage);
                        throw new RpcException(resultAcceptor.GetStatus(), errorMessage);
                    }
                }
                else
                {
                    _logger.LogError("Invalid macaroon for node:{}", node.Name);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on {}", nameof(ProcessNodeChannelAcceptorJob));
                throw;
            }
        }

        /// <summary>
        /// Deletes the old jobs of method ChannelAcceptorJob including ProcessNodeChannelAcceptorJob from hangfire
        /// </summary>
        private void DeleteOldChannelAcceptorJobs()
        {
            var api = JobStorage.Current.GetMonitoringApi();

            var processingJobs = api.ProcessingJobs(0, int.MaxValue)
                .Where(x => x.Value.Job.ToString() == "ILightningService.ChannelAcceptorJob" ||
                            x.Value.Job.ToString() == "ILightningService.ProcessNodeChannelAcceptorJob")
                .OrderBy(x => x.Value.StartedAt);

            if (processingJobs != null && processingJobs.Any())
            {
                var currentJob = processingJobs.LastOrDefault(x => x.Value.Job.ToString() == "ILightningService.ChannelAcceptorJob");

                _logger.LogInformation("ChannelAcceptorJob and ProcessNodeChannelAcceptorJob stale jobs found, pruning old jobs.");

                //We delete all except the last one
                foreach (var processingJob in processingJobs)
                {
                    if (processingJob.Key != currentJob.Key)
                    {
                        var stateChanged =
                            _backgroundJobClient.ChangeState(processingJob.Key, new DeletedState());

                        if (stateChanged == false)
                        {
                            _logger.LogError("Error while deleting old {} job method with id:{}", nameof(ChannelAcceptorJob),
                                processingJob.Key);
                        }
                        else
                        {
                            _logger.LogInformation("Pruning old {} job method with id:{}", processingJob.Value.Job.ToString(),
                                processingJob.Key);
                        }
                    }
                }
            }
        }

        public async Task SweepNodeWalletsJob()
        {
            _logger.LogInformation("Starting {}... ", nameof(SweepNodeWalletsJob));
            try
            {
                var managedNodes = await _nodeRepository.GetAllManagedByFundsManager();

                foreach (var managedNode in managedNodes.Where(managedNode => managedNode.ChannelAdminMacaroon != null))
                {
                    _backgroundJobClient.Enqueue<ILightningService>(x =>
                        x.SweepNodeWalletJob(managedNode.Id));
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error on {}", nameof(SweepNodeWalletsJob));
                throw;
            }

            _logger.LogInformation("{} ended", nameof(SweepNodeWalletsJob));
        }

        public async Task SweepNodeWalletJob(int managedNodeId)
        {
            _logger.LogInformation("Starting {}... on node:{}", nameof(SweepNodeWalletsJob), managedNodeId);

            var requiredAnchorChannelClosingAmount = long.Parse(Environment.GetEnvironmentVariable("ANCHOR_CLOSINGS_MINIMUM_SATS")); // Check https://github.com/lightningnetwork/lnd/issues/6505#issuecomment-1120364460 to understand, we need 100K+ to support anchor channel closings

            var (_, nbxplorerClient) = GenerateNetwork(_logger);

            #region Local functions

            async Task SweepFunds(Node node, Wallet wallet, Lightning.LightningClient lightningClient,
                ExplorerClient explorerClient, List<Utxo> utxos)
            {
                if (node == null) throw new ArgumentNullException(nameof(node));
                if (wallet == null) throw new ArgumentNullException(nameof(wallet));
                if (lightningClient == null) throw new ArgumentNullException(nameof(lightningClient));
                if (explorerClient == null) throw new ArgumentNullException(nameof(explorerClient));

                var returningAddress = await explorerClient.GetUnusedAsync(wallet.GetDerivationStrategy(),
                    DerivationFeature.Deposit,
                    0,
                    false);//Reserve is false since this is a cron job and we wan't to avoid massive reserves

                var lndChangeAddress = await lightningClient.NewAddressAsync(new NewAddressRequest
                {
                    Type = AddressType.UnusedWitnessPubkeyHash
                },
                    new Metadata
                    {
                        {
                            "macaroon", node.ChannelAdminMacaroon
                        }
                    });
                var totalSatsAvailable = utxos.Sum(x => x.AmountSat);

                if (returningAddress != null && lndChangeAddress != null && utxos.Any() && totalSatsAvailable > requiredAnchorChannelClosingAmount)
                {
                    var sweepedFundsAmount = (totalSatsAvailable - requiredAnchorChannelClosingAmount); // We should let requiredAnchorChannelClosingAmount sats as a UTXO in  in the hot wallet for channel closings
                    var sendManyResponse = await lightningClient.SendManyAsync(new SendManyRequest()
                    {
                        AddrToAmount =
                        {
                            {returningAddress.Address.ToString(), sweepedFundsAmount}, //Sweeped funds
                            {lndChangeAddress.Address, requiredAnchorChannelClosingAmount},
                        },
                        MinConfs = 1,
                        Label = $"Hot wallet Sweep tx on {DateTime.UtcNow.ToString("O")} to walletId:{wallet.Id}",
                        SpendUnconfirmed = false,
                        TargetConf = 1 // 1 for now TODO Maybe this can be set as a global env var for all the Target blocks of the FM..
                    },
                        new Metadata
                        {
                            {
                                "macaroon", node.ChannelAdminMacaroon
                            }
                        });

                    _logger.LogInformation("Utxos swept out for nodeId:{} on txid:{} with returnAddress:{}",
                        node.Id,
                        sendManyResponse.Txid,
                        returningAddress.Address);
                }
                else
                {
                    var reason = returningAddress == null
                        ? "Returning address not found / null"
                        :
                        lndChangeAddress == null
                            ? "LND returning address not found / null"
                            :
                            !utxos.Any()
                                ? "No UTXOs found to fund the sweep tx"
                                :
                                totalSatsAvailable > requiredAnchorChannelClosingAmount
                                    ?
                                    "Total sats available is less than the required to have for channel closing amounts, ignoring tx" : string.Empty;


                    _logger.LogError("Error while funding sweep transaction reason:{}", reason);
                }
            }

            #endregion Local functions

            var node = await _nodeRepository.GetById(managedNodeId);
            if (node == null)
            {
                _logger.LogError("{} failed on node with id:{} reason: node not found",
                    nameof(SweepNodeWalletsJob),
                    managedNodeId);
                throw new ArgumentException("node not found", nameof(node));
            }

            var loggerFactory = LoggerFactory.Create(logging =>
            {
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddFilter("Grpc", LogLevel.Warning);
            });

            try
            {
                using var grpcChannel = GrpcChannel.ForAddress($"https://{node.Endpoint}",
                    new GrpcChannelOptions
                    {
                        HttpHandler = new HttpClientHandler
                        {
                            ServerCertificateCustomValidationCallback =
                                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                        },
                        LoggerFactory = loggerFactory,
                    });

                var client = new Lightning.LightningClient(grpcChannel);

                var unspentResponse = await client.ListUnspentAsync(new ListUnspentRequest { MinConfs = 1, MaxConfs = Int32.MaxValue }, new Metadata
                {
                    {
                        "macaroon", node.ChannelAdminMacaroon ?? throw new InvalidOperationException()
                    }
                });

                if (unspentResponse.Utxos.Any()
                    && unspentResponse.Utxos.Any(x => x.AmountSat >= 100_000) //At least 1 UTXO with 100K according to  https://github.com/lightningnetwork/lnd/issues/6505#issuecomment-1120364460
                    )
                {
                    if (node.ReturningFundsMultisigWallet == null)
                    {
                        //No returning multisig, let's assign the oldest

                        var wallet = (await _walletRepository.GetAvailableWallets()).FirstOrDefault();

                        if (wallet != null)
                        {
                            //Existing Wallet found
                            await SweepFunds(node, wallet, client, nbxplorerClient, unspentResponse.Utxos.ToList());

                            node.ReturningFundsMultisigWalletId = wallet.Id;

                            //We assign the node's returning wallet
                            var updateResult = _nodeRepository.Update(node);

                            if (updateResult.Item1 == false)
                            {
                                _logger.LogError(
                                    "Error while adding returning node wallet with id:{} to node:{}",
                                    wallet.Id, node.Name);
                            }
                        }
                        else
                        {
                            //Wallet not found
                            _logger.LogError("No wallets available in the system to perform the {} on node:{}",
                                nameof(SweepNodeWalletJob),
                                node.Name);

                            throw new ArgumentException("No wallets available in the system", nameof(wallet));
                        }
                    }
                    else
                    {
                        //Returning wallet found
                        await SweepFunds(node, node.ReturningFundsMultisigWallet, client, nbxplorerClient, unspentResponse.Utxos.ToList());
                    }
                }
            }
            catch (Exception e)
            {
                if (e.Message.Contains("insufficient input to create sweep tx"))
                {
                    //This means that the utxo is not big enough for it to be transacted, so it is a warn
                    _logger.LogWarning("Insufficient UTXOs to fund a sweep tx on node:{}", node.Name);
                }
                else if (e.Message.Contains("insufficient funds available to construct transaction"))
                {
                    _logger.LogWarning("Insufficient funds to fund a sweep tx on node:{}", node.Name);
                }
                else
                {
                    _logger.LogError(e, "Error on {}", nameof(SweepNodeWalletJob));
                    throw;
                }
            }
            _logger.LogInformation("{} ended on node:{}", nameof(SweepNodeWalletJob), node.Name);
        }
    }
}