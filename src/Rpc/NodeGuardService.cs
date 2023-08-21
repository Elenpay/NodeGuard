using AutoMapper;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Jobs;
using NodeGuard.Services;
using Grpc.Core;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Nodeguard;
using Quartz;
using LiquidityRule = Nodeguard.LiquidityRule;
using Node = Nodeguard.Node;
using Wallet = NodeGuard.Data.Models.Wallet;

namespace NodeGuard.Rpc;

public interface INodeGuardService
{
    Task<GetLiquidityRulesResponse> GetLiquidityRules(GetLiquidityRulesRequest request,
        ServerCallContext context);

    Task<GetNewWalletAddressResponse> GetNewWalletAddress(GetNewWalletAddressRequest request,
        ServerCallContext context);

    Task<RequestWithdrawalResponse> RequestWithdrawal(RequestWithdrawalRequest request, ServerCallContext context);

    Task<GetAvailableWalletsResponse>
        GetAvailableWallets(GetAvailableWalletsRequest request, ServerCallContext context);

    Task<GetNodesResponse> GetNodes(GetNodesRequest request, ServerCallContext context);

    Task<AddNodeResponse> AddNode(AddNodeRequest request, ServerCallContext context);

    Task<OpenChannelResponse> OpenChannel(OpenChannelRequest request, ServerCallContext context);

    Task<CloseChannelResponse> CloseChannel(CloseChannelRequest request, ServerCallContext context);
}

/// <summary>
/// gRPC Server implementation of the NodeGuard API
/// </summary>
public class NodeGuardService : Nodeguard.NodeGuardService.NodeGuardServiceBase, INodeGuardService
{
    private readonly ILogger<NodeGuardService> _logger;
    private readonly ILiquidityRuleRepository _liquidityRuleRepository;
    private readonly IWalletRepository _walletRepository;
    private readonly IMapper _mapper;
    private readonly IWalletWithdrawalRequestRepository _walletWithdrawalRequestRepository;
    private readonly IBitcoinService _bitcoinService;
    private readonly INBXplorerService _nbXplorerService;
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly INodeRepository _nodeRepository;
    private readonly IChannelOperationRequestRepository _channelOperationRequestRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly ICoinSelectionService _coinSelectionService;
    private readonly IScheduler _scheduler;
    private readonly ILightningService _lightningService;

    public NodeGuardService(ILogger<NodeGuardService> logger,
        ILiquidityRuleRepository liquidityRuleRepository,
        IWalletRepository walletRepository,
        IMapper mapper,
        IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository,
        IBitcoinService bitcoinService,
        INBXplorerService nbXplorerService,
        ISchedulerFactory schedulerFactory,
        INodeRepository nodeRepository,
        IChannelOperationRequestRepository channelOperationRequestRepository,
        IChannelRepository channelRepository,
        ICoinSelectionService coinSelectionService,
        ILightningService lightningService
    )
    {
        _logger = logger;
        _liquidityRuleRepository = liquidityRuleRepository;
        _walletRepository = walletRepository;
        _mapper = mapper;
        _walletWithdrawalRequestRepository = walletWithdrawalRequestRepository;
        _bitcoinService = bitcoinService;
        _nbXplorerService = nbXplorerService;
        _schedulerFactory = schedulerFactory;
        _nodeRepository = nodeRepository;
        _channelOperationRequestRepository = channelOperationRequestRepository;
        _channelRepository = channelRepository;
        _coinSelectionService = coinSelectionService;
        _lightningService = lightningService;
        _scheduler = Task.Run(() => _schedulerFactory.GetScheduler()).Result;
    }

    public override async Task<GetLiquidityRulesResponse> GetLiquidityRules(GetLiquidityRulesRequest request,
        ServerCallContext context)
    {
        //Check the pubkey is set
        if (string.IsNullOrWhiteSpace(request.NodePubkey))
        {
            _logger.LogError("NodePubkey is required");
            throw new RpcException(new Status(StatusCode.InvalidArgument, "NodePubkey is required"));
        }

        var result = new GetLiquidityRulesResponse();
        try
        {
            var liquidityRules = await _liquidityRuleRepository.GetByNodePubKey(request.NodePubkey);
            result = new GetLiquidityRulesResponse()
            {
                LiquidityRules = {liquidityRules.Select(x => _mapper.Map<LiquidityRule>(x)).ToList()}
            };
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error getting liquidity rules for node {nodePubkey}", request.NodePubkey);
            throw new RpcException(new Status(StatusCode.Internal, e.Message));
        }

        return result;
    }

