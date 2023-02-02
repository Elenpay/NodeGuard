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

﻿using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using Quartz;
 using Unmockable;

namespace FundsManager.Jobs;

[DisallowConcurrentExecution]
public class SweepNodeWalletsJob : IJob
{
    private readonly ILogger<SweepNodeWalletsJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly IWalletRepository _walletRepository;

    public SweepNodeWalletsJob(ILogger<SweepNodeWalletsJob> logger,
        INodeRepository _nodeRepository,
        IWalletRepository walletRepository)
    {
        _logger = logger;
        this._nodeRepository = _nodeRepository;
        _walletRepository = walletRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var managedNodeId = context.JobDetail.JobDataMap.GetIntValueFromString("managedNodeId");
        if (managedNodeId <= 0) throw new JobExecutionException(new Exception("Invalid managedNodeId"), false);

        _logger.LogInformation("Starting {JobName}... on node: {NodeId}", nameof(SweepNodeWalletsJob), managedNodeId);

        var requiredAnchorChannelClosingAmount = Constants.ANCHOR_CLOSINGS_MINIMUM_SATS;
            

        var (_, nbxplorerClient) = LightningHelper.GenerateNetwork();

        #region Local functions

        async Task SweepFunds(Node node, Wallet wallet, Lightning.LightningClient lightningClient,
            IUnmockable<ExplorerClient> explorerClient, List<Utxo> utxos)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            if (wallet == null) throw new ArgumentNullException(nameof(wallet));
            if (lightningClient == null) throw new ArgumentNullException(nameof(lightningClient));
            if (explorerClient == null) throw new ArgumentNullException(nameof(explorerClient));

            var returningAddress = await explorerClient.Execute(x => x.GetUnusedAsync(wallet.GetDerivationStrategy(),
                DerivationFeature.Deposit,
                0,
                false, //Reserve is false since this is a cron job and we wan't to avoid massive reserves 
                default));

            if (node.ChannelAdminMacaroon != null)
            {
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
                    var sweepedFundsAmount = (long)((totalSatsAvailable - requiredAnchorChannelClosingAmount) * 0.9); // We should let requiredAnchorChannelClosingAmount sats as a UTXO in  in the hot wallet for channel closings
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

                    _logger.LogInformation("Utxos swept out for nodeId: {NodeId} on txid: {TxId} with returnAddress: {Address}",
                        node.Id,
                        sendManyResponse.Txid,
                        returningAddress.Address);

                    //TODO We need to store the txid somewhere to monitor it..
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
                                totalSatsAvailable <= requiredAnchorChannelClosingAmount
                                    ?
                                    "Total sats available is less than the required to have for channel closing amounts, ignoring tx" : string.Empty;

                    _logger.LogError("Error while funding sweep transaction reason: {Reason}", reason);
                }
            }
        }

        #endregion Local functions

        var node = await _nodeRepository.GetById(managedNodeId);
        if (node == null)
        {
            _logger.LogError("{JobName} failed on node with id: {NodeId}, reason: node not found",
                nameof(SweepNodeWalletsJob),
                managedNodeId);
            throw new ArgumentException("node not found", nameof(node));
        }

        var loggerFactory = GRPCLoggerFactoryHelper.LoggerFactory();

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
                                "Error while adding returning node wallet with id: {WalletId} to node: {NodeName}",
                                wallet.Id, node.Name);
                        }
                    }
                    else
                    {
                        //Wallet not found
                        _logger.LogError("No wallets available in the system to perform the {JobName} on node: {NodeName}",
                            nameof(SweepAllNodesWalletsJob),
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
                _logger.LogInformation("Insufficient UTXOs to fund a sweep tx on node: {NodeName}", node.Name);
            }
            else if (e.Message.Contains("insufficient funds available to construct transaction"))
            {
                _logger.LogInformation("Insufficient funds to fund a sweep tx on node: {NodeName}", node.Name);
            }
            else
            {
                _logger.LogError(e, "Error on {JobName}", nameof(SweepNodeWalletsJob));
                throw new JobExecutionException(e);
            }
        }
        _logger.LogInformation("{JobName} ended on node: {NodeName}", nameof(SweepNodeWalletsJob), node.Name);
    }
}