using FundsManager.Data;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using System.Security.Cryptography;
using Channel = FundsManager.Data.Models.Channel;
using Key = FundsManager.Data.Models.Key;

namespace FundsManager.Services
{
    public interface ILndService
    {
        /// <summary>
        /// Opens a channel based on a presigned psbt with inputs
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="source"></param>
        /// <param name="destination"></param>
        /// <param name="presignedPSBT"></param>
        /// <returns></returns>
        public Task<bool> OpenChannel(ChannelOperationRequest channelOperationRequest, Node source, Node destination, PSBT presignedPSBT);

        /// <summary>
        /// Generates a template PSBT with Sighash_NONE and some UTXOs from the wallet related to the request without signing
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="destinationAddress"></param>
        /// <returns></returns>
        public Task<PSBT?> GenerateTemplatePSBT(ChannelOperationRequest channelOperationRequest, BitcoinAddress? destinationAddress = null);

        /// <summary>
        /// Based on a channel operation request of type close, requests the close of a channel to LND without acking the request
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="forceClose"></param>
        /// <returns></returns>
        public Task<bool> CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false);
    }

    /// <summary>
    /// Service in charge of communicating with LND over gRPC
    /// </summary>
    public class LndService : ILndService
    {
        private readonly ILogger<LndService> _logger;
        private readonly IChannelRepository _channelRepository;
        private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        public LndService(ILogger<LndService> logger, IChannelRepository channelRepository,
            IChannelOperationRequestRepository channelOperationRequestRepository, IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _logger = logger;
            _channelRepository = channelRepository;
            _channelOperationRequestRepository = channelOperationRequestRepository;
            _dbContextFactory = dbContextFactory;
        }

        /// <summary>
        /// Opens a channel with a completely signed PSBT from a node to another given node
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="source"></param>
        /// <param name="destination"></param>
        /// <param name="presignedPSBT"></param>
        /// <returns></returns>
        public async Task<bool> OpenChannel(ChannelOperationRequest channelOperationRequest, Node source,
            Node destination, PSBT presignedPSBT)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            if (channelOperationRequest.RequestType != OperationRequestType.Open)
                return false;

            if (source.Id == destination.Id)
            {
                return false;
            }

            //Setup of grpc lnd api client (Lightning.proto)
            //Hack to allow self-signed https grpc calls
            var httpHandler = new HttpClientHandler();
            httpHandler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var grpcChannel = GrpcChannel.ForAddress($"https://{source.Endpoint}",
                new GrpcChannelOptions { HttpHandler = httpHandler });

            var client = new Lightning.LightningClient(grpcChannel);

            var result = false;

            //32 bytes of secure randomness for the pending channel id (lnd)

            var pendingChannelId = RandomNumberGenerator.GetBytes(32);

            var pendingChannelIdHex = Convert.ToHexString(pendingChannelId);

            var (network, nbxplorerClient) = GenerateNetwork();
            var txBuilder = network.CreateTransactionBuilder();

            //Derivation strategy for the multisig address based on its wallet
            var (derivationStrategyBase, internalWalletDerivationStrategy) = GetDerivationStrategy(channelOperationRequest, network);

            var closeAddress = await nbxplorerClient.GetUnusedAsync(derivationStrategyBase, DerivationFeature.Deposit, 0, true);

            //TODO Log user approver

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
                                FundingTx = response.ChanOpen.ChannelPoint.FundingTxidStr,
                                FundingTxOutputIndex = response.ChanOpen.ChannelPoint.OutputIndex,
                                BtcCloseAddress = closeAddress?.Address.ToString(),
                                SatsAmount = channelOperationRequest.SatsAmount,
                                UpdateDatetime = DateTimeOffset.Now,
                                ChannelOperationRequests = new List<ChannelOperationRequest> { channelOperationRequest }
                            };

                            var addChannelResult = await _channelRepository.AddAsync(channel);

                            if (addChannelResult.Item1 != false)
                            {
                                _logger.LogError("Channel for channel operation request id:{} could not be created, reason:{}", channelOperationRequest.Id, addChannelResult.Item2);
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
                                    totalIn = totalIn + (input.GetTxOut().Value);
                                }
                                var totalOut = new Money(channelOperationRequest.SatsAmount, MoneyUnit.Satoshi);
                                var totalFees = presignedPSBT.GetFee();
                                channelfundingTx.Outputs[0].Value = totalIn - totalOut - totalFees;

                                //We need to SIGHASH_ALL | AnyOneCanpay as fundsmanager to protect the output from tampering by adding a UTXO by us and signing with SIGHASH_ALL

                                var unusedInternalWalletAddress =
                                    await nbxplorerClient.GetUnusedAsync(
                                        internalWalletDerivationStrategy, DerivationFeature.Deposit);

                                var fundManagerUTXOS = await nbxplorerClient.GetUTXOsAsync(internalWalletDerivationStrategy);
                                //We limit to one 1 UTXO only.. important remark
                                var fundManagerUTXO = fundManagerUTXOS.Confirmed.UTXOs.FirstOrDefault();
                                if (fundManagerUTXO == null || fundManagerUTXOS.Confirmed.UTXOs.Sum(x => (Money)x.Value) <= 0)
                                {
                                    _logger.LogError("Internal wallet for the fundmanager has no balance it its UTXO set");
                                    return false;
                                };

                                //var derivedBitcoinExtKey = channelOperationRequest.Wallet.InternalWallet
                                //    .GetAccountKey(network).Derive(fundManagerUTXO.KeyPath);

                                //We get the UTXO keyPath / derivation path from nbxplorer

                                var UTXOs = await nbxplorerClient.GetUTXOsAsync(derivationStrategyBase);

                                var OutpointKeyPathDictionary = UTXOs.Confirmed.UTXOs.ToDictionary(x => x.Outpoint, x => x.KeyPath);

                                var txInKeyPathDictionary =
                                    channelfundingTx.Inputs.Where(x => OutpointKeyPathDictionary.ContainsKey(x.PrevOut)).ToDictionary(x => x, x => OutpointKeyPathDictionary[x.PrevOut]);

                                if (!txInKeyPathDictionary.Any())
                                {
                                    _logger.LogError("Error, keypaths for the UTXOs used in this tx are not found");
                                    return false;
                                }

                                var privateKeysForUsedUTXOs = txInKeyPathDictionary.Select(x =>
                                    channelOperationRequest.Wallet.InternalWallet.GetAccountKey(network)
                                        .Derive(x.Value).PrivateKey).ToList();

                                //var fundManagerTX = txBuilder
                                //    .AddKeys(derivedBitcoinExtKey.PrivateKey)
                                //    .AddCoin(fundManagerUTXO.AsCoin(internalWalletDerivationStrategy))
                                //    .Send(fundManagerUTXO.ScriptPubKey, fundManagerUTXO.Value)
                                //    .SetSigningOptions(SigHash.None)
                                //    .SendEstimatedFees(FeeRate.Zero)
                                //    .BuildPSBT(true);

                                ////TODO Remove-> We merge, we shall have 1 input fron fundsmanager internal wallet and N from the multisig treasury wallet and three outputs, 1 for the channel funding, another for the change and the one goign back to the fundsManager internal wallet (this is a way to SIGHASH_ALL)

                                //channelfundingTx.Inputs.AddRange(fundManagerTX.Inputs);
                                //channelfundingTx.Outputs.AddRange(fundManagerTX.Outputs);

                                //We merge fundedPSBT with the ones with the change fixed

                                var changeFixedPSBT = channelfundingTx.CreatePSBT(network).UpdateFrom(fundedPSBT);
                                changeFixedPSBT.Inputs.FirstOrDefault().Sign(privateKeysForUsedUTXOs.First());

                                //Sanity check
                                changeFixedPSBT.AssertSanity();

                                channelfundingTx = changeFixedPSBT.GetGlobalTransaction();

                                //Just a check of the tx based on the changeFixedPSBT
                                var checkTx = channelfundingTx.Check();

                                if (checkTx == TransactionCheckResult.Success)
                                {
                                    //We tell lnd to verify the psbt
                                    var verifyPSBT = client.FundingStateStep(
                                        new FundingTransitionMsg
                                        {
                                            PsbtVerify = new FundingPsbtVerify
                                            {
                                                FundedPsbt = ByteString.CopyFrom(Convert.FromHexString(changeFixedPSBT.ToHex())),
                                                PendingChanId = ByteString.CopyFrom(pendingChannelId)
                                            }
                                        }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } });

                                    //PSBT marked as verified so time to finalize the PSBT and broadcast the tx

                                    var fundingStateStepResp = await client.FundingStateStepAsync(new FundingTransitionMsg
                                    {
                                        PsbtFinalize = new FundingPsbtFinalize
                                        {
                                            PendingChanId = ByteString.CopyFrom(pendingChannelId),
                                            //FinalRawTx = ByteString.CopyFrom(Convert.FromHexString(finalTxHex)),
                                            SignedPsbt = ByteString.CopyFrom(Convert.FromHexString(changeFixedPSBT.Finalize().ToHex()))
                                        },
                                    }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } });
                                }
                                else
                                {
                                    CancelPendingChannel(source, client, pendingChannelId);
                                }
                            }

                            break;

                        default:
                            throw new ArgumentOutOfRangeException();
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
            }

            return result;
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

        public async Task<PSBT?> GenerateTemplatePSBT(ChannelOperationRequest channelOperationRequest, BitcoinAddress? destinationAddress = null)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

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

            var (nbXplorerNetwork, nbxplorerClient) = GenerateNetwork();

            if (!(await nbxplorerClient.GetStatusAsync()).IsFullySynched)
            {
                _logger.LogError("Error, nbxplorer not fully synched");
                return null;
            }

            //UTXOs -> they need to be tracked first on nbxplorer to get results!!
            //TODO Create track method for nbxplorer

            var (derivationStrategy, internalWalletDerivationStrategy) = GetDerivationStrategy(channelOperationRequest, nbXplorerNetwork);

            var multisigCoins = await GetTxInputCoins(channelOperationRequest, nbxplorerClient, derivationStrategy);

            var utxoChanges = (await nbxplorerClient.GetUTXOsAsync(internalWalletDerivationStrategy));
            var internalWalletCoins = utxoChanges.Confirmed.UTXOs.Select(x => x.AsCoin(internalWalletDerivationStrategy)
                    )
                .ToList();

            var firstInternalWalletUTXO = internalWalletCoins.FirstOrDefault();
            if (firstInternalWalletUTXO == null)
            {
                _logger.LogError("Internal wallet has not UTXOs");
                return null;
            }

            var firtInernalWalletUTXOMoney = (Money)firstInternalWalletUTXO.Amount;
            var internalWalletReturnScriptPubKey = firstInternalWalletUTXO.ScriptPubKey;

            //We got enough inputs to fund the TX so time to build the PSBT with a output to the InternalWallet to return the funds,
            //the funding address of the channel will be added later by LND

            PSBT? result = null;

            var txBuilder = nbXplorerNetwork.CreateTransactionBuilder();

            var feeRateResult = await GetFeeRateResult(nbXplorerNetwork, nbxplorerClient);

            var changeAddress = nbxplorerClient.GetUnused(derivationStrategy, DerivationFeature.Change);

            if (destinationAddress != null)
            {
                var builder = txBuilder;
                builder.AddCoins(multisigCoins);
                //builder.AddCoin(firstInternalWalletUTXO);

                builder.Send(destinationAddress, new Money(channelOperationRequest.SatsAmount, MoneyUnit.Satoshi))
                                .Send(internalWalletReturnScriptPubKey, firtInernalWalletUTXOMoney)
                                .SetSigningOptions(SigHash.None)
                                .SendAllRemainingToChange()
                                .SetChange(changeAddress.Address)
                                .SendEstimatedFees(feeRateResult.FeeRate);

                result = builder.BuildPSBT(false);
            }
            else
            {
                var feesCoins = new Money(0.0000001M, MoneyUnit.BTC);
                var builder = txBuilder;
                builder.AddCoins(multisigCoins);
                //builder.AddCoin(firstInternalWalletUTXO);

                builder.SetSigningOptions(SigHash.None)
                    .SendAllRemainingToChange()
                    .SetChange(changeAddress.Address)
                    .SendEstimatedFees(feeRateResult.FeeRate);

                result = builder.BuildPSBT(false);
            }

            //TODO Remove hack when https://github.com/MetacoSA/NBitcoin/issues/1112 is fixed
            result.Settings.SigningOptions = new SigningOptions(SigHash.None);

            return result;
        }

        private static async Task<GetFeeRateResult> GetFeeRateResult(Network nbXplorerNetwork, ExplorerClient nbxplorerClient)
        {
            var feeRateResult = new GetFeeRateResult();
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

        private (DerivationStrategyBase?, DerivationStrategyBase?) GetDerivationStrategy(ChannelOperationRequest channelOperationRequest,
            Network nbXplorerNetwork)
        {
            var bitcoinExtPubKeys = channelOperationRequest?.Wallet?.Keys.Select(x => new BitcoinExtPubKey(x.XPUB,
                    nbXplorerNetwork))
                .ToList();

            if (bitcoinExtPubKeys == null || !bitcoinExtPubKeys.Any())
            {
                _logger.LogError("No XPUBs for the wallet found");
                return (null, null);
            }

            var factory = new DerivationStrategyFactory(nbXplorerNetwork);

            var multiSigDerivationStrategy
                = factory.CreateMultiSigDerivationStrategy(bitcoinExtPubKeys.ToArray(),
                channelOperationRequest.Wallet.MofN,
                new DerivationStrategyOptions
                {
                    ScriptPubKeyType = ScriptPubKeyType.Segwit
                });

            var internalWalletXPUB = new BitcoinExtPubKey(channelOperationRequest.Wallet.InternalWallet.GetXPUB(nbXplorerNetwork), nbXplorerNetwork);
            var internalWalletDerivationStrategy = new DirectDerivationStrategy(internalWalletXPUB, true);

            return (multiSigDerivationStrategy, internalWalletDerivationStrategy);
        }

        /// <summary>
        /// Generates the ExplorerClient for using nbxplorer based on a bitcoin networy type
        /// </summary>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        private (Network nbXplorerNetwork, ExplorerClient nbxplorerClient) GenerateNetwork()
        {
            var network = Environment.GetEnvironmentVariable("BITCOIN_NETWORK");
            var nbxplorerUri = Environment.GetEnvironmentVariable("NBXPLORER_URI") ??
                               throw new ArgumentNullException("Environment.GetEnvironmentVariable(\"NBXPLORER_URI\")");

            if (network == null)
            {
                _logger.LogError("Bitcoin network not set");
            }

            //Nbxplorer api client

            var nbXplorerNetwork = network switch
            {
                "REGTEST" => Network.RegTest,
                "MAINNET" => Network.Main,
                "TESTNET" => Network.TestNet,
                _ => Network.RegTest
            };

            var provider = new NBXplorerNetworkProvider(nbXplorerNetwork.ChainName);
            var nbxplorerClient = new ExplorerClient(provider.GetFromCryptoCode(nbXplorerNetwork.NetworkSet.CryptoCode),
                new Uri(nbxplorerUri));
            return (nbXplorerNetwork, nbxplorerClient);
        }

        private async Task<List<ScriptCoin>> GetTxInputCoins(ChannelOperationRequest channelOperationRequest, ExplorerClient nbxplorerClient,
            DerivationStrategyBase derivationStrategy)
        {
            var utxoChanges = await nbxplorerClient.GetUTXOsAsync(derivationStrategy);

            if (utxoChanges == null || !utxoChanges.Confirmed.UTXOs.Any())
            {
                _logger.LogError("PSBT cannot be generated, no UTXOs are available for walletId:{}",
                    channelOperationRequest.WalletId);
                return new List<ScriptCoin>();
            }

            var utxosStack = new Stack<UTXO>(utxoChanges.Confirmed.UTXOs.OrderByDescending(x => x.Confirmations));

            //FIFO Algorithm to match the amount

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

        /// <summary>
        /// Based on a channel operation request of type close, requests the close of a channel to LND without acking the request
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="forceClose"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public async Task<bool> CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            if (channelOperationRequest.RequestType != OperationRequestType.Close)
                return false;

            var result = true;

            _logger.LogInformation("Channel close request for request id:{}",
                channelOperationRequest.Id);

            try
            {
                if (channelOperationRequest.ChannelId != null)
                {
                    var channel = await _channelRepository.GetById(
                        channelOperationRequest.ChannelId.Value);

                    if (channel != null)
                    {
                        //TODO
                        ////Time to close the channel
                        //var closeChannelResult = _lightningClient.CloseChannel(new CloseChannelRequest
                        //{
                        //    ChannelPoint = new ChannelPoint
                        //    {
                        //        FundingTxidStr = channel.FundingTx,
                        //        OutputIndex = channel.FundingTxOutputIndex
                        //    },
                        //    Force = forceClose,

                        //}, new Metadata { { "macaroon", channelOperationRequest.SourceNode.ChannelAdminMacaroon } });

                        //_logger.LogInformation("Channel close request:{} triggered",
                        //    channelOperationRequest.Id);

                        ////TODO The closeChannelResult is a streaming with the status updates, this is an async long operation, maybe we should track this process elsewhere (?)
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
    }
}