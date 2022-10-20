using FundsManager.Data.Models;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using NBXplorer;
using NBXplorer.DerivationStrategy;
using Quartz;

namespace FundsManager.Jobs;

/// <summary>
/// Subtask of ChannelAcceptorJob
/// </summary>
[DisallowConcurrentExecution]
public class ProcessNodeChannelAcceptorJob : IJob
{
    private readonly INodeRepository _nodeRepository;
    private readonly IWalletRepository _walletRepository;
    private readonly ILogger<ProcessNodeChannelAcceptorJob> _logger;

    public ProcessNodeChannelAcceptorJob(ILogger<ProcessNodeChannelAcceptorJob> logger,
        INodeRepository nodeRepository,
        IWalletRepository walletRepository)
    {
        _nodeRepository = nodeRepository;
        _walletRepository = walletRepository;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var managedNodeId = context.JobDetail.JobDataMap.GetIntValueFromString("managedNodeId");

        if (managedNodeId <= 0) throw new JobExecutionException(new Exception("Invalid managedNodeId"), false);

        var loggerFactory = GRPCLoggerFactoryHelper.LoggerFactory();

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

        var node = await _nodeRepository.GetById(managedNodeId);

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
                    node = await _nodeRepository.GetById(managedNodeId);
                    if (node == null)
                    {
                        _logger.LogInformation("The node:{} is no longer ready to be supported hangfire jobs", managedNodeId);
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
                            var (_, nbxplorerClient) = LightningHelper.GenerateNetwork();

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
            throw new JobExecutionException(e, true);
        }
    }
}