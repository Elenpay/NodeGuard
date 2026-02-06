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

using System.Diagnostics;
using System.Runtime.InteropServices;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using Google.Protobuf;
using Grpc.Core;
using Lnrpc;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using System.Security.Cryptography;
using NodeGuard.Data;
using NodeGuard.Helpers;
using Microsoft.EntityFrameworkCore;
using Routerrpc;
using Channel = NodeGuard.Data.Models.Channel;
using Transaction = NBitcoin.Transaction;

// ReSharper disable InconsistentNaming

// ReSharper disable IdentifierTypo

namespace NodeGuard.Services
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
        /// Gets a dictionary of the local and remote balance of all the channels managed by NG
        /// </summary>
        /// <returns></returns>
        public Task<Dictionary<ulong, ChannelState>> GetChannelsState();

        /// <summary>
        /// Cancels a pending channel from LND PSBT-based funding of channels
        /// </summary>
        /// <param name="source"></param>
        /// <param name="pendingChannelId"></param>
        /// <param name="client"></param>
        public void CancelPendingChannel(Node source, byte[] pendingChannelId, Lightning.LightningClient? client = null);

        /// <summary>
        /// Creates a channel object given a source node and a channel point
        /// </summary>
        /// <param name="source"></param>
        /// <param name="destId"></param>
        /// <param name="channelPoint"></param>
        /// <param name="satsAmount"></param>
        /// <param name="closeAddress"></param>
        /// <returns></returns>
        public Task<Channel> CreateChannel(Node source, int destId, ChannelPoint channelPoint, long satsAmount, string? closeAddress = null);

        /// <summary>
        /// Lists all channels for a given node
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        public Task<ListChannelsResponse?> ListChannels(Node node);

        /// <summary>
        /// Gets the channel balance for a given node
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        public Task<ChannelBalanceResponse?> ChannelBalanceAsync(Node node);

        /// <summary>
        /// Estimates the routing fee for a payment
        /// </summary>
        /// <param name="destPubkey">The destination node pubkey</param>
        /// <param name="amountSat">The amount in satoshis</param>
        /// <param name="paymentRequest">Optional payment request</param>
        /// <param name="timeout">Optional timeout in seconds</param>
        /// <returns></returns>
        public Task<RouteFeeResponse?> EstimateRouteFee(string destPubkey, long amountSat, string? paymentRequest = null, uint timeout = 30);
    }

    public class LightningService : ILightningService
    {
        private readonly ILogger<LightningService> _logger;
        private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
        private readonly INodeRepository _nodeRepository;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IChannelOperationRequestPSBTRepository _channelOperationRequestPsbtRepository;
        private readonly IChannelRepository _channelRepository;
        private readonly IRemoteSignerService _remoteSignerService;
        private readonly INBXplorerService _nbXplorerService;
        private readonly ICoinSelectionService _coinSelectionService;
        private readonly ILightningClientService _lightningClientService;
        private readonly ILightningRouterService _lightningRouterService;

        public LightningService(ILogger<LightningService> logger,
            IChannelOperationRequestRepository channelOperationRequestRepository,
            INodeRepository nodeRepository,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            IChannelOperationRequestPSBTRepository channelOperationRequestPsbtRepository,
            IChannelRepository channelRepository,
            IRemoteSignerService remoteSignerService,
            INBXplorerService nbXplorerService,
            ICoinSelectionService coinSelectionService,
            ILightningClientService lightningClientService,
            ILightningRouterService lightningRouterService
        )

        {
            _logger = logger;
            _channelOperationRequestRepository = channelOperationRequestRepository;
            _nodeRepository = nodeRepository;
            _dbContextFactory = dbContextFactory;
            _channelOperationRequestPsbtRepository = channelOperationRequestPsbtRepository;
            _channelRepository = channelRepository;
            _remoteSignerService = remoteSignerService;
            _nbXplorerService = nbXplorerService;
            _coinSelectionService = coinSelectionService;
            _lightningClientService = lightningClientService;
            _lightningRouterService = lightningRouterService;
        }

        /// <summary>
        /// Record used to match AWS SignPSBT function input
        /// </summary>
        /// <param name="Psbt"></param>
        /// <param name="EnforcedSighash"></param>
        /// <param name="Network"></param>
        /// <param name="AwsKmsKeyId"></param>
        public record RemoteSignerRequest(string Psbt, SigHash? EnforcedSighash, string Network);

        /// <summary>
        /// Record used to match AWS SignPSBT funciton output
        /// </summary>
        /// <param name="Psbt"></param>
        public record RemoteSignerResponse(string? Psbt);

        public async Task OpenChannel(ChannelOperationRequest channelOperationRequest)
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            CheckArgumentsAreValid(channelOperationRequest, OperationRequestType.Open, _logger);

            channelOperationRequest = await _channelOperationRequestRepository.GetById(channelOperationRequest.Id) ??
                                      throw new InvalidOperationException("ChannelOperationRequest not found");

            // If the request already has a txid it means that the open channel flow was already executed and we are probably in a retry, in this case we exit early, in the likely scenario we did not catch the channel opening, the channel monitoring job will create the channel on its own
            if (!string.IsNullOrWhiteSpace(channelOperationRequest.TxId))
            {
                var statusMessage =
                    $"Funding tx already set (txid: {channelOperationRequest.TxId}); skipping open flow";
                _logger.LogInformation(statusMessage);

                channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmationPending;
                channelOperationRequest.StatusLogs?.Add(ChannelStatusLog.Info(statusMessage));

                var (isSuccess, error) = _channelOperationRequestRepository.Update(channelOperationRequest);
                if (!isSuccess)
                {
                    _logger.LogWarning(
                        "Request has a txid, but could not update status for request id: {RequestId} reason: {Reason}",
                        channelOperationRequest.Id, error);
                }

                return;
            }

            // If the request is already failed, we exit early to avoid retrying an already failed request
            if (channelOperationRequest.Status == ChannelOperationRequestStatus.Failed)
            {
                _logger.LogInformation(
                    "Channel operation request with id: {RequestId} is already marked as failed, skipping execution",
                    channelOperationRequest.Id);
                return;
            }

            var (source, destination) = CheckNodesAreValid(channelOperationRequest, _logger);
            var derivationStrategyBase = GetDerivationStrategyBase(channelOperationRequest, _logger);

            var client = _lightningClientService.GetLightningClient(source.Endpoint);

            var network = CurrentNetworkHelper.GetCurrentNetwork();


            _logger.LogInformation(
                "Channel open request for  request id: {RequestId} from node: {SourceNodeName} to node: {DestinationNodeName}",
                channelOperationRequest.Id,
                source.Name,
                destination.Name);

            var combinedPSBT = GetCombinedPsbt(channelOperationRequest, _logger);


            //32 bytes of secure randomness for the pending channel id (lnd)
            var pendingChannelId = RandomNumberGenerator.GetBytes(32);
            var pendingChannelIdHex = Convert.ToHexString(pendingChannelId);

            try
            {
                var humanSignaturesCount = channelOperationRequest.ChannelOperationRequestPsbts.Count(
                    x => channelOperationRequest.Wallet != null &&
                         !x.IsFinalisedPSBT &&
                         !x.IsInternalWalletPSBT &&
                         !x.IsTemplatePSBT);

                //If it is a hot wallet, we dont check the number of (human) signatures
                if (channelOperationRequest.Wallet != null && !channelOperationRequest.Wallet.IsHotWallet &&
                    channelOperationRequest.Wallet != null &&
                    humanSignaturesCount != channelOperationRequest.Wallet.MofN - 1)
                {
                    _logger.LogError(
                        "The number of human signatures does not match the number of signatures required for this wallet, expected {MofN} but got {HumanSignaturesCount}",
                        channelOperationRequest.Wallet.MofN - 1, humanSignaturesCount);
                    throw new InvalidOperationException(
                        "The number of human signatures does not match the number of signatures required for this wallet");
                }

                if (channelOperationRequest.Changeless && combinedPSBT.Outputs.Any())
                {
                    _logger.LogError("Changeless channel operation request cannot have outputs at this stage");
                    throw new InvalidOperationException(
                        "Changeless channel operation request cannot have outputs at this stage");
                }

                //Prior to opening the channel, we add the remote node as a peer
                var remoteNodeInfo = await GetNodeInfo(channelOperationRequest.DestNode?.PubKey);
                if (remoteNodeInfo == null)
                {
                    _logger.LogError("Error, remote node with {Pubkey} not found",
                        channelOperationRequest.DestNode?.PubKey);
                    throw new InvalidOperationException();
                }

                var feeRate = await _nbXplorerService.GetFeesByType(channelOperationRequest.MempoolRecommendedFeesType) ?? channelOperationRequest.FeeRate;
                var initialFeeRate = feeRate ??
                                     (await LightningHelper.GetFeeRateResult(network, _nbXplorerService)).FeeRate
                                     .SatoshiPerByte;

                var fundingAmount = GetFundingAmount(channelOperationRequest, combinedPSBT, initialFeeRate);

                var openChannelRequest = await CreateOpenChannelRequest(channelOperationRequest, combinedPSBT,
                    remoteNodeInfo, fundingAmount, pendingChannelId, derivationStrategyBase);

                await _lightningClientService.ConnectToPeer(source, channelOperationRequest.DestNode?.PubKey, client);

                //We launch a openstatusupdate stream for all the events when calling OpenChannel api method from LND
                if (source.ChannelAdminMacaroon != null)
                {
                    var openStatusUpdateStream = _lightningClientService.OpenChannel(source, openChannelRequest, client);

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

                                channelOperationRequest.Status =
                                    ChannelOperationRequestStatus.OnChainConfirmationPending;
                                channelOperationRequest.TxId = LightningHelper.DecodeTxId(response.ChanPending.Txid);
                                _channelOperationRequestRepository.Update(channelOperationRequest);

                                break;

                            case OpenStatusUpdate.UpdateOneofCase.ChanOpen:
                                await OnStatusChannelOpened(channelOperationRequest, source, response.ChanOpen.ChannelPoint, openChannelRequest.CloseAddress);
                                break;

                            case OpenStatusUpdate.UpdateOneofCase.PsbtFund:
                                channelOperationRequest.Status = ChannelOperationRequestStatus.FinalizingPSBT;
                                channelOperationRequest.FeeRate = feeRate;
                                var (isSuccess, error) =
                                    _channelOperationRequestRepository.Update(channelOperationRequest);
                                if (!isSuccess)
                                {
                                    var errorMessage = string.Format(
                                        "Request in funding stage, but could not update status to {Status} for request id: {RequestId} reason: {Reason}",
                                        ChannelOperationRequestStatus.FinalizingPSBT, channelOperationRequest.Id,
                                        error);
                                    _logger.LogError(errorMessage);
                                    throw new Exception(errorMessage);
                                }

                                //We got the funded PSBT, we need to tweak the tx outputs and mimick lnd-cli calls
                                var hexPSBT = Convert.ToHexString(response.PsbtFund.Psbt.ToByteArray());
                                if (PSBT.TryParse(hexPSBT, network,
                                        out var fundedPSBT))
                                {
                                    fundedPSBT.AssertSanity();

                                    // There's a long standing issue on LND that if you psbt fund a channel and the input is spent the channel is actually created and pending for a long time if not forever and you need to lncli abandonchannel, this is safeguard based on our nbxplorer instance confirmed UTXO set.
                                    await ValidatePSBTInputsAreSpendable(channelOperationRequest, fundedPSBT,
                                        derivationStrategyBase);

                                    //We ensure to SigHash.None
                                    fundedPSBT.Settings.SigningOptions = new SigningOptions
                                    {
                                        SigHash = SigHash.None
                                    };

                                    var channelfundingTx = fundedPSBT.GetGlobalTransaction();
                                    var totalOut = new Money(channelOperationRequest.SatsAmount, MoneyUnit.Satoshi);

                                    if (!channelOperationRequest.Changeless)
                                    {
                                        if (fundedPSBT.TryGetVirtualSize(out var vsize))
                                        {
                                            var totalIn = fundedPSBT.Inputs.Sum(i => i.GetTxOut()?.Value);
                                            //We manually fix the change (it was wrong from the Base template due to nbitcoin requiring a change on a PSBT)
                                            var totalChangefulFees =
                                                new Money(vsize * initialFeeRate, MoneyUnit.Satoshi);
                                            var changeOutput =
                                                channelfundingTx.Outputs.SingleOrDefault(o =>
                                                    o.Value != channelOperationRequest.SatsAmount) ??
                                                channelfundingTx.Outputs.First();
                                            changeOutput.Value = totalIn - totalOut - totalChangefulFees;
                                            if (changeOutput.Value < 0)
                                            {
                                                throw new NotEnoughRoomInUtxosForFeesException();
                                            }

                                            //We merge changeFixedPSBT with the other PSBT with the change fixed
                                            fundedPSBT = channelfundingTx.CreatePSBT(network).UpdateFrom(fundedPSBT);
                                        }
                                        else
                                        {
                                            throw new ExternalException(
                                                "VSized could not be calculated for the funded PSBT, channel operation request id: {RequestId}",
                                                channelOperationRequest.Id);
                                        }
                                    }

                                    var finalSignedPSBT = await SignWithInternalWallet(channelOperationRequest, fundedPSBT, derivationStrategyBase, channelfundingTx, network);    
                                    
                                    //Null check
                                    if (finalSignedPSBT is null)
                                    {
                                        _logger.LogError("Could not sign the PSBT for funding channel operation request id: {RequestId}", channelOperationRequest.Id);
                                        throw new Exception("Could not sign the PSBT for funding channel operation request");
                                    }

                                    //Time to finalize the PSBT and broadcast the tx
                                    var finalizedPSBT = finalSignedPSBT.Finalize();

                                    //Sanity check
                                    finalizedPSBT.AssertSanity();

                                    channelfundingTx = finalizedPSBT.ExtractTransaction();

                                    //We check the feerate of the finalized PSBT by checking a minimum and maximum allowed and also a fee-level max check in ratio
                                    var feerate = new FeeRate(finalizedPSBT.GetFee(),
                                        channelfundingTx.GetVirtualSize());

                                    var minFeeRate = Constants.MIN_SAT_PER_VB_RATIO * initialFeeRate;

                                    var maxFeeRate = Constants.MAX_SAT_PER_VB_RATIO * initialFeeRate;

                                    if (feerate.SatoshiPerByte < minFeeRate)
                                    {
                                        _logger.LogError(
                                            "Channel operation request id: {RequestId} finalized PSBT sat/vb: {SatPerVb} is lower than the minimum allowed: {MinSatPerVb}",
                                            channelOperationRequest.Id, feerate.SatoshiPerByte, minFeeRate);
                                        throw new Exception(
                                            "The finalized PSBT sat/vb is lower than the minimum allowed");
                                    }

                                    if (feerate.SatoshiPerByte > maxFeeRate)
                                    {
                                        _logger.LogError(
                                            "Channel operation request id: {RequestId} finalized PSBT sat/vb: {SatPerVb} is higher than the maximum allowed: {MaxSatPerVb}",
                                            channelOperationRequest.Id, feerate.SatoshiPerByte, maxFeeRate);
                                        throw new Exception(
                                            "The finalized PSBT sat/vb is higher than the maximum allowed");
                                    }

                                    //if the fee is too high, we throw an exception
                                    var finalizedTotalIn = finalizedPSBT.Inputs.Sum(x => (long)x.GetCoin()?.Amount);
                                    if (finalizedPSBT.GetFee().Satoshi >=
                                        finalizedTotalIn * Constants.MAX_TX_FEE_RATIO)
                                    {
                                        _logger.LogError(
                                            "Channel operation request id: {RequestId} finalized PSBT fee: {Fee} is higher than the maximum allowed: {MaxFee} sats",
                                            channelOperationRequest.Id, finalizedPSBT.GetFee().Satoshi,
                                            finalizedTotalIn * Constants.MAX_TX_FEE_RATIO);
                                        throw new Exception(
                                            "The finalized PSBT fee is higher than the maximum allowed");
                                    }


                                    _logger.LogInformation(
                                        "Channel operation request id: {RequestId} finalized PSBT sat/vb: {SatPerVb}",
                                        channelOperationRequest.Id, feerate.SatoshiPerByte);

                                    //Just a check of the tx based on the finalizedPSBT
                                    var checkTx = channelfundingTx.Check();

                                    if (checkTx == TransactionCheckResult.Success)
                                    {
                                        //We tell lnd to verify the psbt
                                        _lightningClientService.FundingStateStepVerify(source, finalizedPSBT, pendingChannelId, client);

                                        //Saving the PSBT in the ChannelOperationRequest collection of PSBTs
                                        channelOperationRequest =
                                            await _channelOperationRequestRepository.GetById(channelOperationRequest
                                                .Id) ?? throw new InvalidOperationException();

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
                                                _channelOperationRequestPsbtRepository.AddAsync(
                                                    finalizedChannelOperationRequestPsbt);

                                            if (!finalisedPSBTAdd.Item1)
                                            {
                                                _logger.LogError(
                                                    "Error while saving the finalised PSBT for channel operation request with id: {RequestId}",
                                                    channelOperationRequest.Id);
                                            }
                                        }

                                        _lightningClientService.FundingStateStepFinalize(source, finalizedPSBT, pendingChannelId, client);
                                    }
                                    else
                                    {
                                        _logger.LogError(
                                            "TX Check failed for channel operation request id: {RequestId} reason: {Reason}",
                                            channelOperationRequest.Id, checkTx);
                                        CancelPendingChannel(source, pendingChannelId, client);
                                    }
                                }
                                else
                                {
                                    _logger.LogError(
                                        "Could not parse the PSBT for funding channel operation request id: {RequestId}",
                                        channelOperationRequest.Id);
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

                //TODO: We have to separate the exceptions between the ones that are retriable and the ones that are not
                //TODO: and mark the channel operation request as failed automatically when they are not retriable
                if (e.Message.Contains("remote canceled funding"))
                {
                    // TODO: Make exception message pretty
                    throw new RemoteCanceledFundingException(e.Message);
                }

                if (e.Message.Contains("is not online"))
                {
                    throw new PeerNotOnlineException($"$peer {destination.PubKey} is not online");
                }

                throw;
            }
        }

        private async Task<PSBT?> SignWithInternalWallet(ChannelOperationRequest channelOperationRequest, PSBT fundedPSBT,
            DerivationStrategyBase derivationStrategyBase, Transaction channelfundingTx, Network network)
        {
            PSBT? finalSignedPSBT;
            //We check the way the nodeguard signs, with the nodeguard remote signer or with the embedded signer
            if (Constants.ENABLE_REMOTE_SIGNER)
            {
                finalSignedPSBT = await _remoteSignerService.Sign(fundedPSBT);
                if (finalSignedPSBT == null)
                {
                    const string errorMessage =
                        "The signed PSBT was null, something went wrong while signing with the remote signer";
                    _logger.LogError(errorMessage);
                    throw new Exception(
                        errorMessage);
                }
            }
            else
            {
                finalSignedPSBT = await SignPSBTWithEmbeddedSigner(channelOperationRequest,
                    _nbXplorerService,
                    derivationStrategyBase,
                    channelfundingTx,
                    network,
                    fundedPSBT,
                    _logger);

                if (finalSignedPSBT == null)
                {
                    const string errorMessage =
                        "The signed PSBT was null, something went wrong while signing with the embedded signer";
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

            var addResult =
                await _channelOperationRequestPsbtRepository.AddAsync(
                    signedChannelOperationRequestPsbt);

            if (!addResult.Item1)
            {
                _logger.LogError(
                    "Could not store the signed PSBT for channel operation request id: {RequestId} reason: {Reason}",
                    channelOperationRequest.Id, addResult.Item2);
            }

            return finalSignedPSBT;
        }


        /// <summary>
        /// Validates that all funded PSBT inputs are still present in the confirmed UTXO set.
        /// This prevents LND from creating long-lived pending channels when inputs are already spent.
        /// </summary>
        /// <param name="channelOperationRequest"></param>
        /// <param name="fundedPSBT"></param>
        /// <param name="derivationStrategyBase"></param>
        /// <exception cref="UTXOsNoLongerValidException"></exception>
        private async Task ValidatePSBTInputsAreSpendable(ChannelOperationRequest channelOperationRequest,
            PSBT fundedPSBT, DerivationStrategyBase derivationStrategyBase)
        {
            var utxos = await _nbXplorerService.GetUTXOsAsync(derivationStrategyBase, default);
            utxos.RemoveDuplicateUTXOs();

            var confirmedOutpoints = new HashSet<NBitcoin.OutPoint>(
                utxos.Confirmed.UTXOs.Select(x => x.Outpoint));
            var alreadySpentOutpoints = fundedPSBT.Inputs
                .Select(input => input.PrevOut)
                .Where(outpoint => !confirmedOutpoints.Contains(outpoint))
                .ToList();

            if (alreadySpentOutpoints.Count == 0)
            {
                return;
            }

            var errorMessage =
                $"One or more PSBT inputs are already spent (not confirmed) for channel operation request:{channelOperationRequest.Id}";
            throw new UTXOsNoLongerValidException(errorMessage);
        }

        public async Task OnStatusChannelOpened(ChannelOperationRequest channelOperationRequest, Node source, ChannelPoint channelPoint, string? closeAddress = null)
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            _logger.LogInformation(
                "Channel opened for channel operation request request id: {RequestId}, channel point: {ChannelPoint}",
                channelOperationRequest.Id, channelPoint.ToString());

            channelOperationRequest.Status = ChannelOperationRequestStatus.OnChainConfirmed;
            if (channelOperationRequest.StatusLogs.Count > 0)
            {
                channelOperationRequest.StatusLogs.Add(ChannelStatusLog.Info($"Channel opened successfully 🎉"));
            }

            var (isSuccess, error) = _channelOperationRequestRepository.Update(channelOperationRequest);
            if (!isSuccess)
            {
                _logger.LogWarning("Request is in OnChainConfirmed, but could not update status for request id: {RequestId} reason: {Reason}", channelOperationRequest.Id, error);
            }

            var channel = await CreateChannel(source, channelOperationRequest.DestNode.Id, channelPoint, channelOperationRequest.SatsAmount, closeAddress);

            var channelExists = await _channelRepository.GetByChanId(channel.ChanId);
            if (channelExists == null)
                await context.AddAsync(channel);
            else
            {
                channel.Id = channelExists.Id;
                context.Update(channel);
            }

            var addChannelResult = (await context.SaveChangesAsync()) > 0;

            if (addChannelResult == false)
            {
                _logger.LogError(
                    "Channel for channel operation request id: {RequestId} could not be created, reason: {Reason}",
                    channelOperationRequest.Id,
                    "Could not persist to db");
            }

            channelOperationRequest.ChannelId = channel?.Id;
            channelOperationRequest.DestNode = null;

            var channelUpdate = _channelOperationRequestRepository.Update(channelOperationRequest);

            if (channelUpdate.Item1 == false)
            {
                _logger.LogError(
                    "Could not assign channel id to channel operation request: {RequestId} reason: {Reason}",
                    channelOperationRequest.Id,
                    channelUpdate.Item2);
            }
        }

        public async Task<Channel> CreateChannel(Node source, int destId, ChannelPoint channelPoint, long satsAmount, string? closeAddress = null)
        {
            var fundingTx = LightningHelper.DecodeTxId(channelPoint.FundingTxidBytes);

            //Get the channels to find the channelId, not the temporary one
            var channels = await _lightningClientService.ListChannels(source);

            var currentChannel = channels?.Channels.SingleOrDefault(x => x.ChannelPoint == $"{fundingTx}:{channelPoint.OutputIndex}");

            if (currentChannel == null)
            {
                throw new InvalidOperationException($"Error, channel not found for channel point: {channelPoint}");
            }

            var sourceNodeId = source.Id;
            var destinationNodeId = destId;
            if (!currentChannel.Initiator)
            {
                sourceNodeId = destId;
                destinationNodeId = source.Id;
            }

            var channel = new Channel
            {
                ChanId = currentChannel.ChanId,
                CreationDatetime = DateTimeOffset.Now,
                FundingTx = fundingTx,
                FundingTxOutputIndex = channelPoint.OutputIndex,
                BtcCloseAddress = closeAddress,
                SatsAmount = satsAmount,
                UpdateDatetime = DateTimeOffset.Now,
                Status = Channel.ChannelStatus.Open,
                SourceNodeId = sourceNodeId,
                DestinationNodeId = destinationNodeId,
                CreatedByNodeGuard = true,
                IsPrivate = currentChannel.Private
            };

            return channel;
        }

        public long GetFundingAmount(ChannelOperationRequest channelOperationRequest, PSBT combinedPSBT, decimal initialFeeRate)
        {
            if (!combinedPSBT.TryGetVirtualSize(out var estimatedVsize))
            {
                _logger.LogError("Could not estimate virtual size of the PSBT");
                throw new InvalidOperationException("Could not estimate virtual size of the PSBT");
            }

            var changelessVSize =
                channelOperationRequest.Changeless
                    ? 43
                    : 0; // 8 value + 1 script pub key size + 34 script pub key hash (Segwit output 2-0f-2 multisig)
            var outputVirtualSize = estimatedVsize + changelessVSize; // We add the change output if needed

            var totalFees = new Money(outputVirtualSize * initialFeeRate, MoneyUnit.Satoshi);
            return channelOperationRequest.Changeless
                ? channelOperationRequest.SatsAmount - totalFees
                : channelOperationRequest.SatsAmount;
        }

        public async Task<OpenChannelRequest> CreateOpenChannelRequest(ChannelOperationRequest channelOperationRequest,
            PSBT? combinedPSBT, LightningNode? remoteNodeInfo, long fundingAmount, byte[] pendingChannelId,
            DerivationStrategyBase? derivationStrategyBase)
        {
            if (combinedPSBT == null) throw new ArgumentNullException(nameof(combinedPSBT));
            if (remoteNodeInfo == null) throw new ArgumentNullException(nameof(remoteNodeInfo));
            if (derivationStrategyBase == null) throw new ArgumentNullException(nameof(derivationStrategyBase));

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
                LocalFundingAmount = fundingAmount,
                Private = channelOperationRequest.IsChannelPrivate,
                NodePubkey = ByteString.CopyFrom(Convert.FromHexString(remoteNodeInfo.PubKey)),
            };

            // Check features to see if we need or is allowed to add a close address
            var upfrontShutdownScriptOpt =
                remoteNodeInfo.Features.ContainsKey((uint)FeatureBit.UpfrontShutdownScriptOpt);
            var upfrontShutdownScriptReq =
                remoteNodeInfo.Features.ContainsKey((uint)FeatureBit.UpfrontShutdownScriptReq);
            if (upfrontShutdownScriptOpt && remoteNodeInfo.Features[(uint)FeatureBit.UpfrontShutdownScriptOpt] is
                    { IsKnown: true } ||
                upfrontShutdownScriptReq && remoteNodeInfo.Features[(uint)FeatureBit.UpfrontShutdownScriptReq] is
                    { IsKnown: true })
            {
                var address = await GetCloseAddress(channelOperationRequest, derivationStrategyBase, _nbXplorerService,
                    _logger);
                openChannelRequest.CloseAddress = address.Address.ToString();
                ;
            }

            return openChannelRequest;
        }

        public static PSBT GetCombinedPsbt(ChannelOperationRequest channelOperationRequest, ILogger? _logger = null)
        {
            //PSBT Combine
            var signedPsbts = channelOperationRequest.ChannelOperationRequestPsbts.Where(x =>
                channelOperationRequest.Wallet != null && !x.IsFinalisedPSBT && !x.IsInternalWalletPSBT &&
                (channelOperationRequest.Wallet.IsHotWallet || !x.IsTemplatePSBT));
            var signedPsbts2 = signedPsbts.Select(x => x.PSBT);

            var combinedPSBT = LightningHelper.CombinePSBTs(signedPsbts2, _logger);

            if (combinedPSBT != null) return combinedPSBT;

            var invalidPsbtNullToBeUsedForTheRequest =
                $"Invalid PSBT(null) to be used for the channel op request:{channelOperationRequest.Id}";
            _logger?.LogError(invalidPsbtNullToBeUsedForTheRequest);

            throw new ArgumentException(invalidPsbtNullToBeUsedForTheRequest, nameof(combinedPSBT));
        }

        public static async Task<KeyPathInformation> GetCloseAddress(ChannelOperationRequest channelOperationRequest,
            DerivationStrategyBase derivationStrategyBase, INBXplorerService nbXplorerService, ILogger? _logger = null)
        {
            var closeAddress = await
                nbXplorerService.GetUnusedAsync(derivationStrategyBase, DerivationFeature.Deposit, 0, true, default);

            if (closeAddress != null) return closeAddress;

            var closeAddressNull =
                $"Closing address was null for an operation on wallet:{channelOperationRequest.Wallet.Id}";
            _logger?.LogError(closeAddressNull);

            throw new ArgumentException(closeAddressNull);
        }

        public static DerivationStrategyBase GetDerivationStrategyBase(ChannelOperationRequest channelOperationRequest,
            ILogger? _logger = null)
        {
            //Derivation strategy for the multisig address based on its wallet
            var derivationStrategyBase = channelOperationRequest.Wallet.GetDerivationStrategy();

            if (derivationStrategyBase != null) return derivationStrategyBase;

            var derivationNull = $"Derivation scheme not found for wallet:{channelOperationRequest.Wallet.Id}";

            _logger?.LogError(derivationNull);

            throw new ArgumentException(derivationNull);
        }

        public static void CheckArgumentsAreValid(ChannelOperationRequest channelOperationRequest,
            OperationRequestType requestype, ILogger? _logger = null)
        {
            if (channelOperationRequest == null) throw new ArgumentNullException(nameof(channelOperationRequest));
            
            //If the wallet is watch only, we cannot open a channel as there is no way securely sign the PSBT with SIGHASH_NONE (the internal wallet is not there)
            if (channelOperationRequest.Wallet != null && channelOperationRequest.Wallet.IsWatchOnly)
            {
                var watchOnlyWalletsCannotOpenChannels =
                    $"Watch only wallets cannot open channels with channel request id: {channelOperationRequest.Id} and wallet id: {channelOperationRequest.Wallet.Id}";

                _logger?.LogError(watchOnlyWalletsCannotOpenChannels);

                throw new ArgumentException(watchOnlyWalletsCannotOpenChannels);
            }

            if (channelOperationRequest.RequestType == requestype) return;

            string requestInvalid =
                $"Invalid request. Requested ${channelOperationRequest.RequestType.ToString()} on ${requestype.ToString()} method";

            _logger?.LogError(requestInvalid);
            
           
            throw new ArgumentOutOfRangeException(requestInvalid);
        }

        public static (Node, Node) CheckNodesAreValid(ChannelOperationRequest channelOperationRequest,
            ILogger? _logger = null)
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
        /// Aux method when nodeguard's internal signer is the one in charge of signing the PSBTs
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
        public static async Task<PSBT> SignPSBTWithEmbeddedSigner(
            ChannelOperationRequest channelOperationRequest, INBXplorerService nbXplorerService,
            DerivationStrategyBase derivationStrategyBase, Transaction channelfundingTx, Network network,
            PSBT changeFixedPSBT, ILogger? logger = null)

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


            Dictionary<NBitcoin.OutPoint, NBitcoin.Key> privateKeysForUsedUTXOs;
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

            //We need to SIGHASH_ALL all inputs/outputs to protect the tx from tampering by adding a signature
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

            //We should have added a signature for each input, plus already existing signatures
            var expectedPartialSigs = partialSigsCount + changeFixedPSBT.Inputs.Count;

            if (partialSigsCountAfterSignature == 0 ||
                partialSigsCountAfterSignature != expectedPartialSigs)
            {
                var invalidNoOfPartialSignatures =
                    $"Invalid expected number of partial signatures after signing for the channel operation request:{channelOperationRequest.Id}, expected:{expectedPartialSigs}, actual:{partialSigsCountAfterSignature}";
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
        public void CancelPendingChannel(Node source, byte[] pendingChannelId, Lightning.LightningClient? client)
        {
            try
            {
                if (pendingChannelId != null)
                {
                    _lightningClientService.FundingStateStepCancel(source, pendingChannelId, client);
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
            var derivationStrategy = channelOperationRequest.Wallet?.GetDerivationStrategy();

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
                channelOperationRequest.ChannelOperationRequestPsbts.Where(x => x.IsTemplatePSBT).MaxBy(x => x.Id);

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
                        _logger.LogError("Error while updating withdrawal request: {RequestId}",
                            channelOperationRequest.Id);
                    }

                    return (null, false);
                }
            }

            var previouslyLockedUTXOs =
                await _coinSelectionService.GetLockedUTXOsForRequest(channelOperationRequest,
                    BitcoinRequestType.ChannelOperation);
            var availableUTXOs = previouslyLockedUTXOs.Count > 0
                ? previouslyLockedUTXOs
                : await _coinSelectionService.GetAvailableUTXOsAsync(derivationStrategy);
            var (multisigCoins, selectedUtxOs) =
                await _coinSelectionService.GetTxInputCoins(availableUTXOs, channelOperationRequest,
                    derivationStrategy);

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

                var feeRate = await _nbXplorerService.GetFeesByType(channelOperationRequest.MempoolRecommendedFeesType) ?? channelOperationRequest.FeeRate;
                var feeRateResult = feeRate ??
                                    (await LightningHelper.GetFeeRateResult(network, _nbXplorerService)).FeeRate
                                    .SatoshiPerByte;

                var changeAddress = await _nbXplorerService.GetUnusedAsync(derivationStrategy, DerivationFeature.Change,
                    0, false, default);
                if (changeAddress == null)
                {
                    _logger.LogError("Change address was not found for wallet: {WalletId}",
                        channelOperationRequest.Wallet.Id);
                    return (null, false);
                }

                var builder = txBuilder;
                builder.AddCoins(multisigCoins);

                builder.SetSigningOptions(SigHash.None)
                    .SendAllRemainingToChange()
                    .SetChange(changeAddress.Address)
                    .SendEstimatedFees(new FeeRate(satoshiPerByte: feeRateResult));

                var originalPSBT = builder.BuildPSBT(false);

                //Hack to remove outputs
                var combinedPsbTtx = originalPSBT.GetGlobalTransaction();
                if (channelOperationRequest.Changeless)
                {
                    combinedPsbTtx.Outputs.Clear();
                }

                result.Item1 = combinedPsbTtx.CreatePSBT(network);

                //Hack, see https://github.com/MetacoSA/NBitcoin/issues/1112 for details
                //Hack to make sure that witness and non-witness UTXOs, witness scripts and redeem scripts are added to the PSBT along with SigHash
                foreach (var input in result.Item1.Inputs)
                {
                    input.WitnessUtxo =
                        originalPSBT.Inputs.FirstOrDefault(x => x.PrevOut == input.PrevOut)?.WitnessUtxo;
                    input.NonWitnessUtxo = originalPSBT.Inputs.FirstOrDefault(x => x.PrevOut == input.PrevOut)
                        ?.NonWitnessUtxo;
                    input.WitnessScript = originalPSBT.Inputs.FirstOrDefault(x => x.PrevOut == input.PrevOut)
                        ?.WitnessScript;
                    input.RedeemScript = originalPSBT.Inputs.FirstOrDefault(x => x.PrevOut == input.PrevOut)
                        ?.RedeemScript;

                    input.SighashType = SigHash.None;
                }

                var psbt = LightningHelper.AddDerivationData(channelOperationRequest.Wallet, result.Item1,
                    selectedUtxOs, multisigCoins, _logger);
                result = (psbt, result.Item2);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while generating base PSBT");
            }

            if (previouslyLockedUTXOs.Count == 0)
            {
                await _coinSelectionService.LockUTXOs(selectedUtxOs, channelOperationRequest,
                    BitcoinRequestType.ChannelOperation);
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
                    _logger.LogError("Error while saving template PSBT to channel operation request: {RequestId}",
                        channelOperationRequest.Id);
                }
            }

            return result;
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

                    var node = string.IsNullOrEmpty(channelOperationRequest.SourceNode.ChannelAdminMacaroon)
                        ? channelOperationRequest.DestNode
                        : channelOperationRequest.SourceNode;

                    if (channel != null && node.ChannelAdminMacaroon != null)
                    {
                        //Time to close the channel
                        var closeChannelResult = _lightningClientService.CloseChannel(node, channel, forceClose);

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

                                    channelOperationRequest.Status =
                                        ChannelOperationRequestStatus.OnChainConfirmationPending;
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
                                    var chanCloseClosingTxid =
                                        LightningHelper.DecodeTxId(response.ChanClose.ClosingTxid);
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
                            _logger.LogInformation(
                                "Setting channel with id: {ChannelId} to closed as it no longer exists",
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
                keyPathInformation = await _nbXplorerService.GetUnusedAsync(wallet.GetDerivationStrategy(),
                    derivationFeature, default, false, default);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while getting wallet address for wallet: {WalletId}", wallet.Id);
            }

            var result = keyPathInformation?.Address ?? null;

            return result;
        }

        public async Task<LightningNode?> GetNodeInfo(string pubkey)
        {
            if (string.IsNullOrWhiteSpace(pubkey))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(pubkey));

            LightningNode? result = null;

            var node = (await _nodeRepository.GetAllManagedByNodeGuard()).FirstOrDefault();
            if (node == null)
                node = (await _nodeRepository.GetAllManagedByNodeGuard()).LastOrDefault();

            if (node == null)
            {
                _logger.LogError("No managed node found on the system");
                return result;
            }


            return await _lightningClientService.GetNodeInfo(node, pubkey);
        }

        public async Task<Dictionary<ulong, ChannelState>> GetChannelsState()
        {
            var nodes = await _nodeRepository.GetAllManagedByNodeGuard();

            var result = new Dictionary<ulong, ChannelState>();
            foreach (var node in nodes)
            {
                var listChannelsResponse = await _lightningClientService.ListChannels(node);

                if (listChannelsResponse == null)
                {
                    _logger.LogError("Error while getting channels for node: {NodeId}", node.Id);
                    continue;
                }

                var channels = listChannelsResponse.Channels.ToList();

                foreach (var channel in channels)
                {
                    if (channel == null) continue;
                    // If the source node is not the channel initiator, but the remote node is also managed by NodeGuard
                    // We skip and wait for the other node to report the channel
                    if (nodes.Any((n) => !channel.Initiator && n.PubKey == channel.RemotePubkey)) continue;

                    var htlcsLocal = channel.PendingHtlcs.Where(x => x.Incoming == true).Sum(x => x.Amount);
                    var htlcsRemote = channel.PendingHtlcs.Where(x => x.Incoming == false).Sum(x => x.Amount);

                    var localBalance = channel.LocalBalance + htlcsLocal;
                    var remoteBalance = channel.RemoteBalance + htlcsRemote;

                    result.TryAdd(channel.ChanId, new ChannelState()
                    {
                        LocalBalance = localBalance,
                        RemoteBalance = remoteBalance,
                        Active = channel.Active
                    });
                }
            }

            return result;
        }

        public async Task<ListChannelsResponse?> ListChannels(Node node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));

            try
            {
                return await _lightningClientService.ListChannels(node);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while listing channels for node: {NodeId}", node.Id);
                return null;
            }
        }

        public async Task<ChannelBalanceResponse?> ChannelBalanceAsync(Node node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));

            try
            {
                return await _lightningClientService.ChannelBalanceAsync(node);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while getting channel balance for node: {NodeId}", node.Id);
                return null;
            }
        }

        public async Task<RouteFeeResponse?> EstimateRouteFee(string destPubkey, long amountSat, string? paymentRequest = null, uint timeout = 30)
        {
            if (string.IsNullOrWhiteSpace(destPubkey))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(destPubkey));

            if (amountSat <= 0)
                throw new ArgumentException("Amount must be greater than zero.", nameof(amountSat));

            var node = (await _nodeRepository.GetAllManagedByNodeGuard()).FirstOrDefault();
            if (node == null)
            {
                _logger.LogError("No managed node found on the system");
                return null;
            }

            try
            {
                var routeFeeRequest = new RouteFeeRequest
                {
                    AmtSat = amountSat,
                    Timeout = timeout
                };

                if (!string.IsNullOrWhiteSpace(paymentRequest))
                {
                    routeFeeRequest.PaymentRequest = paymentRequest;
                }
                else
                {
                    routeFeeRequest.Dest = ByteString.CopyFrom(Convert.FromHexString(destPubkey));
                }

                return await _lightningRouterService.EstimateRouteFee(node, routeFeeRequest);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while estimating route fee for destination: {DestPubkey}", destPubkey);
                return null;
            }
        }
    }
}