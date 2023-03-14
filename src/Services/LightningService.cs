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
using Blazored.Toast.Services;
using FundsManager.Data;
using FundsManager.Data.Repositories;
using FundsManager.Helpers;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Channel = FundsManager.Data.Models.Channel;
using Transaction = NBitcoin.Transaction;
using UTXO = NBXplorer.Models.UTXO;
using Unmockable;

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

        /// <summary>
        /// Channel balance
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public Task<(long?, long?)> GetChannelBalance(Channel channel);

        /// <summary>
        /// Cancels a pending channel from LND PSBT-based funding of channels
        /// </summary>
        /// <param name="source"></param>
        /// <param name="pendingChannelId"></param>
        /// <param name="client"></param>
        public void CancelPendingChannel(Node source, byte[] pendingChannelId,  IUnmockable<Lightning.LightningClient>? client = null);
    }

    public class LightningService : ILightningService
    {
        private readonly ILogger<LightningService> _logger;
        private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
        private readonly INodeRepository _nodeRepository;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IMapper _mapper;
        private readonly IWalletRepository _walletRepository;
        private readonly IFMUTXORepository _ifmutxoRepository;
        private readonly IChannelOperationRequestPSBTRepository _channelOperationRequestPsbtRepository;
        private readonly IChannelRepository _channelRepository;
        private readonly IRemoteSignerService _remoteSignerService;
        private readonly INBXplorerService _nbXplorerService;

        public LightningService(ILogger<LightningService> logger,
            IChannelOperationRequestRepository channelOperationRequestRepository,
            INodeRepository nodeRepository,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IMapper mapper,
            IWalletRepository walletRepository,
            IFMUTXORepository ifmutxoRepository,
            IChannelOperationRequestPSBTRepository channelOperationRequestPsbtRepository,
            IChannelRepository channelRepository,
            IRemoteSignerService remoteSignerService,
            INBXplorerService nbXplorerService
          )
        
        {
            _logger = logger;
            _channelOperationRequestRepository = channelOperationRequestRepository;
            _nodeRepository = nodeRepository;
            _dbContextFactory = dbContextFactory;
            _mapper = mapper;
            _walletRepository = walletRepository;
            _ifmutxoRepository = ifmutxoRepository;
            _channelOperationRequestPsbtRepository = channelOperationRequestPsbtRepository;
            _channelRepository = channelRepository;
            _remoteSignerService = remoteSignerService;
            _nbXplorerService = nbXplorerService;
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
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            
            CheckArgumentsAreValid(channelOperationRequest, OperationRequestType.Open, _logger);

            channelOperationRequest = await _channelOperationRequestRepository.GetById(channelOperationRequest.Id) ??
                                      throw new InvalidOperationException("ChannelOperationRequest not found");

            var (source, destination) = CheckNodesAreValid(channelOperationRequest, _logger);
            var derivationStrategyBase = GetDerivationStrategyBase(channelOperationRequest, _logger);
 
            var client = CreateLightningClient(source.Endpoint);

            var network = CurrentNetworkHelper.GetCurrentNetwork();

            var closeAddress = await GetCloseAddress(channelOperationRequest, derivationStrategyBase, _nbXplorerService, _logger);

            _logger.LogInformation("Channel open request for  request id: {RequestId} from node: {SourceNodeName} to node: {DestinationNodeName}",
                channelOperationRequest.Id,
                source.Name,
                destination.Name);

            var combinedPSBT = GetCombinedPsbt(channelOperationRequest, _logger);

            //32 bytes of secure randomness for the pending channel id (lnd)
            var pendingChannelId = RandomNumberGenerator.GetBytes(32);
            var pendingChannelIdHex = Convert.ToHexString(pendingChannelId);
            
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

                //Prior to opening the channel, we add the remote node as a peer
                var remoteNodeInfo = await GetNodeInfo(channelOperationRequest.DestNode?.PubKey);
                if (remoteNodeInfo == null)
                {
                    _logger.LogError("Error, remote node with {Pubkey} not found",
                        channelOperationRequest.DestNode?.PubKey);
                    throw new InvalidOperationException();
                }

                //For now, we only rely on pure tcp IPV4 connections
                var addr = remoteNodeInfo.Addresses.FirstOrDefault(x => x.Network == "tcp").Addr;
                
                if(addr == null)
                {
                    _logger.LogError("Error, remote node with {Pubkey} has no tcp IPV4 address",
                        channelOperationRequest.DestNode?.PubKey);
                    throw new InvalidOperationException();
                }
                var isPeerAlreadyConnected = false;

                ConnectPeerResponse connectPeerResponse = null;
                try
                {
                    connectPeerResponse = await client.Execute(x => x.ConnectPeerAsync(new ConnectPeerRequest
                    {
                        Addr = new LightningAddress {Host = addr, Pubkey = remoteNodeInfo.PubKey},
                        Perm = true
                    }, new Metadata
                    {
                        {"macaroon", source.ChannelAdminMacaroon}
                    }, null, default));
                }
                //We avoid to stop the method if the peer is already connected
                catch (RpcException e)
                {
                    if (!e.Message.Contains("already connected to peer"))
                    {
                        throw;
                    }
                    else
                    {
                        isPeerAlreadyConnected = true;
                    }
                }

                if (connectPeerResponse != null || isPeerAlreadyConnected)
                {
                    if (isPeerAlreadyConnected)
                    {
                        _logger.LogInformation("Peer: {Pubkey} already connected", remoteNodeInfo.PubKey);
                    }
                    else
                    {
                        _logger.LogInformation("Peer connected to {Pubkey}", remoteNodeInfo.PubKey);
                    }
                }
                else
                {
                    _logger.LogError("Error, peer not connected to {Pubkey} on address: {address}",
                        remoteNodeInfo.PubKey, addr);
                    throw new InvalidOperationException();
                }

                //We launch a openstatusupdate stream for all the events when calling OpenChannel api method from LND
                if (source.ChannelAdminMacaroon != null)
                {
                    var openStatusUpdateStream = client.Execute(x => x.OpenChannel(openChannelRequest,
                        new Metadata {{"macaroon", source.ChannelAdminMacaroon}}, null, default
                    ));

                    await foreach (var response in openStatusUpdateStream.ResponseStream.ReadAllAsync())
                    {
                        switch (response.UpdateCase)
                        {
                            case OpenStatusUpdate.UpdateOneofCase.None:
                                break;

                            case OpenStatusUpdate.UpdateOneofCase.ChanPending:
                                //Channel funding tx on mempool and pending status on lnd

                                _logger.LogInformation(
                                    "Channel pending for channel operation request id: {RequestId} for pending channel id: {ChannelId}",
                                    channelOperationRequest.Id, pendingChannelIdHex);

                                channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmationPending;
                                channelOperationRequest.TxId = LightningHelper.DecodeTxId(response.ChanPending.Txid);
                                _channelOperationRequestRepository.Update(channelOperationRequest);

                                break;

                            case OpenStatusUpdate.UpdateOneofCase.ChanOpen:
                                _logger.LogInformation(
                                    "Channel opened for channel operation request request id: {RequestId}, channel point: {ChannelPoint}",
                                    channelOperationRequest.Id, response.ChanOpen.ChannelPoint.ToString());

                                channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmed;
                                _channelOperationRequestRepository.Update(channelOperationRequest);

                                var fundingTx = LightningHelper.DecodeTxId(response.ChanOpen.ChannelPoint.FundingTxidBytes);

                                //Get the channels to find the channelId, not the temporary one
                                var channels = await client.Execute(x => x.ListChannelsAsync(new ListChannelsRequest(),
                                    new Metadata {{"macaroon", source.ChannelAdminMacaroon}}, null, default));
                                var currentChannel = channels.Channels.SingleOrDefault(x=> x.ChannelPoint == $"{fundingTx}:{response.ChanOpen.ChannelPoint.OutputIndex}");

                                if(currentChannel == null)
                                {
                                    _logger.LogError("Error, channel not found for channel point: {ChannelPoint}",
                                        response.ChanOpen.ChannelPoint.ToString());
                                    throw new InvalidOperationException();
                                }

                                var channel = new Channel
                                {
                                    ChanId = currentChannel.ChanId,
                                    CreationDatetime = DateTimeOffset.Now,
                                    FundingTx = fundingTx,
                                    FundingTxOutputIndex = response.ChanOpen.ChannelPoint.OutputIndex,
                                    BtcCloseAddress = closeAddress?.Address.ToString(),
                                    SatsAmount = channelOperationRequest.SatsAmount,
                                    UpdateDatetime = DateTimeOffset.Now,
                                    Status = Channel.ChannelStatus.Open,
                                    SourceNodeId = channelOperationRequest.SourceNode.Id,
                                    DestinationNodeId = channelOperationRequest.DestNode.Id,
                                    CreatedByNodeGuard = true
                                };

                                await context.AddAsync(channel);

                                var addChannelResult = (await context.SaveChangesAsync()) > 0;

                                if (addChannelResult == false)
                                {
                                    _logger.LogError(
                                        "Channel for channel operation request id: {RequestId} could not be created, reason: {Reason}",
                                        channelOperationRequest.Id,
                                        "Could not persist to db");
                                }

                                channelOperationRequest.ChannelId = channel.Id;
                                channelOperationRequest.DestNode = null;

                                var channelUpdate = _channelOperationRequestRepository.Update(channelOperationRequest);

                                if (channelUpdate.Item1 == false)
                                {
                                    _logger.LogError(
                                        "Could not assign channel id to channel operation request: {RequestId} reason: {Reason}",
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

                                    PSBT? finalSignedPSBT = null;
                                    //We check the way the fundsmanager signs, with the remoteFundsManagerSigner or by itself.
                                    if (Constants.ENABLE_REMOTE_SIGNER)
                                    {
                                        finalSignedPSBT = await _remoteSignerService.Sign(changeFixedPSBT);
                                        if (finalSignedPSBT == null)
                                        {
                                            const string errorMessage = "The signed PSBT was null, something went wrong while signing with the remote signer";
                                            _logger.LogError(errorMessage);
                                            throw new Exception(
                                                errorMessage);
                                        }
                                    }
                                    else
                                    {
                                        finalSignedPSBT = await SignPSBT(channelOperationRequest,
                                            _nbXplorerService,
                                            derivationStrategyBase,
                                            channelfundingTx,
                                            network,
                                            changeFixedPSBT,
                                            _logger);
                                        
                                        if (finalSignedPSBT == null)
                                        {
                                            const string errorMessage = "The signed PSBT was null, something went wrong while signing with the embedded signer";
                                            _logger.LogError(errorMessage);
                                            throw new Exception(
                                                errorMessage);
                                        }
                                    }

                                    //We store the final signed PSBT without being finalized for debugging purposes
                                    var signedChannelOperationRequestPsbt = new ChannelOperationRequestPSBT
                                    {
                                        ChannelOperationRequestId = channelOperationRequest.Id,
                                        PSBT = finalSignedPSBT.ToBase64(),
                                        CreationDatetime = DateTimeOffset.Now,
                                        IsInternalWalletPSBT = true
                                    };

                                    var addResult = await _channelOperationRequestPsbtRepository.AddAsync(signedChannelOperationRequestPsbt);

                                    if (!addResult.Item1)
                                    {
                                        _logger.LogError("Could not store the signed PSBT for channel operation request id: {RequestId} reason: {Reason}", channelOperationRequest.Id, addResult.Item2);
                                    }

                                    //Time to finalize the PSBT and broadcast the tx

                                    var finalizedPSBT = finalSignedPSBT.Finalize();

                                    //Sanity check
                                    finalizedPSBT.AssertSanity();

                                    channelfundingTx = finalizedPSBT.ExtractTransaction();

                                    //Just a check of the tx based on the finalizedPSBT
                                    var checkTx = channelfundingTx.Check();

                                    if (checkTx == TransactionCheckResult.Success)
                                    {
                                        //We tell lnd to verify the psbt
                                        client.Execute(x => x.FundingStateStep(
                                            new FundingTransitionMsg
                                            {
                                                PsbtVerify = new FundingPsbtVerify
                                                {
                                                    FundedPsbt =
                                                        ByteString.CopyFrom(Convert.FromHexString(finalizedPSBT.ToHex())),
                                                    PendingChanId = ByteString.CopyFrom(pendingChannelId)
                                                }
                                            }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } }, null, default));

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
                                                    "Error while saving the finalised PSBT for channel operation request with id: {RequestId}",
                                                    channelOperationRequest.Id);
                                            }
                                        }

                                        var fundingStateStepResp = await client.Execute(x => x.FundingStateStepAsync(
                                            new FundingTransitionMsg
                                            {
                                                PsbtFinalize = new FundingPsbtFinalize
                                                {
                                                    PendingChanId = ByteString.CopyFrom(pendingChannelId),
                                                    //FinalRawTx = ByteString.CopyFrom(Convert.FromHexString(finalTxHex)),
                                                    SignedPsbt =
                                                        ByteString.CopyFrom(Convert.FromHexString(finalizedPSBT.ToHex()))
                                                },
                                            }, new Metadata { { "macaroon", source.ChannelAdminMacaroon } }, null, default));
                                    }
                                    else
                                    {
                                        CancelPendingChannel(source, pendingChannelId, client);
                                    }
                                }
                                else
                                {
                                    CancelPendingChannel(source, pendingChannelId, client);
                                }

                                break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Channel open request failed for channel operation request: {RequestId} from node: {SourceNodeName} to node: {DestinationNodeName}",
                    channelOperationRequest.Id,
                    source.Name,
                    destination.Name);

                CancelPendingChannel(source, pendingChannelId, client);

                //TODO Mark as failed (?)
                throw;
            }
        }

        public static Func<string?, IUnmockable<Lightning.LightningClient>> CreateLightningClient = (endpoint) =>
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Endpoint cannot be null");
            }

            //Setup of grpc lnd api client (Lightning.proto)
            //Hack to allow self-signed https grpc calls
            var httpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            var grpcChannel = GrpcChannel.ForAddress($"https://{endpoint}",
                new GrpcChannelOptions { HttpHandler = httpHandler });

            return new Lightning.LightningClient(grpcChannel).Wrap();
        };

        public static PSBT GetCombinedPsbt(ChannelOperationRequest channelOperationRequest, ILogger? _logger = null)
        {
            //PSBT Combine
            var signedPsbts = channelOperationRequest.ChannelOperationRequestPsbts.Where(x =>
                    !x.IsFinalisedPSBT && !x.IsInternalWalletPSBT && !x.IsTemplatePSBT);
            var signedPsbts2 = signedPsbts.Select(x => x.PSBT);

            var combinedPSBT = LightningHelper.CombinePSBTs(signedPsbts2, _logger);

            if (combinedPSBT != null) return combinedPSBT;
            
            var invalidPsbtNullToBeUsedForTheRequest = $"Invalid PSBT(null) to be used for the channel op request:{channelOperationRequest.Id}";
            _logger?.LogError(invalidPsbtNullToBeUsedForTheRequest);

            throw new ArgumentException(invalidPsbtNullToBeUsedForTheRequest, nameof(combinedPSBT));
        }
        
        public static async Task<KeyPathInformation?> GetCloseAddress(ChannelOperationRequest channelOperationRequest,
            DerivationStrategyBase derivationStrategyBase, INBXplorerService nbXplorerService, ILogger? _logger = null)
        {
            var closeAddress =await 
               nbXplorerService.GetUnusedAsync(derivationStrategyBase, DerivationFeature.Deposit, 0, true, default);

            if (closeAddress != null) return closeAddress;
            
            var closeAddressNull =  $"Closing address was null for an operation on wallet:{channelOperationRequest.Wallet.Id}";
            _logger?.LogError(closeAddressNull);

            throw new ArgumentException(closeAddressNull);
        }

        public static DerivationStrategyBase GetDerivationStrategyBase(ChannelOperationRequest channelOperationRequest, ILogger? _logger = null)
        {
            //Derivation strategy for the multisig address based on its wallet
            var derivationStrategyBase = channelOperationRequest.Wallet.GetDerivationStrategy();

            if (derivationStrategyBase != null) return derivationStrategyBase;
            
            var derivationNull = $"Derivation scheme not found for wallet:{channelOperationRequest.Wallet.Id}";
            
            _logger?.LogError(derivationNull);

            throw new ArgumentException(derivationNull);
        }
        
        public static void CheckArgumentsAreValid(ChannelOperationRequest channelOperationRequest, OperationRequestType requestype, ILogger? _logger = null)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));

            if (channelOperationRequest.RequestType == requestype) return;
            
            string requestInvalid = $"Invalid request. Requested ${channelOperationRequest.RequestType.ToString()} on ${requestype.ToString()} method";

            _logger?.LogError(requestInvalid);

            throw new ArgumentOutOfRangeException(requestInvalid);
        }
        
        public static (Node, Node) CheckNodesAreValid(ChannelOperationRequest channelOperationRequest, ILogger? _logger = null)
        {
            var source = channelOperationRequest.SourceNode;
            var destination = channelOperationRequest.DestNode;

            if (source == null || destination == null)
            {
                throw new ArgumentException("Source or destination null", nameof(source));
            }
            
            if (source.ChannelAdminMacaroon == null)
            {
                throw new UnauthorizedAccessException("Macaroon not set for source channel");
            }

            if (source.PubKey != destination.PubKey) return (source, destination);
            
            const string aNodeCannotOpenAChannelToItself = "A node cannot open a channel to itself.";

            _logger?.LogError(aNodeCannotOpenAChannelToItself);

            throw new ArgumentException(aNodeCannotOpenAChannelToItself);
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
        public static async Task<PSBT> SignPSBT(
                ChannelOperationRequest channelOperationRequest, INBXplorerService nbXplorerService,
                DerivationStrategyBase derivationStrategyBase, Transaction channelfundingTx, Network network, PSBT changeFixedPSBT, ILogger? logger = null)
            
        {
            //We get the UTXO keyPath / derivation path from nbxplorer

            var UTXOs = await nbXplorerService.GetUTXOsAsync(derivationStrategyBase, default);
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

                logger?.LogError(errorKeypathsForTheUtxosUsedInThisTxAreNotFound);

                throw new ArgumentException(
                    errorKeypathsForTheUtxosUsedInThisTxAreNotFound);
            }


            Dictionary<NBitcoin.OutPoint,NBitcoin.Key> privateKeysForUsedUTXOs;
            try
            {
                privateKeysForUsedUTXOs = txInKeyPathDictionary.ToDictionary(x => x.Key.PrevOut, x => 
                    channelOperationRequest.Wallet.DeriveUtxoPrivateKey(network, x.Value));
            }
            catch (Exception e)
            {
                var errorParsingSubderivationPath =
                    $"Invalid Internal Wallet Subderivation Path for wallet:{channelOperationRequest.WalletId}";
                logger?.LogError(errorParsingSubderivationPath);

                throw new ArgumentException(
                    errorParsingSubderivationPath);
            }

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
                logger?.LogError(invalidNoOfPartialSignatures);

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
        public void CancelPendingChannel(Node source, byte[] pendingChannelId, IUnmockable<Lightning.LightningClient>? client = null)
        {
            try
            {
                if (client == null)
                {
                    client = CreateLightningClient(source.Endpoint);
                }
                if (pendingChannelId != null)
                {
                    var cancelRequest = new FundingShimCancel
                    {
                        PendingChanId = ByteString.CopyFrom(pendingChannelId)
                    };

                    if (source.ChannelAdminMacaroon != null)
                    {
                        var cancelResult = client.Execute(x => x.FundingStateStep(new FundingTransitionMsg
                        {
                            ShimCancel = cancelRequest,
                        },
                            new Metadata { { "macaroon", source.ChannelAdminMacaroon } }, null, default));
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while cancelling pending channel with id: {ChannelId} (hex)",
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

            //UTXOs -> they need to be tracked first on nbxplorer to get results!!
            var derivationStrategy = channelOperationRequest.Wallet.GetDerivationStrategy();

            var nbXplorerServiceGetStatusAsync = await _nbXplorerService.GetStatusAsync(default);
            
            if (!nbXplorerServiceGetStatusAsync.IsFullySynched)
            {
                _logger.LogError("Error, nbxplorer not fully synched");
                return (null, false);
            }

            if (derivationStrategy == null)
            {
                _logger.LogError("Error while getting the derivation strategy scheme for wallet: {WalletId}",
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
                var currentUtxos = await _nbXplorerService.GetUTXOsAsync(derivationStrategy, default);
                if (parsedTemplatePSBT.Inputs.All(
                        x => currentUtxos.Confirmed.UTXOs.Select(x => x.Outpoint).Contains(x.PrevOut)))
                {
                    return (parsedTemplatePSBT, false);
                }
                else
                {
                    //We mark the request as failed since we would need to invalidate existing PSBTs
                    _logger.LogError(
                        "Marking the channel operation request: {RequestId} as failed since the original UTXOs are no longer valid",
                        channelOperationRequest.Id);

                    channelOperationRequest.Status = ChannelOperationRequestStatus.Failed;

                    var updateResult = _channelOperationRequestRepository.Update(channelOperationRequest);

                    if (!updateResult.Item1)
                    {
                        _logger.LogError("Error while updating withdrawal request: {RequestId}", channelOperationRequest.Id);
                    }

                    return (null, false);
                }
            }

            var (multisigCoins, selectedUtxOs) =
                await GetTxInputCoins(channelOperationRequest, _nbXplorerService, derivationStrategy);

            if (multisigCoins == null || !multisigCoins.Any())
            {
                _logger.LogError(
                    "Cannot generate base template PSBT for {Visibility} channel operation request: {RequestId}, no UTXOs found for the wallet: {WalletId}",
                    channelOperationRequest.Id,
                    channelOperationRequest.IsChannelPrivate ? "private" : "public",
                    channelOperationRequest.WalletId);

                return (null, true); //true means no UTXOS
            }

            try
            {
                //We got enough inputs to fund the TX so time to build the PSBT, the funding address of the channel will be added later by LND

                var network = CurrentNetworkHelper.GetCurrentNetwork();
                var txBuilder = network.CreateTransactionBuilder();

                var feeRateResult = await LightningHelper.GetFeeRateResult(network, _nbXplorerService);

                var changeAddress = await _nbXplorerService.GetUnusedAsync(derivationStrategy, DerivationFeature.Change, 0, false, default);
                if (changeAddress == null)
                {
                    _logger.LogError("Change address was not found for wallet: {WalletId}", channelOperationRequest.Wallet.Id);
                    return (null, false);
                }

                var builder = txBuilder;
                builder.AddCoins(multisigCoins);

                builder.SetSigningOptions(SigHash.None)
                    .SendAllRemainingToChange()
                    .SetChange(changeAddress.Address)
                    .SendEstimatedFees(feeRateResult.FeeRate);

                result.Item1 = builder.BuildPSBT(false);

                //Hack, see https://github.com/MetacoSA/NBitcoin/issues/1112 for details
                foreach (var input in result.Item1.Inputs)
                {
                    input.SighashType = SigHash.None;
                }

                //Additional fields to support PSBT signing with a HW or the Remote Signer 
                var psbt = LightningHelper.AddDerivationData(channelOperationRequest.Wallet, result.Item1, selectedUtxOs, multisigCoins, _logger);
                result = (psbt, result.Item2);
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
                    _logger.LogError("Error while saving template PSBT to channel operation request: {RequestId}", channelOperationRequest.Id);
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
        private async Task<(List<ICoin> coins, List<UTXO> selectedUTXOs)> GetTxInputCoins(
            ChannelOperationRequest channelOperationRequest,
            INBXplorerService nbXplorerService,
            DerivationStrategyBase derivationStrategy)
        {
            var utxoChanges = await nbXplorerService.GetUTXOsAsync(derivationStrategy, default);
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
            CheckArgumentsAreValid(channelOperationRequest, OperationRequestType.Close, _logger);

            _logger.LogInformation("Channel close request for request id: {RequestId}",
                channelOperationRequest.Id);

            try
            {
                if (channelOperationRequest.ChannelId != null)
                {
                    var channel = await _channelRepository.GetById((int)channelOperationRequest.ChannelId);
                    
                    var node = String.IsNullOrEmpty(channelOperationRequest.SourceNode.ChannelAdminMacaroon)
                        ? channelOperationRequest.DestNode
                        : channelOperationRequest.SourceNode;

                    if (channel != null && node.ChannelAdminMacaroon != null)
                    {


                        var client = CreateLightningClient(node.Endpoint);

                        //Time to close the channel
                        var closeChannelResult = client.Execute(x => x.CloseChannel(new CloseChannelRequest
                        {
                            ChannelPoint = new ChannelPoint
                            {
                                FundingTxidStr = channel.FundingTx,
                                OutputIndex = channel.FundingTxOutputIndex
                            },
                            Force = forceClose,
                        }, new Metadata { { "macaroon", node.ChannelAdminMacaroon } }, null, default));

                        _logger.LogInformation("Channel close request: {RequestId} triggered",
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
                                        "Channel close request in status: {RequestStatus} for channel operation request: {RequestId} for channel: {ChannelId} closing txId: {TxId}",
                                        nameof(ChannelOperationRequestStatus.OnChainConfirmationPending),
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
                                            "Error while updating channel operation request id: {RequestId} to status: {RequestStatus}",
                                            channelOperationRequest.Id,
                                            nameof(ChannelOperationRequestStatus.OnChainConfirmationPending));
                                    }

                                    break;

                                case CloseStatusUpdate.UpdateOneofCase.ChanClose:

                                    //TODO Review why chanclose.success it is false for confirmed closings of channels
                                    var chanCloseClosingTxid = LightningHelper.DecodeTxId(response.ChanClose.ClosingTxid);
                                    _logger.LogInformation(
                                        "Channel close request in status: {RequestStatus} for channel operation request: {RequestId} for channel: {ChannelId} closing txId: {TxId}",
                                        nameof(ChannelOperationRequestStatus.OnChainConfirmed),
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
                                            "Error while updating channel operation request id: {RequestId} to status: {RequestStatus}",
                                            channelOperationRequest.Id,
                                            nameof(ChannelOperationRequestStatus.OnChainConfirmed));
                                    }

                                    channel.Status = Channel.ChannelStatus.Closed;

                                    var updateChannelResult = _channelRepository.Update(channel);

                                    if (!updateChannelResult.Item1)
                                    {
                                        _logger.LogError(
                                            "Error while setting to closed status a closed channel with id: {ChannelId}",
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
                            _logger.LogInformation("Setting channel with id: {ChannelId} to closed as it no longer exists",
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
                        "Channel close request failed for channel operation request: {RequestId}",
                        channelOperationRequest.Id);
                    throw;
                }
            }
        }

        public async Task<GetBalanceResponse?> GetWalletBalance(Wallet wallet)
        {
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));
            
            GetBalanceResponse? getBalanceResponse = null;
            try
            {
                getBalanceResponse = await _nbXplorerService.GetBalanceAsync(wallet.GetDerivationStrategy(), default);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while getting wallet balance for wallet: {WalletId}", wallet.Id);
            }

            return getBalanceResponse;
        }

        public async Task<BitcoinAddress?> GetUnusedAddress(Wallet wallet,
            DerivationFeature derivationFeature)
        {
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));

            KeyPathInformation? keyPathInformation = null;
            try
            {
                keyPathInformation =await _nbXplorerService.GetUnusedAsync(wallet.GetDerivationStrategy(),
                    derivationFeature, default, false, default);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while getting wallet balance for wallet: {WalletId}", wallet.Id);
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



            var client = CreateLightningClient(node.Endpoint);
            try
            {
                if (node.ChannelAdminMacaroon != null)
                {
                    var nodeInfo = await client.Execute(x => x.GetNodeInfoAsync(new NodeInfoRequest
                    {
                        PubKey = pubkey,
                        IncludeChannels = false
                    }, new Metadata { { "macaroon", node.ChannelAdminMacaroon } }, null, default));

                    result = nodeInfo?.Node;
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while obtaining node info for node with pubkey: {PubKey}", pubkey);
            }

            return result;
        }

        public async Task<(long?, long?)> GetChannelBalance(Channel channel)
        {
            IUnmockable<Lightning.LightningClient> client;
            var destinationNode = await _nodeRepository.GetById(channel.DestinationNodeId);
            var sourceNode = await _nodeRepository.GetById(channel.SourceNodeId);
            var node = String.IsNullOrEmpty(sourceNode.ChannelAdminMacaroon) ? destinationNode : sourceNode;
            
            client = CreateLightningClient(node.Endpoint);
            var result = client.Execute(x => x.ListChannels(new ListChannelsRequest(), 
                new Metadata {
                {"macaroon", node.ChannelAdminMacaroon}
            }, null, default));
            
            var chan = result.Channels.FirstOrDefault(x => x.ChanId == channel.ChanId);
            if(chan == null)
                return (null, null);

            var res = (chan.LocalBalance, chan.RemoteBalance);
            return res;
        }
    }
}