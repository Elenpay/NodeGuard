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

using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Services;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using NBXplorer.DerivationStrategy;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Subtask of ChannelAcceptorJob
/// </summary>
[DisallowConcurrentExecution]
public class ProcessNodeChannelAcceptorJob : IJob
{
    private readonly INodeRepository _nodeRepository;
    private readonly IWalletRepository _walletRepository;
    private readonly INBXplorerService _nBXplorerService;
    private readonly ILogger<ProcessNodeChannelAcceptorJob> _logger;
    private readonly ILightningClientService _lightningClientService;

    public ProcessNodeChannelAcceptorJob(ILogger<ProcessNodeChannelAcceptorJob> logger,
        INodeRepository nodeRepository,
        IWalletRepository walletRepository,
        INBXplorerService nBXplorerService,
        ILightningClientService lightningClientService
        )
    {
        _nodeRepository = nodeRepository;
        _walletRepository = walletRepository;
        _nBXplorerService = nBXplorerService;
        _logger = logger;
        _lightningClientService = lightningClientService;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var managedNodeId = context.JobDetail.JobDataMap.GetIntValueFromString("managedNodeId");

        if (managedNodeId <= 0) throw new JobExecutionException(new Exception("Invalid managedNodeId"), false);
        #region Local Functions

        async Task AcceptChannelOpeningRequest(
            AsyncDuplexStreamingCall<ChannelAcceptResponse, ChannelAcceptRequest> resultAcceptor,
            Node node, ChannelAcceptRequest response)
        {
            var openerNodePubKey = Convert.ToHexString(response.NodePubkey.ToByteArray()).ToLower();
            var capacity = response.FundingAmt;
            _logger.LogInformation(
                "Accepting channel opening request from external node: {PubKey} to managed node: {NodeName}, with capacity: {Capacity} and no returning address",
                openerNodePubKey, node.Name, capacity);
            await resultAcceptor.RequestStream.WriteAsync(new ChannelAcceptResponse
            {
                Accept = true,
                PendingChanId = response.PendingChanId
            });
        }

        async Task AcceptChannelOpeningRequestWithUpfrontShutdown(INBXplorerService nbXplorerService,
            Wallet returningMultisigWallet,
            AsyncDuplexStreamingCall<ChannelAcceptResponse, ChannelAcceptRequest> asyncDuplexStreamingCall, Node node, ChannelAcceptRequest? response)
        {
            if (response != null)
            {
                var openerNodePubKey = Convert.ToHexString(response.NodePubkey.ToByteArray()).ToLower();
                var capacity = response.FundingAmt;

                var address = await  _nBXplorerService.GetUnusedAsync(returningMultisigWallet.GetDerivationStrategy(),
                    DerivationFeature.Deposit, 0, false, default); //Reserve is false to avoid DoS

                if (address != null)
                {
                    _logger.LogInformation(
                        "Accepting channel opening request from external node: {PubKey} to managed node: {NodeName}, with capacity: {Capacity} and returning address: {Address}",
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
                    _logger.LogError("Could not find an address for wallet: {WalletId} for a returning address",
                        returningMultisigWallet.Id);
                    //Just accept..
                    await AcceptChannelOpeningRequest(asyncDuplexStreamingCall, node, response);
                }
            }
        }

        #endregion Local Functions

        var node = await _nodeRepository.GetById(managedNodeId);

        if (node == null)
        {
            _logger.LogInformation("The node: {@Node} is no longer ready to be supported quartz jobs", node);
            return;
        }

        try
        {
            _logger.LogInformation("Starting {JobName} on node: {NodeName}", nameof(ProcessNodeChannelAcceptorJob), node.Name);

            var client = _lightningClientService.GetLightningClient(node.Endpoint);

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
                    node = await _nodeRepository.GetById(managedNodeId);
                    if (node == null)
                    {
                        _logger.LogInformation("The node: {NodeId} is no longer ready to be supported quartz jobs", managedNodeId);
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

                            //Lets find the node's assigned multisig wallet
                            var returningMultisigWallet = node.ReturningFundsWallet;

                            if (returningMultisigWallet != null)
                            {
                                await AcceptChannelOpeningRequestWithUpfrontShutdown(_nBXplorerService,
                                    returningMultisigWallet, resultAcceptor, node, response);
                            }
                            else
                            {
                                //The node does not have a wallet assigned, lets pick the oldest.

                                var wallet = (await _walletRepository.GetAvailableWallets()).FirstOrDefault();

                                if (wallet != null)
                                {
                                    //Wallet found
                                    await AcceptChannelOpeningRequestWithUpfrontShutdown(_nBXplorerService,
                                        wallet, resultAcceptor, node, response);

                                    node.ReturningFundsWalletId = wallet.Id;

                                    //We assign the node's returning wallet
                                    var updateResult = _nodeRepository.Update(node);

                                    if (updateResult.Item1 == false)
                                    {
                                        _logger.LogError(
                                            "Error while adding returning node wallet with id: {WalletId} to node: {NodeId}",
                                            wallet.Id, node.Id);
                                    }
                                }
                                else
                                {
                                    _logger.LogError("No wallets available in the system for {JobName}",
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
                _logger.LogError("Invalid macaroon for node: {NodeName}", node.Name);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(ProcessNodeChannelAcceptorJob));
            
            //Sleep to avoid massive requests
            await Task.Delay(5000);

            throw new JobExecutionException(e, true);

        }
    }
}
