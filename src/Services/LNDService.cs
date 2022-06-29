using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
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
using Channel = FundsManager.Data.Models.Channel;

namespace FundsManager.Services
{
    public interface ILndService
    {
        /// <summary>
        /// Opens a channel with a completely signed PSBT from a node to another given node
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="source"></param>
        /// <param name="destination"></param>
        /// <returns></returns>
        public Task<bool> OpenChannel(ChannelOperationRequest channelOperationRequest, Node source, Node destination);

        /// <summary>
        /// Generates a template PSBT with Sighash_NONE and some UTXOs from the wallet related to the request without signing
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <returns></returns>
        public Task<PSBT?> GenerateTemplatePSBT(ChannelOperationRequest channelOperationRequest);

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
        private readonly Lightning.LightningClient _lightningClient;
        private readonly ILogger<LndService> _logger;
        private readonly IChannelRepository _channelRepository;
        private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;

        public LndService(Lightning.LightningClient lightningClient, ILogger<LndService> logger, IChannelRepository channelRepository,
            IChannelOperationRequestRepository channelOperationRequestRepository)
        {
            _lightningClient = lightningClient;
            _logger = logger;
            _channelRepository = channelRepository;
            _channelOperationRequestRepository = channelOperationRequestRepository;
        }

        /// <summary>
        /// Opens a channel with a completely signed PSBT from a node to another given node
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="source"></param>
        /// <param name="destination"></param>
        /// <returns></returns>
        public async Task<bool> OpenChannel(ChannelOperationRequest channelOperationRequest, Node source, Node destination)
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

            var httpHandler = new HttpClientHandler();
            // Return `true` to allow certificates that are untrusted/invalid
            httpHandler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var grpcChannel = GrpcChannel.ForAddress($"https://{source.Endpoint}",
                new GrpcChannelOptions { HttpHandler = httpHandler });

            var client = new Lightning.LightningClient(grpcChannel);

            var result = true;

            //TODO Log user approver 

            _logger.LogInformation("Channel open request for  request id:{} from node:{} to node:{}",
                channelOperationRequest.Id,
                source.Name,
                destination.Name);
            try
            {
                var openChannelRequest = new OpenChannelRequest
                {
                    //TODO Shim details for the PSBT
                    FundingShim = new FundingShim
                    {
                        PsbtShim = new PsbtShim
                        { BasePsbt = ByteString.Empty, NoPublish = false, PendingChanId = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) }
                    },
                    LocalFundingAmount = channelOperationRequest.SatsAmount,
                    //TODO Close address
                    //CloseAddress = "bc1...003"
                    Private = channelOperationRequest.IsChannelPrivate,
                    NodePubkey = ByteString.CopyFrom(Convert.FromHexString(destination.PubKey)),


                };

                //If PSBT mode is not enabled.. go the sync way
                var channelPoint
                    = new ChannelPoint();
                if (openChannelRequest.FundingShim == null)
                {

                    channelPoint = await client.OpenChannelSyncAsync(openChannelRequest,
                        new Metadata { { "macaroon", source.ChannelAdminMacaroon } }
                    );

                    _logger.LogInformation("Opened channel on channel point:{}:{} request id:{} from node:{} to node:{}",
                        channelPoint.FundingTxidStr,
                        channelPoint.OutputIndex,
                        channelOperationRequest.Id,
                        source.Name,
                        destination.Name);

                    //Channel creation

                    //FundingTxidbytes to hex string

                    var fundingTxid = Convert.ToHexString(channelPoint.FundingTxidBytes.ToByteArray());

                    var channel = new Channel
                    {
                        Capacity = channelOperationRequest.SatsAmount,
                        //TODO Channel id retrieval it is not on the result from the open channel
                        FundingTx = fundingTxid, //TODO Validate this data (?)
                        FundingTxOutputIndex = channelPoint.OutputIndex,
                        CreationDatetime = DateTimeOffset.Now,
                        UpdateDatetime = DateTimeOffset.Now,
                        ChannelOperationRequests = new List<ChannelOperationRequest> { channelOperationRequest }
                        //TODO Set btc close address
                    };
                    var channelCreationResult = await _channelRepository.AddAsync(channel);

                    if (!channelCreationResult.Item1)
                    {
                        _logger.LogError("Error while saving channel entity for channel operation request with id:{} error:{}",
                            channelOperationRequest.Id, channelCreationResult.Item2);

                        if (channel.Id > 0)
                        {
                            channelOperationRequest.ChannelId = channel.Id;

                            _channelOperationRequestRepository.Update(channelOperationRequest);
                        }
                    }
                }
                else // Go to the async way with OpenStatusUpdate (PSBT)
                {
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
                                break;
                            case OpenStatusUpdate.UpdateOneofCase.ChanOpen:
                                break;
                            case OpenStatusUpdate.UpdateOneofCase.PsbtFund:

                                //We got the funding address

                                //Generation of the Base PSBT with the output

                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }

