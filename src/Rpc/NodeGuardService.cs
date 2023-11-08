using AutoMapper;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Jobs;
using NodeGuard.Services;
using Grpc.Core;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NBitcoin;
using NBitcoin.RPC;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Nodeguard;
using Quartz;
using LiquidityRule = NodeGuard.Data.Models.LiquidityRule;
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

    Task<GetChannelOperationRequestResponse> GetChannelOperationRequest(GetChannelOperationRequestRequest request, ServerCallContext context);

    Task<AddLiquidityRuleResponse> AddLiquidityRule(AddLiquidityRuleRequest request, ServerCallContext context);

    Task<GetAvailableUtxosResponse> GetAvailableUtxos(GetAvailableUtxosRequest request, ServerCallContext context);

    Task<GetWithdrawalsRequestStatusResponse> GetWithdrawalsRequestStatus(GetWithdrawalsRequestStatusRequest request, ServerCallContext context);

    Task<GetChannelResponse> GetChannel(GetChannelRequest request, ServerCallContext context);
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
    private readonly IFMUTXORepository _fmutxoRepository;

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
        ILightningService lightningService,
        IFMUTXORepository fmutxoRepository
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
        _fmutxoRepository = fmutxoRepository;
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
                LiquidityRules = {liquidityRules.Select(x => _mapper.Map<Nodeguard.LiquidityRule>(x)).ToList()}
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
        WalletWithdrawalRequest? withdrawalRequest = null;
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
                amount = utxos.Sum(u => ((Money)u.Value).ToUnit(MoneyUnit.BTC));
            }

            withdrawalRequest = new WalletWithdrawalRequest()
            {
                WalletId = request.WalletId,
                Amount = amount,
                DestinationAddress = request.Address,
                Description = request.Description,
                Status = wallet.IsHotWallet
                    ? WalletWithdrawalRequestStatus.PSBTSignaturesPending
                    : WalletWithdrawalRequestStatus.Pending,
                RequestMetadata = request.RequestMetadata,
                Changeless = request.Changeless
            };

            //Save withdrawal request
            var withdrawalSaved = await _walletWithdrawalRequestRepository.AddAsync(withdrawalRequest);

            if (!withdrawalSaved.Item1)
            {
                _logger.LogError("Error saving withdrawal request for wallet with id {walletId}", request.WalletId);
                throw new RpcException(new Status(StatusCode.Internal, "Error saving withdrawal request for wallet"));
            }

            if (request.Changeless)
            {
                // Lock the utxos
                await _coinSelectionService.LockUTXOs(utxos, withdrawalRequest,
                    BitcoinRequestType.WalletWithdrawal);
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
                Txid = psbt.GetGlobalTransaction().GetHash().ToString(),
                RequestId = withdrawalRequest.Id
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
            if (withdrawalRequest != null)
            {
                withdrawalRequest.Status = WalletWithdrawalRequestStatus.Cancelled;
                var (success, error) = _walletWithdrawalRequestRepository.Update(withdrawalRequest);
                if (!success)
                {
                    _logger.LogError(e, "Error updating status of withdrawal request {RequestId} for wallet {WalletId}", withdrawalRequest.Id, request.WalletId);
                }
            }
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
                        },
                        Threshold = w.MofN
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

        if (request.MempoolFeeRate == FEES_TYPE.CustomFee && request.CustomFeeRate == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Mempool fee rate configuration is not valid"));
        }

        if (request.Changeless && request.UtxosOutpoints.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Changeless channel open requires utxos"));
        }

        int requestId;

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
            var feeType = request.MempoolFeeRate switch
            {
                FEES_TYPE.EconomyFee => MempoolRecommendedFeesTypes.EconomyFee,
                FEES_TYPE.FastestFee => MempoolRecommendedFeesTypes.FastestFee,
                FEES_TYPE.HourFee => MempoolRecommendedFeesTypes.HourFee,
                FEES_TYPE.HalfHourFee => MempoolRecommendedFeesTypes.HalfHourFee,
                FEES_TYPE.CustomFee => MempoolRecommendedFeesTypes.CustomFee,
                _ => throw new ArgumentOutOfRangeException(nameof(request.MempoolFeeRate), request.MempoolFeeRate, "Unknown status")
            };

            if (feeType == MempoolRecommendedFeesTypes.CustomFee && !request.HasCustomFeeRate)
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

            requestId = channelOperationRequest.Id;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error opening channel through gRPC");
            throw new RpcException(new Status(StatusCode.Internal, e.Message));
        }


        return new OpenChannelResponse()
        {
            ChannelOperationRequestId = requestId
        };
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

    public override async Task<GetChannelOperationRequestResponse> GetChannelOperationRequest(GetChannelOperationRequestRequest request, ServerCallContext context)
    {
        var channelOperationRequest = await _channelOperationRequestRepository.GetById(request.ChannelOperationRequestId);

        if (channelOperationRequest == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Channel operation request not found"));
        }

        var status = channelOperationRequest.Status switch
        {
            ChannelOperationRequestStatus.Approved => CHANNEL_OPERATION_STATUS.Approved,
            ChannelOperationRequestStatus.Cancelled => CHANNEL_OPERATION_STATUS.Cancelled,
            ChannelOperationRequestStatus.Rejected => CHANNEL_OPERATION_STATUS.Rejected,
            ChannelOperationRequestStatus.Pending => CHANNEL_OPERATION_STATUS.Pending,
            ChannelOperationRequestStatus.PSBTSignaturesPending => CHANNEL_OPERATION_STATUS.PsbtSignaturesPending,
            ChannelOperationRequestStatus.OnChainConfirmationPending => CHANNEL_OPERATION_STATUS
                .OnchainConfirmationPending,
            ChannelOperationRequestStatus.OnChainConfirmed => CHANNEL_OPERATION_STATUS.OnchainConfirmed,
            ChannelOperationRequestStatus.Failed => CHANNEL_OPERATION_STATUS.Failed,
            ChannelOperationRequestStatus.FinalizingPSBT => CHANNEL_OPERATION_STATUS.FinalizingPsbt,
            _ => throw new ArgumentOutOfRangeException(nameof(channelOperationRequest.Status), channelOperationRequest.Status, "Unknown status")
        };

        var type = channelOperationRequest.RequestType switch
        {
            OperationRequestType.Open => CHANNEL_OPERATION_TYPE.OpenChannel,
            OperationRequestType.Close => CHANNEL_OPERATION_TYPE.CloseChannel,
            _ => throw new ArgumentOutOfRangeException(nameof(channelOperationRequest.RequestType), channelOperationRequest.RequestType, "Unknown type")
        };

        var result = new GetChannelOperationRequestResponse
        {
            SatsAmount = channelOperationRequest.SatsAmount,
            Description = channelOperationRequest.Description,
            Status = status,
            Type = type,
            SourceNodeId = channelOperationRequest.SourceNodeId,
            Private = channelOperationRequest.IsChannelPrivate,
            JobId = channelOperationRequest.JobId
        };
        if (channelOperationRequest.TxId != null)
            result.TxId = channelOperationRequest.TxId;
        if (channelOperationRequest.ClosingReason != null)
            result.ClosingReason = channelOperationRequest.ClosingReason;
        if (channelOperationRequest.FeeRate != null)
            result.FeeRate = (double)channelOperationRequest.FeeRate;
        if (channelOperationRequest.WalletId != null)
            result.WalletId = channelOperationRequest.WalletId ?? 0;
        if (channelOperationRequest.ChannelId != null)
            result.ChannelId = channelOperationRequest.ChannelId ?? 0;
        if (channelOperationRequest.DestNodeId != null)
            result.DestNodeId = channelOperationRequest.DestNodeId ?? 0;

        return result;
    }

    public override async Task<AddLiquidityRuleResponse> AddLiquidityRule(AddLiquidityRuleRequest request, ServerCallContext context)
    {
        var channel = await _channelRepository.GetById(request.ChannelId);
        if (channel == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Channel not found"));
        }

        if (!channel.IsAutomatedLiquidityEnabled)
        {
            channel.IsAutomatedLiquidityEnabled = true;
        }

        var source = await _nodeRepository.GetById(channel.SourceNodeId);
        if (source == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Source node not found"));
        }
        var destination = await _nodeRepository.GetById(channel.DestinationNodeId);
        if (destination == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Destination node not found"));
        }

        var node = string.IsNullOrEmpty(source.ChannelAdminMacaroon) ? destination : source;

        var rules = await _liquidityRuleRepository.GetByNodePubKey(node.PubKey);
        var rule = rules.FirstOrDefault(r => r.ChannelId == request.ChannelId);

        var wallet = await _walletRepository.GetById(request.WalletId);
        if (wallet == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Wallet not found"));
        }

        var liquidityRule = rule ?? new LiquidityRule()
        {
            ChannelId = request.ChannelId,
            NodeId = node.Id,
            IsWalletRule = request.IsWalletRule,
        };
        if (request.IsWalletRule)
            liquidityRule.WalletId = request.WalletId;
        else
            liquidityRule.Address = request.Address;
        if (request.HasMinimumLocalBalance)
            liquidityRule.MinimumLocalBalance = (decimal)request.MinimumLocalBalance;
        if (request.HasMinimumRemoteBalance)
            liquidityRule.MinimumRemoteBalance = (decimal)request.MinimumRemoteBalance;
        if (request.HasRebalanceTarget)
            liquidityRule.RebalanceTarget = (decimal)request.RebalanceTarget;

        if (!(ValidateLocalBalance(liquidityRule) && ValidateRemoteBalance(liquidityRule) &&
            ValidateTargetBalance(liquidityRule)))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid rule"));
        }

        if (rule == null)
        {
            var addResult = await _liquidityRuleRepository.AddAsync(liquidityRule);
            if (!addResult.Item1)
            {
                throw new RpcException(new Status(StatusCode.Internal, "Error adding liquidity rule"));
            }
        }
        else
        {
            var updateResult = _liquidityRuleRepository.Update(liquidityRule);
            if (!updateResult.Item1)
            {
                throw new RpcException(new Status(StatusCode.Internal, "Error updating liquidity rule"));
            }
        }
        _channelRepository.Update(channel);

        return new AddLiquidityRuleResponse()
        {
            RuleId = liquidityRule.Id,
        };
    }

    private bool ValidateLocalBalance(LiquidityRule rule)
    {
        //If the minimum remote balance is 0 this cannot be 0
        if ((rule.MinimumLocalBalance == 0 || rule.MinimumLocalBalance == null)
            && (rule.MinimumRemoteBalance == 0 || rule.MinimumRemoteBalance == null))
            return false;

        //If the value is 0 is valid
        if (rule.MinimumRemoteBalance == 0 || rule.MinimumRemoteBalance == null)
            return true;

        //Check that the balance is between 0 and 100
        if (rule.MinimumLocalBalance < 0 || rule.MinimumLocalBalance > 100)
            return false;

        //Check that the Minimum local balance must be less than the minimum remote balance
        if (rule.MinimumLocalBalance >= rule.MinimumRemoteBalance)
            return false;

        return true;
    }

    private bool ValidateRemoteBalance(LiquidityRule rule)
    {
        //If the minimum local balance is 0 this cannot be 0
        if ((rule.MinimumLocalBalance == 0 || rule.MinimumLocalBalance == null)
            && (rule.MinimumRemoteBalance == 0 || rule.MinimumRemoteBalance == null))
            return false;

        //If the value is 0 is valid
        if (rule.MinimumRemoteBalance == 0 || rule.MinimumRemoteBalance == null)
            return true;

        //Check that the minimum remote balance is between 0 and 100
        if (rule.MinimumRemoteBalance < 0 || rule.MinimumRemoteBalance > 100)
            return false;

        //Check that the Minimum remote balance must be greater than the minimum local balance
        if (rule.MinimumRemoteBalance <= rule.MinimumLocalBalance)
            return false;

        return true;
    }

    private bool ValidateTargetBalance(LiquidityRule rule)
    {
        //If the value is 0 is valid
        if (rule.RebalanceTarget == 0 || rule.RebalanceTarget == null)
            return true;

        //Check that the target balance is between 0 and 100
        if (rule.RebalanceTarget < 0 || rule.RebalanceTarget > 100)
            return false;

        //Check that the rebalancetarget of the current liquidity rule is between the mininum local and minimum remote balance
        if (rule.RebalanceTarget < rule.MinimumLocalBalance ||
            rule.RebalanceTarget > rule.MinimumRemoteBalance)
            return false;

        return true;
    }

    public override async Task<GetAvailableUtxosResponse> GetAvailableUtxos(GetAvailableUtxosRequest request, ServerCallContext context)
    {
        var wallet = await _walletRepository.GetById(request.WalletId);
        if (wallet == null)
        {
            throw new Exception("Wallet not found");
        }

        var coinSelectionStrategy = request.Strategy switch
        {
            COIN_SELECTION_STRATEGY.BiggestFirst => CoinSelectionStrategy.BiggestFirst,
            COIN_SELECTION_STRATEGY.SmallestFirst => CoinSelectionStrategy.SmallestFirst,
            COIN_SELECTION_STRATEGY.ClosestToTargetFirst => CoinSelectionStrategy.ClosestToTargetFirst,
            COIN_SELECTION_STRATEGY.UpToAmount => CoinSelectionStrategy.UpToAmount,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Strategy), request.Strategy, "Unknown status")
        };

        var derivationStrategy = wallet.GetDerivationStrategy();
        if (derivationStrategy == null)
        {
            throw new Exception("Derivation strategy not found for wallet with id {walletId}");
        }

        var lockedUtxos = await _fmutxoRepository.GetLockedUTXOs();
        var utxos = await _nbXplorerService.GetUTXOsByLimitAsync(
            derivationStrategy,
            coinSelectionStrategy,
            request.Limit,
            request.Amount,
            request.ClosestTo,
            lockedUtxos.Select(utxo => $"{utxo.TxId}-{utxo.OutputIndex}").ToList()
            );

        var confirmedUtxos = utxos.Confirmed.UTXOs.Select(utxo => new Utxo()
        {
            Amount = (Money)utxo.Value,
            Outpoint = utxo.Outpoint.ToString()
        });
        var unconfirmedUtxos = utxos.Unconfirmed.UTXOs.Select(utxo => new Utxo()
        {
            Amount = (Money)utxo.Value,
            Outpoint = utxo.Outpoint.ToString()
        });

        return new GetAvailableUtxosResponse()
        {
            Confirmed = { confirmedUtxos },
            Unconfirmed = { unconfirmedUtxos },
        };
    }

    private WITHDRAWAL_REQUEST_STATUS GetStatus(WalletWithdrawalRequestStatus status)
    {
        return status switch
        {
            WalletWithdrawalRequestStatus.Cancelled => WITHDRAWAL_REQUEST_STATUS.WithdrawalCancelled,
            WalletWithdrawalRequestStatus.Failed => WITHDRAWAL_REQUEST_STATUS.WithdrawalFailed,
            WalletWithdrawalRequestStatus.FinalizingPSBT => WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingApproval,
            WalletWithdrawalRequestStatus.OnChainConfirmationPending => WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            WalletWithdrawalRequestStatus.OnChainConfirmed => WITHDRAWAL_REQUEST_STATUS.WithdrawalSettled,
            WalletWithdrawalRequestStatus.Pending => WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingApproval,
            WalletWithdrawalRequestStatus.PSBTSignaturesPending => WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingApproval,
            WalletWithdrawalRequestStatus.Rejected => WITHDRAWAL_REQUEST_STATUS.WithdrawalRejected,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status")
        };
    }

    public override async Task<GetWithdrawalsRequestStatusResponse> GetWithdrawalsRequestStatus(GetWithdrawalsRequestStatusRequest request, ServerCallContext context)
    {
        var withdrawalRequests = await _walletWithdrawalRequestRepository.GetByIds(request.RequestIds.ToList());
        return new GetWithdrawalsRequestStatusResponse()
        {
            WithdrawalRequests =
            {
                withdrawalRequests.Select(wr => new WithdrawalRequest
                {
                    RequestId = wr.Id,
                    Status = GetStatus(wr.Status),
                    RejectOrCancelReason = wr.RejectCancelDescription ?? ""
                }).ToList()
            }
        };
    }

    public override async Task<GetChannelResponse> GetChannel(GetChannelRequest request, ServerCallContext context)
    {
        var channel = await _channelRepository.GetById(request.ChannelId);
        if (channel == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Channel not found"));
        }

        var status = channel.Status switch
        {
            Channel.ChannelStatus.Open => CHANNEL_STATUS.Open,
            Channel.ChannelStatus.Closed => CHANNEL_STATUS.Closed,
            _ => throw new ArgumentOutOfRangeException(nameof(channel.Status), channel.Status, "Unknown status")
        };

        var result = new GetChannelResponse()
        {
            FundingTx = channel.FundingTx,
            OutputIndex = channel.FundingTxOutputIndex,
            ChanId = channel.ChanId,
            SatsAmount = channel.SatsAmount,
            Status = status,
            CreatedByNodeguard = channel.CreatedByNodeGuard,
            IsAutomatedLiquidityEnabled = channel.IsAutomatedLiquidityEnabled,
            IsPrivate = channel.IsPrivate,
        };

        result.BtcCloseAddress = channel.BtcCloseAddress != null ? channel.BtcCloseAddress : String.Empty;

        return result;
    }
}