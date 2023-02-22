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
using Lnrpc;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using System.Security.Cryptography;
using AutoMapper;
using FundsManager.Data;
using FundsManager.Helpers;
using FundsManager.Services.Interfaces;
using FundsManager.Services.ServiceHelpers;
using Humanizer;
using Microsoft.EntityFrameworkCore;
using Channel = FundsManager.Data.Models.Channel;
using UTXO = NBXplorer.Models.UTXO;

// ReSharper disable InconsistentNaming

// ReSharper disable IdentifierTypo

namespace FundsManager.Services
{
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
            
            LightningServiceHelper.CheckArgumentsAreValid(channelOperationRequest, OperationRequestType.Open, _logger);

            channelOperationRequest = await _channelOperationRequestRepository.GetById(channelOperationRequest.Id) ??
                                      throw new InvalidOperationException("ChannelOperationRequest not found");

            var (source, destination) = LightningServiceHelper.CheckNodesAreValid(channelOperationRequest, _logger);
            var derivationStrategyBase = LightningServiceHelper.GetDerivationStrategyBase(channelOperationRequest, _logger);
 
            var client = LightningHelper.CreateLightningClient(source.Endpoint);

            var network = CurrentNetworkHelper.GetCurrentNetwork();

            var closeAddress = await LightningServiceHelper.GetCloseAddress(channelOperationRequest, derivationStrategyBase, _nbXplorerService, _logger);

            _logger.LogInformation("Channel open request for  request id: {RequestId} from node: {SourceNodeName} to node: {DestinationNodeName}",
                channelOperationRequest.Id,
                source.Name,
                destination.Name);

            var combinedPSBT = LightningServiceHelper.GetCombinedPsbt(channelOperationRequest, _logger);

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
                                LightningServiceHelper.HandleChannelPending(channelOperationRequest, pendingChannelIdHex, response, _channelOperationRequestRepository, _logger);
                                break;

                            case OpenStatusUpdate.UpdateOneofCase.ChanOpen:
                                await LightningServiceHelper.HandleChannelOpen(channelOperationRequest, response, client, source, closeAddress, context, _channelOperationRequestRepository, _logger);
                                break;

                            case OpenStatusUpdate.UpdateOneofCase.PsbtFund:
                                await LightningServiceHelper.HandlePSBTFund(channelOperationRequest, pendingChannelId, response, network, combinedPSBT, client, source, derivationStrategyBase,
                                    _remoteSignerService, _nbXplorerService, _channelOperationRequestPsbtRepository, _channelOperationRequestRepository, _logger);
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

                LightningServiceHelper.CancelPendingChannel(source, client, pendingChannelId, _logger);

                //TODO Mark as failed (?)
                throw;
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
                await LightningServiceHelper.GetTxInputCoins(channelOperationRequest, _nbXplorerService, derivationStrategy, _ifmutxoRepository ,_logger, _mapper);

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
                result = LightningHelper.AddDerivationData(channelOperationRequest.Wallet.Keys, result, selectedUtxOs, multisigCoins, _logger, channelOperationRequest.Wallet.InternalWalletSubDerivationPath);
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

        public async Task CloseChannel(ChannelOperationRequest channelOperationRequest, bool forceClose = false)
        {
            LightningServiceHelper.CheckArgumentsAreValid(channelOperationRequest, OperationRequestType.Close, _logger);

            _logger.LogInformation("Channel close request for request id: {RequestId}",
                channelOperationRequest.Id);

            try
            {
                if (channelOperationRequest.ChannelId != null)
                {
                    var channel = await _channelRepository.GetById((int)channelOperationRequest.ChannelId);

                    if (channel != null && channelOperationRequest.SourceNode.ChannelAdminMacaroon != null)
                    {


                        var client = LightningHelper.CreateLightningClient(channelOperationRequest.SourceNode.Endpoint);

                        //Time to close the channel
                        var closeChannelResult = client.Execute(x => x.CloseChannel(new CloseChannelRequest
                        {
                            ChannelPoint = new ChannelPoint
                            {
                                FundingTxidStr = channel.FundingTx,
                                OutputIndex = channel.FundingTxOutputIndex
                            },
                            Force = forceClose,
                        }, new Metadata { { "macaroon", channelOperationRequest.SourceNode.ChannelAdminMacaroon } }, null, default));

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
                    derivationFeature, default, true, default);
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



            var client = LightningHelper.CreateLightningClient(node.Endpoint);
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
    }
}