                        if (response?.PsbtFund?.FundingAddress != null)
                        {

                        }
                    }

                    //TODO Abandoning channels pending (?)
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
            }

            return result;

        }

        public async Task<PSBT?> GenerateTemplatePSBT(ChannelOperationRequest channelOperationRequest)
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

            var network = Environment.GetEnvironmentVariable("BITCOIN_NETWORK");
            var nbxplorerUri = Environment.GetEnvironmentVariable("NBXPLORER_URI") ?? throw new ArgumentNullException("Environment.GetEnvironmentVariable(\"NBXPLORER_URI\")");

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
            var nbxplorerClient = new ExplorerClient(provider.GetFromCryptoCode(nbXplorerNetwork.NetworkSet.CryptoCode), new Uri(nbxplorerUri));

            if (!(await nbxplorerClient.GetStatusAsync()).IsFullySynched)
            {
                _logger.LogError("Error, nbxplorer not fully synched");
                return null;
            }

            //UTXOs -> they need to be tracked first on nbxplorer to get results!!
            //TODO Create track method for nbxplorer

            var walletXPUBs = channelOperationRequest?.Wallet?.Keys.Select(x => x.XPUB).ToList();

            if (walletXPUBs != null && !walletXPUBs.Any())
            {
                _logger.LogError("No XPUBs for the wallet found");
                return null;
            }

            var xpubKeysList = walletXPUBs?.Select(x => new ExtPubKey(x)).ToList();
            var factory = new DerivationStrategyFactory(nbXplorerNetwork);
            var directDerivationStrategy = factory.CreateMultiSigDerivationStrategy(xpubKeysList.ToArray(),
                channelOperationRequest.Wallet.MofN);

            var derivationSchemeTrackedSource = new DerivationSchemeTrackedSource(directDerivationStrategy);
            var utxoChanges = await nbxplorerClient.GetUTXOsAsync(derivationSchemeTrackedSource);


            if (utxoChanges == null || !utxoChanges.Confirmed.UTXOs.Any())
            {
                _logger.LogError("PSBT cannot be generated, no UTXOs are available for walletId:{}",channelOperationRequest.WalletId);
                return null;
            }

            var utxosStack = new Stack<UTXO>(utxoChanges.Confirmed.UTXOs.OrderByDescending(x=> x.Confirmations));

            //FIFO Algorithm to match the amount

            var totalUTXOsConfirmedSats = utxosStack.Sum(x => ((Money) x.Value).Satoshi);

            if (totalUTXOsConfirmedSats < channelOperationRequest.SatsAmount)
            {
                _logger.LogError(
                    "Error, the total UTXOs set balance for walletid:{} ({} sats) is less than the channel amount request ({}sats)",
                    channelOperationRequest.WalletId,totalUTXOsConfirmedSats, channelOperationRequest.SatsAmount );
                return null;
            }

            var utxosSatsAmountAccumulator = 0M;

            var selectedUTXOs = new List<UTXO>();

            while (channelOperationRequest.SatsAmount >= utxosSatsAmountAccumulator)
            {
                if (utxosStack.TryPop(out var utxo))
                {
                    selectedUTXOs.Add(utxo);
                    utxosSatsAmountAccumulator += ((Money) utxo.Value).Satoshi;
                }

            }

            //UTXOS to Enumerable of ICOINS

            var coins = selectedUTXOs.Select(x => new Coin(x.Outpoint, new TxOut((Money) x.Value, x.ScriptPubKey))).ToList();

            //We got enough inputs to fund the TX so time to build the PSBT without outputs (funding address of the channel)

            PSBT? result = null;

            var txBuilder = nbXplorerNetwork.CreateTransactionBuilder();
            var feeRateResult = await nbxplorerClient.GetFeeRateAsync(1);

            result = txBuilder.AddCoins(coins)
                .SetSigningOptions(SigHash.None)
                .SetChange(coins.First().ScriptPubKey) //Same as firt UTXOs
                .SendEstimatedFees(feeRateResult.FeeRate)
                .BuildPSBT(false);

            //TODO Remove hack when https://github.com/MetacoSA/NBitcoin/issues/1112 is fixed
            result.Settings.SigningOptions = new SigningOptions(SigHash.None);
            
            return result;
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
                        //Time to close the channel
                        var closeChannelResult = _lightningClient.CloseChannel(new CloseChannelRequest
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

                        //TODO The closeChannelResult is a streaming with the status updates, this is an async long operation, maybe we should track this process elsewhere (?)


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