    public override async Task<GetNewWalletAddressResponse> GetNewWalletAddress(GetNewWalletAddressRequest request,
        ServerCallContext context)
    {
        var wallet = await _walletRepository.GetById(request.WalletId);
        if (wallet == null)
        {
            _logger.LogError("Wallet with id {walletId} not found", request.WalletId);
            throw new RpcException(new Status(StatusCode.NotFound, "Wallet not found"));
        }

        var btcAddress = await _nbXplorerService.GetUnusedAsync(wallet.GetDerivationStrategy(),
            DerivationFeature.Deposit,
            0,
            false, default);

        if (btcAddress == null)
        {
            _logger.LogError("Error getting new address for wallet with id {walletId}", request.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, "Error getting new address for wallet"));
        }

        var getNewWalletAddressResponse = new GetNewWalletAddressResponse()
        {
            Address = btcAddress.Address.ToString()
        };

        return getNewWalletAddressResponse;
    }

    public override async Task<RequestWithdrawalResponse> RequestWithdrawal(RequestWithdrawalRequest request,
        ServerCallContext context)
    {
        try
        {
            //We get the wallet
            var wallet = await _walletRepository.GetById(request.WalletId);
            if (wallet == null)
            {
                _logger.LogError("Wallet with id {walletId} not found", request.WalletId);
                throw new RpcException(new Status(StatusCode.NotFound, "Wallet not found"));
            }

            //Create withdrawal request
            var amount = new Money(request.Amount, MoneyUnit.Satoshi).ToUnit(MoneyUnit.BTC);

            var withdrawalRequest = new WalletWithdrawalRequest()
            {
                WalletId = request.WalletId,
                Amount = amount,
                DestinationAddress = request.Address,
                Description = request.Description,
                Status = wallet.IsHotWallet
                    ? WalletWithdrawalRequestStatus.PSBTSignaturesPending
                    : WalletWithdrawalRequestStatus.Pending,
                RequestMetadata = request.RequestMetadata
            };

            //Save withdrawal request
            var withdrawalSaved = await _walletWithdrawalRequestRepository.AddAsync(withdrawalRequest);

            if (!withdrawalSaved.Item1)
            {
                _logger.LogError("Error saving withdrawal request for wallet with id {walletId}", request.WalletId);
                throw new RpcException(new Status(StatusCode.Internal, "Error saving withdrawal request for wallet"));
            }

            //Update to refresh from db
            withdrawalRequest = await _walletWithdrawalRequestRepository.GetById(withdrawalRequest.Id);

            if (!withdrawalSaved.Item1)
            {
                _logger.LogError("Error saving withdrawal request for wallet with id {walletId}", request.WalletId);
                throw new RpcException(new Status(StatusCode.Internal, "Error saving withdrawal request for wallet"));
            }

            //Template PSBT generation with SIGHASH_ALL
            var psbt = await _bitcoinService.GenerateTemplatePSBT(withdrawalRequest);

            //If the wallet is hot, we send the withdrawal request to the node
            if (wallet.IsHotWallet)
            {
                var map = new JobDataMap();
                map.Put("withdrawalRequestId", withdrawalRequest.Id);
                var retryList = RetriableJob.ParseRetryListFromString(Constants.JOB_RETRY_INTERVAL_LIST_IN_MINUTES);
                var job = RetriableJob.Create<PerformWithdrawalJob>(map, withdrawalRequest.Id.ToString(), retryList);
                await _scheduler.ScheduleJob(job.Job, job.Trigger);
            }

            var response = new RequestWithdrawalResponse
            {
                IsHotWallet = wallet.IsHotWallet,
                Txid = psbt.GetGlobalTransaction().GetHash().ToString()
            };

            return response;
        }
        catch (NoUTXOsAvailableException)
        {
            _logger.LogError("No available UTXOs for wallet with id {walletId}", request.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, "No available UTXOs for wallet"));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error requesting withdrawal for wallet with id {walletId}", request.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, "Error requesting withdrawal for wallet"));
        }
    }

    public override async Task<GetAvailableWalletsResponse> GetAvailableWallets(GetAvailableWalletsRequest request,
        ServerCallContext context)
    {
        try
        {
            List<Wallet> wallets;
            var ids = request.Id?.ToList();
            if (request.WalletType != 0 && ids?.Count > 0)
            {
                throw new InvalidOperationException("You can't select wallets by type and id at the same time");
            }

            if (request.WalletType != 0)
            {
                wallets = await _walletRepository.GetAvailableByType(request.WalletType);
            }
            else if (ids?.Count > 0)
            {
                wallets = await _walletRepository.GetAvailableByIds(ids);
            }
            else
            {
                wallets = await _walletRepository.GetAvailableWallets();
            }

            var result = new GetAvailableWalletsResponse()
            {
                Wallets =
                {
                    wallets.Select(w => new Nodeguard.Wallet()
                    {
                        Id = w.Id,
                        Name = w.Name,
                        IsHotWallet = w.IsHotWallet,
                        AccountKeySettings =
                        {
                            w.Keys.Select(k => new Nodeguard.AccountKeySettings()
                            {
                                Xpub = k.XPUB,
                            })
                        }
                    }).ToList()
                }
            };
            return result;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error getting available wallets");
            throw new RpcException(new Status(StatusCode.Internal, e.Message));
        }
    }

    public override async Task<AddNodeResponse> AddNode(AddNodeRequest request, ServerCallContext context)
    {
        var node = new NodeGuard.Data.Models.Node
        {
            PubKey = request.PubKey,
            Name = request.Name,
            Description = request.Description,
            ChannelAdminMacaroon = request.ChannelAdminMacaroon,
            Endpoint = request.Endpoint,
            AutosweepEnabled = request.AutosweepEnabled,
            ReturningFundsWalletId = request.ReturningFundsWalletId,
        };

        try
        {
            var result = await _nodeRepository.AddAsync(node);

            if (result.Item1)
            {
                return new AddNodeResponse();
            }

            _logger?.LogError("Error adding node, error: {error}", result.Item2);
            throw new RpcException(new Status(StatusCode.Internal, "Error adding node"));
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error getting adding node through gRPC");
            throw new RpcException(new Status(StatusCode.Internal, e.Message));
        }
    }

    public override async Task<GetNodesResponse> GetNodes(GetNodesRequest request, ServerCallContext context)
    {
        try
        {
            var nodes = new List<Data.Models.Node>();
            if (request.IncludeUnmanaged)
            {
                nodes = await _nodeRepository.GetAll();
            }
            else
            {
                nodes = await _nodeRepository.GetAllManagedByNodeGuard();
            }

            var mappedNodes = nodes.Select(x => _mapper.Map<Nodeguard.Node>(x)).ToList();

            var response = new GetNodesResponse()
            {
                Nodes = {mappedNodes}
            };
            return response;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error getting nodes through gRPC");

            throw new RpcException(new Status(StatusCode.Internal, e.Message));
        }
    }

    public override async Task<OpenChannelResponse> OpenChannel(OpenChannelRequest request, ServerCallContext context)
    {
        var sourceNode = await _nodeRepository.GetByPubkey(request.SourcePubKey);
        if (sourceNode == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Source node not found"));
        }

        var destNode = await _nodeRepository.GetByPubkey(request.DestinationPubKey);
        if (destNode == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Destination node not found"));
        }

        var wallet = await _walletRepository.GetById(request.WalletId);
        if (wallet == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Wallet not found"));
        }

        if (request.MempoolFeeRate.Equals("") || request.MempoolFeeRate == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Mempool fee rate is required"));
        }

        if (request.Changeless && request.UtxosOutpoints.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Changeless channel open requires utxos"));
        }

        try
        {
            var outpoints = new List<OutPoint>();
            var utxos = new List<UTXO>();

            if (request.Changeless)
            {
                foreach (var outpoint in request.UtxosOutpoints)
                {
                    outpoints.Add(OutPoint.Parse(outpoint));
                }

                // Search the utxos and lock them
                utxos = await _coinSelectionService.GetUTXOsByOutpointAsync(wallet.GetDerivationStrategy(), outpoints);
            }

            // Get the fee type of the request
            MempoolRecommendedFeesTypes feeType;

            switch (request.MempoolFeeRate)
            {
                case "EconomyFee":
                    feeType = MempoolRecommendedFeesTypes.EconomyFee;
                    break;
                case "FastestFee":
                    feeType = MempoolRecommendedFeesTypes.FastestFee;
                    break;
                case "HourFee":
                    feeType = MempoolRecommendedFeesTypes.HourFee;
                    break;
                case "HalfHourFee":
                    feeType = MempoolRecommendedFeesTypes.HalfHourFee;
                    break;
                default:
                    feeType = MempoolRecommendedFeesTypes.CustomFee;
                    break;
            }

            if (feeType == MempoolRecommendedFeesTypes.CustomFee && request.CustomFeeRate == null)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Custom fee rate is required"));
            }

            var channelOperationRequest = new ChannelOperationRequest
            {
                SatsAmount = request.SatsAmount,
                Description = $"Channel open from {sourceNode.PubKey} to {destNode.PubKey} (API)",
                AmountCryptoUnit = MoneyUnit.Satoshi,
                Status = ChannelOperationRequestStatus.Pending,
                RequestType = OperationRequestType.Open,
                WalletId = request.WalletId,
                SourceNodeId = sourceNode.Id,
                DestNodeId = destNode.Id,
                /*UserId = null, //TODO User & Auth
                User = null,*/
                IsChannelPrivate = request.Private,
                Changeless = request.Changeless,
                MempoolRecommendedFeesTypes = feeType,
                FeeRate = feeType == MempoolRecommendedFeesTypes.CustomFee ? request.CustomFeeRate : null,
            };

            //Persist request
            var result = await _channelOperationRequestRepository.AddAsync(channelOperationRequest);
            if (!result.Item1)
            {
                _logger?.LogError("Error adding channel operation request, error: {error}", result.Item2);
                throw new RpcException(new Status(StatusCode.Internal, "Error adding channel operation request"));
            }

            if (request.Changeless)
            {
                // Lock the utxos
                await _coinSelectionService.LockUTXOs(utxos, channelOperationRequest,
                    BitcoinRequestType.ChannelOperation);
            }

            var (templatePsbt, noUtxosAvailable) = (await _lightningService.GenerateTemplatePSBT(channelOperationRequest));
            if (templatePsbt == null)
            {
                channelOperationRequest.Status = ChannelOperationRequestStatus.Failed;
                _channelOperationRequestRepository.Update(channelOperationRequest);
                if (noUtxosAvailable)
                {
                    _logger?.LogError("No UTXOs available for opening the channel");
                    throw new RpcException(new Status(StatusCode.ResourceExhausted,
                        "No UTXOs available for opening the channel"));
                }
                else
                {
                    _logger?.LogError("Error generating template PSBT");
                    throw new RpcException(new Status(StatusCode.Internal, "Error generating template PSBT"));
                }
            }

            //Fire Open Channel Job
            var scheduler = await _schedulerFactory.GetScheduler();

            var map = new JobDataMap();
            map.Put("openRequestId", channelOperationRequest.Id);

            var retryList = RetriableJob.ParseRetryListFromString(Constants.JOB_RETRY_INTERVAL_LIST_IN_MINUTES);
            var job = RetriableJob.Create<ChannelOpenJob>(map, channelOperationRequest.Id.ToString(), retryList);
            await scheduler.ScheduleJob(job.Job, job.Trigger);

            channelOperationRequest.JobId = job.Job.Key.ToString();

            var jobUpdateResult = _channelOperationRequestRepository.Update(channelOperationRequest);

            if (!jobUpdateResult.Item1)
            {
                _logger?.LogError("Error updating channel operation request, error: {error}", jobUpdateResult.Item2);
                throw new RpcException(new Status(StatusCode.Internal, "Error updating channel operation request"));
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error opening channel through gRPC");
            throw new RpcException(new Status(StatusCode.Internal, e.Message));
        }


        return new OpenChannelResponse();
    }

    public override async Task<CloseChannelResponse> CloseChannel(CloseChannelRequest request, ServerCallContext context)
    {
        //Get channel by its chan_id (id of the ln implementation)
        var channel = await _channelRepository.GetByChanId(request.ChannelId);

        if (channel == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Channel not found"));
        }

        try
        {
            //Create channel operation request

            var channelOperationRequest = new ChannelOperationRequest
            {
                Description = "Channel close (API)",
                Status = ChannelOperationRequestStatus.Pending,
                RequestType = OperationRequestType.Close,
                SourceNodeId = channel.SourceNodeId,
                DestNodeId = channel.DestinationNodeId,
                ChannelId = channel.Id,
                /*UserId = null, //TODO User & Auth
            */
            };

            //Persist request

            var result = await _channelOperationRequestRepository.AddAsync(channelOperationRequest);

            if (!result.Item1)
            {
                _logger?.LogError("Error adding channel operation request, error: {error}", result.Item2);
                throw new RpcException(new Status(StatusCode.Internal, "Error adding channel operation request"));
            }

            //Fire Close Channel Job
            var scheduler = await _schedulerFactory.GetScheduler();

            var map = new JobDataMap();
            map.Put("closeRequestId", channelOperationRequest.Id);
            map.Put("forceClose", request.Force);

            var retryList = RetriableJob.ParseRetryListFromString(Constants.JOB_RETRY_INTERVAL_LIST_IN_MINUTES);
            var job = RetriableJob.Create<ChannelCloseJob>(map, channelOperationRequest.Id.ToString(), retryList);
            await scheduler.ScheduleJob(job.Job, job.Trigger);

            channelOperationRequest.JobId = job.Job.Key.ToString();

            var jobUpdateResult = _channelOperationRequestRepository.Update(channelOperationRequest);

            if (!jobUpdateResult.Item1)
            {
                _logger?.LogError("Error updating channel operation request, error: {error}", jobUpdateResult.Item2);
                throw new RpcException(new Status(StatusCode.Internal, "Error updating channel operation request"));
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error closing channel through gRPC");
            throw new RpcException(new Status(StatusCode.Internal, e.Message));
        }

        return new CloseChannelResponse();
    }
}