using System.Net;
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
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using AutoMapper;
using FundsManager.Data;
using FundsManager.Helpers;
using Hangfire;
using Hangfire.States;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using AddressType = Lnrpc.AddressType;
using Channel = FundsManager.Data.Models.Channel;
using ListUnspentRequest = Lnrpc.ListUnspentRequest;
using Transaction = NBitcoin.Transaction;
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
        /// Opens a channel based on a request this method waits for I/O on the blockchain, therefore it can last its execution for minutes
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <returns></returns>
        // ReSharper disable once IdentifierTypo
        public Task OpenChannel(ChannelOperationRequest channelOperationRequest);

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
        private readonly IFMUTXORepository _ifmutxoRepository;
        private readonly IChannelOperationRequestPSBTRepository _channelOperationRequestPsbtRepository;
        private readonly IChannelRepository _channelRepository;

        public LightningService(ILogger<LightningService> logger,
            IChannelOperationRequestRepository channelOperationRequestRepository,
            INodeRepository nodeRepository,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IMapper mapper,
            IBackgroundJobClient backgroundJobClient,
            IWalletRepository walletRepository,
            IFMUTXORepository ifmutxoRepository,
            IChannelOperationRequestPSBTRepository channelOperationRequestPsbtRepository,
            IChannelRepository channelRepository)
        {
            _logger = logger;
            _channelOperationRequestRepository = channelOperationRequestRepository;
            _nodeRepository = nodeRepository;
            _dbContextFactory = dbContextFactory;
            _mapper = mapper;
            _backgroundJobClient = backgroundJobClient;
            _walletRepository = walletRepository;
            _ifmutxoRepository = ifmutxoRepository;
            _channelOperationRequestPsbtRepository = channelOperationRequestPsbtRepository;
            _channelRepository = channelRepository;
        }

        /// <summary>
        /// Record used to match AWS SignPSBT function input
        /// </summary>
        /// <param name="Psbt"></param>
        /// <param name="EnforcedSighash"></param>
        /// <param name="Network"></param>
        /// <param name="AwsKmsKeyId"></param>
        public record Input(string Psbt, SigHash? EnforcedSighash, string Network, string AwsKmsKeyId);
        /// <summary>
        /// Record used to match AWS SignPSBT funciton output
        /// </summary>
        /// <param name="Psbt"></param>
        public record Output(string? Psbt);

        public async Task OpenChannel(ChannelOperationRequest channelOperationRequest)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            if (channelOperationRequest.RequestType != OperationRequestType.Open)
            {
                const string aNodeCannotOpenAChannelToHimself = "A node cannot open a channel to himself.";
                _logger.LogError(aNodeCannotOpenAChannelToHimself);
                throw new ArgumentException(aNodeCannotOpenAChannelToHimself);
            }

            //Update
            channelOperationRequest = await _channelOperationRequestRepository.GetById(channelOperationRequest.Id) ?? throw new InvalidOperationException();

            await using var context = await _dbContextFactory.CreateDbContextAsync();

            var source = channelOperationRequest.SourceNode;
            var destination = channelOperationRequest.DestNode;

            if (source == null || destination == null)
            {
                throw new ArgumentException("Source or destination null", nameof(source));
            }

            if (source.PubKey == destination.PubKey)
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

            var (network, nbxplorerClient) = LightningHelper.GenerateNetwork();

            //Derivation strategy for the multisig address based on its wallet
            var derivationStrategyBase = channelOperationRequest.Wallet.GetDerivationStrategy();

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
                destination.Name);

            //PSBT Combine
            var signedPsbts = channelOperationRequest.ChannelOperationRequestPsbts.Where(x =>
                    !x.IsFinalisedPSBT && !x.IsInternalWalletPSBT && !x.IsTemplatePSBT)
                .Select(x => x.PSBT);

            var combinedPSBT = LightningHelper.CombinePSBTs(signedPsbts, _logger);

            if (combinedPSBT == null)
            {
                var invalidPsbtNullToBeUsedForTheRequest = $"Invalid PSBT(null) to be used for the channel op request:{channelOperationRequest.Id}";
                _logger.LogError(invalidPsbtNullToBeUsedForTheRequest);

                throw new ArgumentException(invalidPsbtNullToBeUsedForTheRequest, nameof(combinedPSBT));
            }

            try
            {
                //We prepare the request (shim) with the base PSBT we had presigned with the UTXOs to fund the channel
                var openChannelRequest = new OpenChannelRequest
                {
                    FundingShim = new FundingShim
                    {
                        PsbtShim = new PsbtShim
                        {
                            BasePsbt = ByteString.FromBase64(combinedPSBT.ToBase64()),
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
                if (source.ChannelAdminMacaroon != null)
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
                                //Channel funding tx on mempool and pending status on lnd

                                _logger.LogInformation(
                                    "Channel pending for channel operation request id:{} for pending channel id:{}",
                                    channelOperationRequest.Id, pendingChannelIdHex);

                                channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmationPending;
                                channelOperationRequest.TxId = LightningHelper.DecodeTxId(response.ChanPending.Txid);
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
                                    FundingTx = LightningHelper.DecodeTxId(response.ChanOpen.ChannelPoint.FundingTxidBytes),
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
                                    var totalFees = combinedPSBT.GetFee();
                                    channelfundingTx.Outputs[0].Value = totalIn - totalOut - totalFees;

                                    //We merge changeFixedPSBT with the other PSBT with the change fixed

                                    var changeFixedPSBT = channelfundingTx.CreatePSBT(network).UpdateFrom(fundedPSBT);
                                    var partialSigsCount = changeFixedPSBT.Inputs.Sum(x => x.PartialSigs.Count);
                                    //We check the way the fundsmanager signs, with the remoteFundsManagerSigner or by itself.
                                    var isFMSignerEnabled =
                                        Environment.GetEnvironmentVariable("ENABLE_REMOTE_FM_SIGNER").ToUpper() == "true".ToUpper();

                                    PSBT? fmSignedPSBT = null;
                                    if (isFMSignerEnabled)
                                    {
                                        var region = Environment.GetEnvironmentVariable("AWS_REGION");
                                        //AWS Call to lambda function
                                        var credentials = new ImmutableCredentials(
                                            Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
                                            Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
                                            null);

                                        var requestPayload = new Input(changeFixedPSBT.ToBase64(), SigHash.All,
                                            CurrentNetworkHelper.GetCurrentNetwork().ToString(),
                                            Environment.GetEnvironmentVariable("AWS_KMS_KEY_ID") ??
                                            throw new InvalidOperationException());

                                        var serializedPayload = JsonSerializer.Serialize(requestPayload);
                                        using var httpClient = new HttpClient();
                                        //We use a special lib for IAM Auth to AWS
                                        var signLambdaResponse = await httpClient.PostAsync(
                                            Environment.GetEnvironmentVariable("FM_SIGNER_ENDPOINT"),
                                            new StringContent(serializedPayload,
                                                Encoding.UTF8,
                                                "application/json"),
                                            regionName: region,
                                            serviceName: "lambda",
                                            credentials: credentials);

                                        if (signLambdaResponse.StatusCode != HttpStatusCode.OK)
                                        {
                                            var errorWhileSignignPsbtWithAwsLambdaFunctionStatus =
                                                $"Error while signing PSBT with AWS Lambda function,status code:{signLambdaResponse.StatusCode} error:{signLambdaResponse.ReasonPhrase}";
                                            _logger.LogError(errorWhileSignignPsbtWithAwsLambdaFunctionStatus);
                                            throw new Exception(errorWhileSignignPsbtWithAwsLambdaFunctionStatus);
                                        }

                                        var json = JsonSerializer.Deserialize<Output>(signLambdaResponse.Content.ReadAsStream());

                                        if (!PSBT.TryParse(json.Psbt, CurrentNetworkHelper.GetCurrentNetwork(),
                                                out fmSignedPSBT))
                                        {
                                            var errorWhileParsingPsbt = "Error while parsing PSBT signed from AWS Remote FundsManagerSigner";
                                            _logger.LogError(errorWhileParsingPsbt);
                                            throw new Exception(errorWhileParsingPsbt);
                                        }
                                    }
                                    else
                                    {
                                        fmSignedPSBT = await SignPSBT(channelOperationRequest,
                                            nbxplorerClient,
                                            derivationStrategyBase,
                                            channelfundingTx,
                                            source,
                                            client,
                                            pendingChannelId,
                                            network,
                                            changeFixedPSBT);
                                    }

                                    //We check that the partial signatures number has changed, otherwise finalize inmediately
                                    var partialSigsCountAfterSignature =
                                        fmSignedPSBT.Inputs.Sum(x => x.PartialSigs.Count);

                                    if (partialSigsCountAfterSignature == 0 ||
                                        partialSigsCountAfterSignature <= partialSigsCount)
                                    {
                                        var invalidNoOfPartialSignatures =
                                            $"Invalid expected number of partial signatures after signing the PSBT";

                                        _logger.LogError(invalidNoOfPartialSignatures);
                                        throw new ArgumentException(
                                            invalidNoOfPartialSignatures);
                                    }

                                    //PSBT marked as verified so time to finalize the PSBT and broadcast the tx

                                    var finalizedPSBT = fmSignedPSBT.Finalize();

                                    //Sanity check
                                    finalizedPSBT.AssertSanity();

                                    channelfundingTx = finalizedPSBT.ExtractTransaction();

                                    //Just a check of the tx based on the finalizedPSBT
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
                                                        ByteString.CopyFrom(Convert.FromHexString(finalizedPSBT.ToHex())),
                                                    PendingChanId = ByteString.CopyFrom(pendingChannelId)
                                                }
                                            }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } });

                                        //Saving the PSBT in the ChannelOperationRequest collection of PSBTs

                                        channelOperationRequest =
                                            await _channelOperationRequestRepository.GetById(channelOperationRequest.Id) ?? throw new InvalidOperationException();

                                        if (channelOperationRequest.ChannelOperationRequestPsbts != null)
                                        {
                                            var finalizedChannelOperationRequestPsbt = new ChannelOperationRequestPSBT
                                            {
                                                IsFinalisedPSBT = true,
                                                CreationDatetime = DateTimeOffset.Now,
                                                PSBT = finalizedPSBT.ToBase64(),
                                                ChannelOperationRequestId = channelOperationRequest.Id
                                            };

                                            var finalisedPSBTAdd = await
                                                _channelOperationRequestPsbtRepository.AddAsync(finalizedChannelOperationRequestPsbt);

                                            if (!finalisedPSBTAdd.Item1)
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

        /// <summary>
        /// Aux method when the fundsmanager is the one in change of signing the PSBTs
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="nbxplorerClient"></param>
        /// <param name="derivationStrategyBase"></param>
        /// <param name="channelfundingTx"></param>
        /// <param name="source"></param>
        /// <param name="client"></param>
        /// <param name="pendingChannelId"></param>
        /// <param name="network"></param>
        /// <param name="changeFixedPSBT"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        private async Task<PSBT> SignPSBT(ChannelOperationRequest channelOperationRequest, ExplorerClient nbxplorerClient,
            DerivationStrategyBase derivationStrategyBase, Transaction channelfundingTx, Node source, Lightning.LightningClient client,
            byte[] pendingChannelId, Network network, PSBT changeFixedPSBT)
        {
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

            return changeFixedPSBT;
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

                    if (source.ChannelAdminMacaroon != null)
                    {
                        var cancelResult = client.FundingStateStep(new FundingTransitionMsg
                        {
                            ShimCancel = cancelRequest,
                        },
                            new Metadata { { "macaroon", source.ChannelAdminMacaroon } }
                        );
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while cancelling pending channel with id:{} (hex)",
                    Convert.ToHexString(pendingChannelId));
            }
        }

        public async Task<(PSBT?, bool)> GenerateTemplatePSBT(ChannelOperationRequest? channelOperationRequest)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            //Refresh in case of the view was outdated TODO This might happen in other places
            channelOperationRequest = (await _channelOperationRequestRepository.GetById(channelOperationRequest.Id))!;

            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (channelOperationRequest == null)
            {
                _logger.LogError("Invalid entity refresh on channel operation request");
                return (null, false);
            }

            (PSBT?, bool) result = (null, false);

            if (channelOperationRequest.RequestType != OperationRequestType.Open)
            {
                _logger.LogError("PSBT Generation cancelled, operation type is not open");

                return (null, false);
            }

            if (channelOperationRequest.Status != ChannelOperationRequestStatus.Pending &&
                channelOperationRequest.Status != ChannelOperationRequestStatus.PSBTSignaturesPending)
            {
                _logger.LogError("PSBT Generation cancelled, operation is not in pending state");
                return (null, false);
            }
            var (nbXplorerNetwork, nbxplorerClient) = LightningHelper.GenerateNetwork();

            //UTXOs -> they need to be tracked first on nbxplorer to get results!!
            var derivationStrategy = channelOperationRequest.Wallet.GetDerivationStrategy();

            if (!(await nbxplorerClient.GetStatusAsync()).IsFullySynched)
            {
                _logger.LogError("Error, nbxplorer not fully synched");
                return (null, false);
            }

            if (derivationStrategy == null)
            {
                _logger.LogError("Error while getting the derivation strategy scheme for wallet:{}",
                    channelOperationRequest.Wallet.Id);
                return (null, false);
            }

            //If there is already a PSBT as template with the inputs as still valid UTXOs we avoid generating the whole process again to
            //avoid non-deterministic issues (e.g. Input order and other potential errors)
            var templatePSBT =
                channelOperationRequest.ChannelOperationRequestPsbts.Where(x => x.IsTemplatePSBT).OrderBy(x => x.Id).LastOrDefault();

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
                        "Marking the channel operation request:{} as failed since the original UTXOs are no longer valid",
                        channelOperationRequest.Id);

                    channelOperationRequest.Status = ChannelOperationRequestStatus.Failed;

                    var updateResult = _channelOperationRequestRepository.Update(channelOperationRequest);

                    if (!updateResult.Item1)
                    {
                        _logger.LogError("Error while updating withdrawal request:{}", channelOperationRequest.Id);
                    }

                    return (null, false);
                }
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

                var feeRateResult = await LightningHelper.GetFeeRateResult(nbXplorerNetwork, nbxplorerClient);

                var changeAddress = await nbxplorerClient.GetUnusedAsync(derivationStrategy, DerivationFeature.Change);
                if (changeAddress == null)
                {
                    _logger.LogError("Change address was not found for wallet:{}", channelOperationRequest.Wallet.Id);
                    return (null, false);
                }

                var builder = txBuilder;
                builder.AddCoins(multisigCoins);

                builder.SetSigningOptions(SigHash.None)
                    .SendAllRemainingToChange()
                    .SetChange(changeAddress.Address)
                    .SendEstimatedFees(feeRateResult.FeeRate);

                result.Item1 = builder.BuildPSBT(false);

                //TODO Remove hack when https://github.com/MetacoSA/NBitcoin/issues/1112 is fixed
                foreach (var input in result.Item1.Inputs)
                {
                    input.SighashType = SigHash.None;
                }

                //Additional fields to support PSBT signing with a HW
                foreach (var key in channelOperationRequest.Wallet.Keys)
                {
                    var bitcoinExtPubKey = new BitcoinExtPubKey(key.XPUB, nbXplorerNetwork);

                    var masterFingerprint = HDFingerprint.Parse(key.MasterFingerprint);
                    var rootedKeyPath = new RootedKeyPath(masterFingerprint, new KeyPath(key.Path));

                    //Global xpubs field addition
                    result.Item1.GlobalXPubs.Add(
                        bitcoinExtPubKey,
                        rootedKeyPath
                    );

                    foreach (var selectedUtxo in selectedUtxOs)
                    {
                        var utxoDerivationPath = KeyPath.Parse(key.Path).Derive(selectedUtxo.KeyPath);
                        var derivedPubKey = bitcoinExtPubKey.Derive(selectedUtxo.KeyPath).GetPublicKey();

                        var input = result.Item1.Inputs.FirstOrDefault(input => input?.GetCoin()?.Outpoint == selectedUtxo.Outpoint);
                        var addressRootedKeyPath = new RootedKeyPath(masterFingerprint, utxoDerivationPath);
                        var multisigCoin = multisigCoins.FirstOrDefault(x => x.Outpoint == selectedUtxo.Outpoint);

                        if (multisigCoin != null && input != null && multisigCoin.Redeem.GetAllPubKeys().Contains(derivedPubKey))
                        {
                            input.AddKeyPath(derivedPubKey, addressRootedKeyPath);
                        }
                        else
                        {
                            var errorMessage = $"Invalid derived pub key for utxo:{selectedUtxo.Outpoint}";
                            _logger.LogError(errorMessage);
                            throw new ArgumentException(errorMessage, nameof(derivedPubKey));
                        }
                    }
                }
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

            // The template PSBT is saved for later reuse

            if (result.Item1 != null)
            {
                var psbt = new ChannelOperationRequestPSBT
                {
                    ChannelOperationRequestId = channelOperationRequest.Id,
                    CreationDatetime = DateTimeOffset.Now,
                    IsTemplatePSBT = true,
                    UpdateDatetime = DateTimeOffset.Now,
                    PSBT = result.Item1.ToBase64()
                };

                var addPsbtResult = await _channelOperationRequestPsbtRepository.AddAsync(psbt);

                if (addPsbtResult.Item1 == false)
                {
                    _logger.LogError("Error while saving template PSBT to channel operation request:{}", channelOperationRequest.Id);
                }
            }

            return result;
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

            var satsAmount = channelOperationRequest.SatsAmount;
            var lockedUTXOs = await _ifmutxoRepository.GetLockedUTXOs(ignoredChannelOperationRequestId: channelOperationRequest.Id);

            var (coins, selectedUTXOs) = await LightningHelper.SelectCoins(channelOperationRequest.Wallet,
                satsAmount,
                utxoChanges,
                lockedUTXOs,
                _logger,
                _mapper);

            return (coins, selectedUTXOs);
        }

        public async Task CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            if (channelOperationRequest.RequestType != OperationRequestType.Close)
                throw new ArgumentException("Channel Operation Request type is not of type Close");

            _logger.LogInformation("Channel close request for request id:{}",
                channelOperationRequest.Id);

            try
            {
                if (channelOperationRequest.ChannelId != null)
                {
                    var channel = await _channelRepository.GetById((int)channelOperationRequest.ChannelId);

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
                                    var closePendingTxid = LightningHelper.DecodeTxId(response.ClosePending.Txid);

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
                                    var chanCloseClosingTxid = LightningHelper.DecodeTxId(response.ChanClose.ClosingTxid);
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

                                    var updateChannelResult = _channelRepository.Update(channel);

                                    if (!updateChannelResult.Item1)
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
                if (e.Message.Contains("channel not found"))
                {
                    //We mark it as closed as it no longer exists
                    if (channelOperationRequest.ChannelId != null)
                    {
                        var channel = await _channelRepository.GetById((int)channelOperationRequest.ChannelId);
                        if (channel != null)
                        {
                            channel.Status = Channel.ChannelStatus.Closed;

                            _channelRepository.Update(channel);
                            _logger.LogInformation("Setting channel with id:{} to closed as it no longer exists",
                                channel.Id);

                            //It does not exists, probably was on-chain confirmed
                            //TODO Might be worth in the future check it onchain ?
                            channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmed;

                            _channelOperationRequestRepository.Update(channelOperationRequest);
                        }
                    }
                }
                else
                {
                    _logger.LogError(e,
                        "Channel close request failed for channel operation request:{}",
                        channelOperationRequest.Id);
                    throw;
                }
            }
        }

        public async Task<GetBalanceResponse?> GetWalletBalance(Wallet wallet)
        {
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));
            var client = LightningHelper.GenerateNetwork();
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

            var client = LightningHelper.GenerateNetwork();
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
                if (node.ChannelAdminMacaroon != null)
                {
                    var nodeInfo = await client.GetNodeInfoAsync(new NodeInfoRequest
                    {
                        PubKey = pubkey,
                        IncludeChannels = false
                    }, new Metadata { { "macaroon", node.ChannelAdminMacaroon } });

                    result = nodeInfo?.Node;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while obtaining node info for node with pubkey:{}", pubkey);
            }

            return result;
        }
    }
}