using AutoMapper;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Helpers;
using NodeGuard.Jobs;
using NodeGuard.Services;
using NodeGuard.Services.Spark;
using Grpc.Core;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using NBXplorer.Models;
using Nodeguard;
using Quartz;
using LiquidityRule = NodeGuard.Data.Models.LiquidityRule;
using Wallet = NodeGuard.Data.Models.Wallet;

namespace NodeGuard.Rpc;

public interface INodeGuardService
{
    Task<GetLiquidityRulesResponse> GetLiquidityRules(GetLiquidityRulesRequest request,
        ServerCallContext context);

    Task<GetNewWalletAddressResponse> GetNewWalletAddress(GetNewWalletAddressRequest request,
        ServerCallContext context);

    Task<RequestWithdrawalResponse> RequestWithdrawal(RequestWithdrawalRequest request, ServerCallContext context);

    Task<BumpWithdrawalResponse> BumpWithdrawal(BumpWithdrawalRequest request, ServerCallContext context);

    Task<CancelWithdrawalResponse> CancelWithdrawal(CancelWithdrawalRequest request, ServerCallContext context);

    Task<GetAvailableWalletsResponse>
        GetAvailableWallets(GetAvailableWalletsRequest request, ServerCallContext context);

    Task<GetWalletBalanceResponse> GetWalletBalance(GetWalletBalanceRequest request, ServerCallContext context);

    Task<GetNodesResponse> GetNodes(GetNodesRequest request, ServerCallContext context);

    Task<AddNodeResponse> AddNode(AddNodeRequest request, ServerCallContext context);

    Task<OpenChannelResponse> OpenChannel(OpenChannelRequest request, ServerCallContext context);

    Task<CloseChannelResponse> CloseChannel(CloseChannelRequest request, ServerCallContext context);

    Task<GetChannelOperationRequestResponse> GetChannelOperationRequest(GetChannelOperationRequestRequest request, ServerCallContext context);

    Task<AddLiquidityRuleResponse> AddLiquidityRule(AddLiquidityRuleRequest request, ServerCallContext context);

    Task<GetUtxosResponse> GetAvailableUtxos(GetAvailableUtxosRequest request, ServerCallContext context);

    Task<GetUtxosResponse> GetUtxos(GetUtxosRequest request, ServerCallContext context);

    Task<GetWithdrawalsRequestStatusResponse> GetWithdrawalsRequestStatus(GetWithdrawalsRequestStatusRequest request, ServerCallContext context);

    Task<GetWithdrawalsRequestStatusResponse> GetWithdrawalsRequestStatusByReferenceIds(GetWithdrawalsRequestStatusByReferenceIdsRequest request, ServerCallContext context);

    Task<GetWithdrawalsRequestStatusResponse> GetWithdrawalsRequestStatusByTxHash(GetWithdrawalsRequestStatusByTxHashRequest request, ServerCallContext context);

    Task<GetChannelResponse> GetChannel(GetChannelRequest request, ServerCallContext context);

    Task<AddTagsResponse> AddTags(AddTagsRequest request, ServerCallContext context);

    Task<RequestRebalanceResponse> RequestRebalance(RequestRebalanceRequest request, ServerCallContext context);

    Task<GetRebalanceResponse> GetRebalance(GetRebalanceRequest request, ServerCallContext context);

    Task<GetRebalancesResponse> GetRebalances(GetRebalancesRequest request, ServerCallContext context);

    Task<SetChannelFeePolicyResponse> SetChannelFeePolicy(SetChannelFeePolicyRequest request, ServerCallContext context);

    Task<RequestSwapOutResponse> RequestSwapOut(RequestSwapOutRequest request, ServerCallContext context);

    Task<GetSwapOutResponse> GetSwapOut(GetSwapOutRequest request, ServerCallContext context);
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
    private readonly IUTXOTagRepository _utxoTagRepository;
    private readonly IHtlcMonitoringScheduler _htlcMonitoringScheduler;
    private readonly IRebalanceService _rebalanceService;
    private readonly IRebalanceRepository _rebalanceRepository;
    private readonly IWithdrawalRequestService _withdrawalRequestService;
    private readonly ISwapsService _swapsService;
    private readonly ISwapOutRepository _swapOutRepository;
    private readonly IAuditService _auditService;
    private readonly ISparkWalletsService _sparkWallets;

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
        IFMUTXORepository fmutxoRepository,
        IUTXOTagRepository utxoTagRepository,
        IHtlcMonitoringScheduler htlcMonitoringScheduler,
        IRebalanceService rebalanceService,
        IRebalanceRepository rebalanceRepository,
        IWithdrawalRequestService withdrawalRequestService,
        ISwapsService swapsService,
        ISwapOutRepository swapOutRepository,
        IAuditService auditService,
        ISparkWalletsService sparkWallets
    )
    {
        _sparkWallets = sparkWallets;
        _swapsService = swapsService;
        _swapOutRepository = swapOutRepository;
        _auditService = auditService;
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
        _utxoTagRepository = utxoTagRepository;
        _htlcMonitoringScheduler = htlcMonitoringScheduler;
        _rebalanceService = rebalanceService;
        _rebalanceRepository = rebalanceRepository;
        _withdrawalRequestService = withdrawalRequestService;
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
                LiquidityRules = { liquidityRules.Select(x => _mapper.Map<Nodeguard.LiquidityRule>(x)).ToList() }
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

        RefuseSparkWallet(wallet);

        var derivationStrategy = wallet.GetDerivationStrategy();
        if (derivationStrategy == null)
        {
            _logger.LogError("Derivation strategy not found for wallet with id {walletId}", request.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, "Derivation strategy not found"));
        }

        var derivationFeature = request.DerivationFeature switch
        {
            DERIVATION_FEATURE.Change => DerivationFeature.Change,
            DERIVATION_FEATURE.Direct => DerivationFeature.Direct,
            DERIVATION_FEATURE.Custom => DerivationFeature.Custom,
            _ => DerivationFeature.Deposit,
        };

        var btcAddress = await _nbXplorerService.GetUnusedAsync(derivationStrategy,
            derivationFeature,
            request.Skip,
            request.Reserve, context.CancellationToken);

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

    private void ValidateWithdrawalDestinations(IList<Destination> destinations, bool isChangeless = false)
    {
        if (destinations == null || destinations.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "At least one destination must be provided"));
        }

        // Changeless transactions can only have one destination
        if (isChangeless && destinations.Count > 1)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Changeless transactions can only have one destination"));
        }

        foreach (var destination in destinations)
        {
            if (destination.AmountSats <= 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Amount must be greater than 0"));
            }

            if (string.IsNullOrEmpty(destination.Address))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    "A destination address must be provided"));
            }
        }
    }

    /// <summary>
    /// Maps the proto coin selection strategy onto the domain one. The two enums are kept in sync by hand, so
    /// this switches on the members instead of casting the ordinal. <paramref name="unknownStrategyException"/>
    /// builds the exception thrown for an unmapped member, so each caller keeps its own error contract.
    /// </summary>
    private static CoinSelectionStrategy MapCoinSelectionStrategy(COIN_SELECTION_STRATEGY strategy,
        Func<COIN_SELECTION_STRATEGY, Exception> unknownStrategyException)
    {
        return strategy switch
        {
            COIN_SELECTION_STRATEGY.SmallestFirst => CoinSelectionStrategy.SmallestFirst,
            COIN_SELECTION_STRATEGY.BiggestFirst => CoinSelectionStrategy.BiggestFirst,
            COIN_SELECTION_STRATEGY.ClosestToTargetFirst => CoinSelectionStrategy.ClosestToTargetFirst,
            COIN_SELECTION_STRATEGY.UpToAmount => CoinSelectionStrategy.UpToAmount,
            _ => throw unknownStrategyException(strategy)
        };
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

            RefuseSparkWallet(wallet);

            // Validate destinations
            ValidateWithdrawalDestinations(request.Destinations, request.Changeless);

            var outpoints = new List<OutPoint>();
            var utxos = new List<UTXO>();

            if (request.Changeless)
            {
                foreach (var outpoint in request.UtxosOutpoints)
                {
                    outpoints.Add(OutPoint.Parse(outpoint));
                }

                // Search the utxos and lock them
                var derivationStrategyBase = wallet.GetDerivationStrategy();

                if (derivationStrategyBase == null)
                    throw new RpcException(new Status(StatusCode.Internal, "Derivation strategy not found"));

                utxos = await _coinSelectionService.GetUTXOsByOutpointAsync(derivationStrategyBase, outpoints);

            }

            // Create destination objects for the withdrawal request
            var withdrawalDestinations = request.Destinations.Select(d => new WalletWithdrawalRequestDestination()
            {
                Address = d.Address,
                Amount = new Money(d.AmountSats, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC),
            }).ToList();

            withdrawalRequest = new WalletWithdrawalRequest()
            {
                WalletId = request.WalletId,
                WalletWithdrawalRequestDestinations = withdrawalDestinations,
                Description = request.Description,
                Status = wallet.IsHotWallet
                    ? WalletWithdrawalRequestStatus.PSBTSignaturesPending
                    : WalletWithdrawalRequestStatus.Pending,
                RequestMetadata = request.RequestMetadata,
                Changeless = request.Changeless,
                MempoolRecommendedFeesType = (MempoolRecommendedFeesType)request.MempoolFeeRate,
                CustomFeeRate = request.CustomFeeRate,
                ReferenceId = request.ReferenceId,
                CoinSelectionStrategy = request.HasCoinSelectionStrategy
                    ? MapCoinSelectionStrategy(request.CoinSelectionStrategy,
                        strategy => new RpcException(new Status(StatusCode.InvalidArgument,
                            $"Unknown coin selection strategy: {strategy}")))
                    : null
            };

            //Save withdrawal request
            var withdrawalSaved = await _walletWithdrawalRequestRepository.AddAsync(withdrawalRequest);
            if (!withdrawalSaved.Item1 && withdrawalSaved.Item2!.Contains("does not have enough funds"))
            {
                _logger.LogError(withdrawalSaved.Item2);
                throw new NotEnoughBalanceInWalletException(withdrawalSaved.Item2);
            }

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

            // Update to refresh from db
            withdrawalRequest = await _walletWithdrawalRequestRepository.GetById(withdrawalRequest.Id);

            if (!withdrawalSaved.Item1)
            {
                _logger.LogError("Error saving withdrawal request for wallet with id {walletId}", request.WalletId);
                throw new RpcException(new Status(StatusCode.Internal, "Error saving withdrawal request for wallet"));
            }

            // Template PSBT generation with SIGHASH_ALL
            var psbt = await _bitcoinService.GenerateTemplatePSBT(withdrawalRequest ??
            throw new ArgumentException(nameof(withdrawalRequest)));

            // If the wallet is hot, we send the withdrawal request to the embedded or remote signer
            if (wallet.IsHotWallet)
            {
                var map = new JobDataMap();
                map.Put("withdrawalRequestId", withdrawalRequest.Id);
                var job = SimpleJob.Create<PerformWithdrawalJob>(map, withdrawalRequest.Id.ToString());
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
            MarkWithdrawalRequestCancelled(withdrawalRequest);
            _logger.LogError("No available UTXOs for wallet with id {walletId}", request.WalletId);
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "No available UTXOs for wallet"));
        }
        catch (NotEnoughBalanceInWalletException e)
        {
            _logger.LogError(e.Message);
            throw new RpcException(new Status(StatusCode.ResourceExhausted, e.Message));
        }
        catch (RpcException e)
        {
            MarkWithdrawalRequestCancelled(withdrawalRequest);
            _logger.LogError(e.Message);
            throw new RpcException(new Status(e.Status.StatusCode, e.Status.Detail));
        }
        catch (Exception e)
        {
            MarkWithdrawalRequestCancelled(withdrawalRequest);
            _logger.LogError(e, "Error requesting withdrawal for wallet with id {walletId}", request.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, "Error requesting withdrawal for wallet"));
        }
    }

    private void MarkWithdrawalRequestCancelled(WalletWithdrawalRequest? withdrawalRequest)
    {
        if (withdrawalRequest != null)
        {
            withdrawalRequest.Status = WalletWithdrawalRequestStatus.Cancelled;
            var (success, error) = _walletWithdrawalRequestRepository.Update(withdrawalRequest);
            if (!success)
            {
                _logger.LogError(error, "Error updating status of withdrawal request {RequestId} for wallet {WalletId}",
                    withdrawalRequest.Id, withdrawalRequest.WalletId);
            }
        }
    }

    public override async Task<BumpWithdrawalResponse> BumpWithdrawal(BumpWithdrawalRequest request,
        ServerCallContext context)
    {
        var target = request.TargetCase switch
        {
            BumpWithdrawalRequest.TargetOneofCase.RequestId => new ReplacementTarget(request.RequestId, null),
            BumpWithdrawalRequest.TargetOneofCase.TxId => new ReplacementTarget(null, request.TxId),
            _ => new ReplacementTarget(null, null),
        };

        var (requestId, txid, isHotWallet) = await ReplaceWithdrawalAsync(target,
            (MempoolRecommendedFeesType)request.MempoolFeeRate,
            request.HasCustomFeeRate ? request.CustomFeeRate : null,
            cancellation: false);

        return new BumpWithdrawalResponse { RequestId = requestId, Txid = txid, IsHotWallet = isHotWallet };
    }

    public override async Task<CancelWithdrawalResponse> CancelWithdrawal(CancelWithdrawalRequest request,
        ServerCallContext context)
    {
        var target = request.TargetCase switch
        {
            CancelWithdrawalRequest.TargetOneofCase.RequestId => new ReplacementTarget(request.RequestId, null),
            CancelWithdrawalRequest.TargetOneofCase.TxId => new ReplacementTarget(null, request.TxId),
            _ => new ReplacementTarget(null, null),
        };

        var (requestId, txid, isHotWallet) = await ReplaceWithdrawalAsync(target,
            (MempoolRecommendedFeesType)request.MempoolFeeRate,
            request.HasCustomFeeRate ? request.CustomFeeRate : null,
            cancellation: true);

        return new CancelWithdrawalResponse { RequestId = requestId, Txid = txid, IsHotWallet = isHotWallet };
    }

    /// <summary>The withdrawal to replace, addressed by NodeGuard request id or by the txid RequestWithdrawal returned.</summary>
    private readonly record struct ReplacementTarget(int? RequestId, string? TxId)
    {
        public override string ToString() => TxId != null ? $"txid {TxId}" : $"id {RequestId}";
    }

    /// <summary>
    /// Shared body of BumpWithdrawal and CancelWithdrawal: resolve the target, create the replacement through
    /// WithdrawalRequestService and, for hot wallets, execute it; cold wallets only get the template so the approvers
    /// reuse it, exactly as RequestWithdrawal does.
    /// </summary>
    private async Task<(int RequestId, string Txid, bool IsHotWallet)> ReplaceWithdrawalAsync(ReplacementTarget target,
        MempoolRecommendedFeesType feeType, decimal? customFeeRate, bool cancellation)
    {
        var action = cancellation ? "cancellation" : "fee bump";
        WalletWithdrawalRequest? replacement = null;
        try
        {
            var originalRequestId = await ResolveReplacementTargetAsync(target);

            replacement = cancellation
                ? await _withdrawalRequestService.CreateCancelRequestAsync(originalRequestId, feeType, customFeeRate)
                : await _withdrawalRequestService.CreateBumpRequestAsync(originalRequestId, feeType, customFeeRate);

            var isHotWallet = replacement.Wallet?.IsHotWallet ?? false;

            var psbt = isHotWallet
                ? await _withdrawalRequestService.ScheduleHotWalletWithdrawalAsync(replacement.Id)
                : await _bitcoinService.GenerateTemplatePSBT(replacement);

            return (replacement.Id, psbt.GetGlobalTransaction().GetHash().ToString(), isHotWallet);
        }
        catch (BumpingException e)
        {
            _logger.LogWarning("RBF {Action} of withdrawal request {Target} refused ({Reason}): {Message}",
                action, target, e.Reason, e.Message);
            MarkWithdrawalRequestCancelled(replacement);
            throw new RpcException(new Status(ToStatusCode(e.Reason), e.Message));
        }
        catch (NoUTXOsAvailableException)
        {
            MarkWithdrawalRequestCancelled(replacement);
            _logger.LogError("No available UTXOs for the RBF {Action} of withdrawal request {Target}", action, target);
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "No available UTXOs for wallet"));
        }
        catch (ShowToUserException e)
        {
            MarkWithdrawalRequestCancelled(replacement);
            _logger.LogError(e, "Error in the RBF {Action} of withdrawal request {Target}", action, target);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }
        catch (RpcException)
        {
            MarkWithdrawalRequestCancelled(replacement);
            throw;
        }
        catch (Exception e)
        {
            MarkWithdrawalRequestCancelled(replacement);
            _logger.LogError(e, "Error in the RBF {Action} of withdrawal request {Target}", action, target);
            throw new RpcException(new Status(StatusCode.Internal, $"Error in the RBF {action} of the withdrawal request"));
        }
    }

    /// <summary>
    /// A txid is validated, normalised (stored txids are lowercase hex) and resolved to the request that broadcast it,
    /// so the service only ever deals with request ids.
    /// </summary>
    private async Task<int> ResolveReplacementTargetAsync(ReplacementTarget target)
    {
        if (target.RequestId is { } requestId)
        {
            return requestId;
        }

        if (target.TxId is { } rawTxId)
        {
            if (!uint256.TryParse(rawTxId, out var txId))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "tx_id is not a valid transaction id"));
            }

            var withdrawalRequest = await _walletWithdrawalRequestRepository.GetByTxHash(txId.ToString());
            if (withdrawalRequest == null)
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"No withdrawal request found for txid {txId}"));
            }

            return withdrawalRequest.Id;
        }

        throw new RpcException(new Status(StatusCode.InvalidArgument, "request_id or tx_id is required"));
    }

    private static StatusCode ToStatusCode(BumpingErrorReason reason)
    {
        return reason switch
        {
            BumpingErrorReason.RequestNotFound => StatusCode.NotFound,
            BumpingErrorReason.InvalidState
                or BumpingErrorReason.AlreadyConfirmed
                or BumpingErrorReason.TransactionNotFound
                or BumpingErrorReason.ChangelessMultipleDestinations => StatusCode.FailedPrecondition,
            BumpingErrorReason.InvalidFeeRate
                or BumpingErrorReason.FeeRateNotHigher
                or BumpingErrorReason.FeeExceedsInputs => StatusCode.InvalidArgument,
            _ => StatusCode.Internal,
        };
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

    public override async Task<GetWalletBalanceResponse> GetWalletBalance(GetWalletBalanceRequest request, ServerCallContext context)
    {
        try
        {
            var wallet = await _walletRepository.GetById(request.WalletId);
            if (wallet == null)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "Wallet not found"));
            }

            RefuseSparkWallet(wallet);

            var balance = await _lightningService.GetWalletBalance(wallet);
            if (balance == null)
            {
                throw new RpcException(new Status(StatusCode.Internal, "Error getting wallet balance"));
            }

            return new GetWalletBalanceResponse
            {
                ConfirmedBalance = ((Money)balance.Confirmed).Satoshi,
                UnconfirmedBalance = ((Money)balance.Unconfirmed).Satoshi
            };
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.FailedPrecondition)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Error getting wallet balance through gRPC");
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
            FundsDestinationWalletId = request.ReturningFundsWalletId,
        };

        if (request.ReturningFundsWalletId != 0)
        {
            RefuseSparkWallet(await _walletRepository.GetById(request.ReturningFundsWalletId));
        }

        try
        {
            var result = await _nodeRepository.AddAsync(node);

            if (result.Item1)
            {
                await _htlcMonitoringScheduler.EnsureNodeWorkerScheduled(node);
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
                Nodes = { mappedNodes }
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

        RefuseSparkWallet(wallet);

        if (request.MempoolFeeRate == FEES_TYPE.CustomFee && request.CustomFeeRate == 0)
        {
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "Mempool fee rate configuration is not valid"));
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

            if (request.UtxosOutpoints.Count > 0)
            {
                foreach (var outpoint in request.UtxosOutpoints)
                {
                    outpoints.Add(OutPoint.Parse(outpoint));
                }

                // Search the utxos and lock them
                var derivationStrategy = wallet.GetDerivationStrategy();
                if (derivationStrategy == null)
                {
                    throw new RpcException(new Status(StatusCode.Internal, "Derivation strategy not found"));
                }

                utxos = await _coinSelectionService.GetUTXOsByOutpointAsync(derivationStrategy, outpoints);
            }

            // Get the fee type of the request
            var feeType = request.MempoolFeeRate switch
            {
                FEES_TYPE.EconomyFee => MempoolRecommendedFeesType.EconomyFee,
                FEES_TYPE.FastestFee => MempoolRecommendedFeesType.FastestFee,
                FEES_TYPE.HourFee => MempoolRecommendedFeesType.HourFee,
                FEES_TYPE.HalfHourFee => MempoolRecommendedFeesType.HalfHourFee,
                FEES_TYPE.CustomFee => MempoolRecommendedFeesType.CustomFee,
                _ => throw new ArgumentOutOfRangeException(nameof(request.MempoolFeeRate), request.MempoolFeeRate,
                    "Unknown status")
            };

            if (feeType == MempoolRecommendedFeesType.CustomFee && !request.HasCustomFeeRate)
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
                MempoolRecommendedFeesType = feeType,
                FeeRate = feeType == MempoolRecommendedFeesType.CustomFee ? request.CustomFeeRate : null,
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

            var (templatePsbt, noUtxosAvailable) =
                (await _lightningService.GenerateTemplatePSBT(channelOperationRequest));
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

    public override async Task<CloseChannelResponse> CloseChannel(CloseChannelRequest request,
        ServerCallContext context)
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

    public override async Task<GetChannelOperationRequestResponse> GetChannelOperationRequest(
        GetChannelOperationRequestRequest request, ServerCallContext context)
    {
        var channelOperationRequest =
            await _channelOperationRequestRepository.GetById(request.ChannelOperationRequestId);

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
            _ => throw new ArgumentOutOfRangeException(nameof(channelOperationRequest.Status),
                channelOperationRequest.Status, "Unknown status")
        };

        var type = channelOperationRequest.RequestType switch
        {
            OperationRequestType.Open => CHANNEL_OPERATION_TYPE.OpenChannel,
            OperationRequestType.Close => CHANNEL_OPERATION_TYPE.CloseChannel,
            _ => throw new ArgumentOutOfRangeException(nameof(channelOperationRequest.RequestType),
                channelOperationRequest.RequestType, "Unknown type")
        };

        var result = new GetChannelOperationRequestResponse
        {
            SatsAmount = channelOperationRequest.SatsAmount,
            Description = channelOperationRequest.Description,
            Status = status,
            Type = type,
            SourceNodeId = channelOperationRequest.SourceNodeId,
            Private = channelOperationRequest.IsChannelPrivate
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

    public override async Task<AddLiquidityRuleResponse> AddLiquidityRule(AddLiquidityRuleRequest request,
        ServerCallContext context)
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

        var swapWallet = await _walletRepository.GetById(request.SwapWalletId);
        if (swapWallet == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Swap wallet not found"));
        }

        RefuseSparkWallet(swapWallet);

        if (request.IsReverseSwapWalletRule)
        {
            if (!request.HasReverseSwapWalletId)
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "WalletId is required for wallet rules"));

            var wallet = await _walletRepository.GetById(request.ReverseSwapWalletId);
            if (wallet == null)
                throw new RpcException(new Status(StatusCode.NotFound, "Wallet not found"));
            RefuseSparkWallet(wallet);
        }
        else
        {
            if (!request.HasReverseSwapAddress)
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    "Address is required for address rules"));
            if (!ValidateBitcoinAddress(request.ReverseSwapAddress))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid address"));
        }

        var liquidityRule = rule ?? new LiquidityRule()
        {
            ChannelId = request.ChannelId,
            NodeId = node.Id,
            SwapWalletId = request.SwapWalletId
        };
        liquidityRule.IsReverseSwapWalletRule = request.IsReverseSwapWalletRule;
        if (request.IsReverseSwapWalletRule)
        {
            liquidityRule.ReverseSwapWalletId = request.ReverseSwapWalletId;
            liquidityRule.ReverseSwapAddress = null;
        }
        else
        {
            liquidityRule.ReverseSwapAddress = request.ReverseSwapAddress;
            liquidityRule.ReverseSwapWalletId = null;
        }

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

    public override async Task<GetUtxosResponse> GetUtxos(GetUtxosRequest request, ServerCallContext context)
    {
        var wallets = await _walletRepository.GetAvailableWallets();
        List<Utxo> confirmed = [];
        List<Utxo> unconfirmed = [];
        foreach (var wallet in wallets)
        {
            var derivationStrategy = wallet.GetDerivationStrategy();
            if (derivationStrategy == null)
            {
                continue;
            }

            var walletUtxos = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);

            confirmed.AddRange(walletUtxos.Confirmed.UTXOs.Select(utxo => new Utxo()
            {
                Amount = (Money)utxo.Value,
                Outpoint = utxo.Outpoint.ToString(),
                Address = utxo.Address.ToString()
            }));
            unconfirmed.AddRange(walletUtxos.Unconfirmed.UTXOs.Select(utxo => new Utxo()
            {
                Amount = (Money)utxo.Value,
                Outpoint = utxo.Outpoint.ToString(),
                Address = utxo.Address.ToString()
            }));
        }

        return new GetUtxosResponse()
        {
            Confirmed = { confirmed },
            Unconfirmed = { unconfirmed }
        };
    }

    public override async Task<GetUtxosResponse> GetAvailableUtxos(GetAvailableUtxosRequest request, ServerCallContext context)
    {
        var wallet = await _walletRepository.GetById(request.WalletId);
        if (wallet == null)
        {
            throw new Exception("Wallet not found");
        }

        RefuseSparkWallet(wallet);

        var coinSelectionStrategy = MapCoinSelectionStrategy(request.Strategy,
            strategy => new ArgumentOutOfRangeException(nameof(request.Strategy), strategy, "Unknown status"));

        var derivationStrategy = wallet.GetDerivationStrategy();
        if (derivationStrategy == null)
        {
            throw new Exception("Derivation strategy not found for wallet with id {walletId}");
        }

        var walletUtxos = await _nbXplorerService.GetUTXOsAsync(derivationStrategy);
        var lockedUtxos = await _fmutxoRepository.GetLockedUTXOsByWalletId(wallet.Id);
        var ignoreOutpoints = new List<string>();
        var listLocked = lockedUtxos.Select(utxo => $"{utxo.TxId}-{utxo.OutputIndex}").ToList();
        var listFrozen = await _coinSelectionService.GetFrozenUTXOs();

        // filter frozen list by only including UTXOs that belong to the wallet
        // TODO: find a way to add wallet id to the UTXOTag model
        listFrozen = listFrozen
            .Where(utxo => walletUtxos.Confirmed.UTXOs.Any(u => u.Outpoint.ToString() == utxo) ||
                           walletUtxos.Unconfirmed.UTXOs.Any(u => u.Outpoint.ToString() == utxo))
                           .ToList();

        // Ignore dust UTXOs server-side too, so they are not counted towards the requested amount
        // by any selection strategy and then stripped locally, returning a short selection
        var listDust = walletUtxos.Confirmed.UTXOs
            .Concat(walletUtxos.Unconfirmed.UTXOs)
            .Where(utxo => ((Money)utxo.Value).Satoshi <= Constants.MINIMUM_UTXO_VALUE_SATS)
            .Select(utxo => utxo.Outpoint.ToString())
            .ToList();

        ignoreOutpoints.AddRange(listLocked);
        ignoreOutpoints.AddRange(listFrozen);
        ignoreOutpoints.AddRange(listDust);

        UTXOChanges utxos;
        try
        {
            utxos = await _nbXplorerService.GetUTXOsByLimitAsync(
                derivationStrategy,
                coinSelectionStrategy,
                request.Limit,
                request.Amount,
                request.ClosestTo,
                ignoreOutpoints
                );
        }
        catch (Exception e)
        {
            // Preserve this RPC's previous contract: a failing custom backend yields an empty
            // selection instead of an error
            _logger.LogWarning(e,
                "UTXO selection through the custom NBXplorer backend failed for wallet {WalletId}, returning an empty selection",
                request.WalletId);
            utxos = new UTXOChanges();
        }

        var confirmedUtxos = utxos.Confirmed.UTXOs.Select(utxo => new Utxo()
        {
            Amount = (Money)utxo.Value,
            Outpoint = utxo.Outpoint.ToString(),
            Address = utxo.Address.ToString()
        });
        var unconfirmedUtxos = utxos.Unconfirmed.UTXOs.Select(utxo => new Utxo()
        {
            Amount = (Money)utxo.Value,
            Outpoint = utxo.Outpoint.ToString(),
            Address = utxo.Address.ToString()
        });

        return new GetUtxosResponse()
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
            WalletWithdrawalRequestStatus.Bumped => WITHDRAWAL_REQUEST_STATUS.WithdrawalBumped,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status")
        };
    }

    private async Task<GetWithdrawalsRequestStatusResponse> GetWithdrawalsRequestStatusResponse(List<WalletWithdrawalRequest> withdrawalRequests)
    {
        var withdrawalsResponses = new List<WithdrawalRequest>();
        foreach (var withdrawalRequest in withdrawalRequests)
        {
            ulong confirmations = 0;
            if (withdrawalRequest.TxId != null)
            {
                var nbxplorerStatus = await _nbXplorerService.GetTransactionAsync(uint256.Parse(withdrawalRequest.TxId));
                confirmations = (ulong)(nbxplorerStatus?.Confirmations ?? 0);
            }

            withdrawalsResponses.Add(new WithdrawalRequest
            {
                RequestId = withdrawalRequest.Id,
                Status = GetStatus(withdrawalRequest.Status),
                RejectOrCancelReason = withdrawalRequest.RejectCancelDescription ?? "",
                ReferenceId = withdrawalRequest.ReferenceId ?? "",
                Confirmations = confirmations,
                TxId = withdrawalRequest.TxId ?? "",
                RequestMetadata = withdrawalRequest.RequestMetadata ?? "",
            });
        }

        return new GetWithdrawalsRequestStatusResponse()
        {
            WithdrawalRequests = { withdrawalsResponses }
        };
    }

    public override async Task<GetWithdrawalsRequestStatusResponse> GetWithdrawalsRequestStatus(GetWithdrawalsRequestStatusRequest request, ServerCallContext context)
    {
        var withdrawalRequests = await _walletWithdrawalRequestRepository.GetByIds(request.RequestIds.ToList());

        return await GetWithdrawalsRequestStatusResponse(withdrawalRequests);
    }

    public override async Task<GetWithdrawalsRequestStatusResponse> GetWithdrawalsRequestStatusByReferenceIds(GetWithdrawalsRequestStatusByReferenceIdsRequest request, ServerCallContext context)
    {
        var withdrawalRequests = await _walletWithdrawalRequestRepository.GetByReferenceIds(request.ReferenceIds.ToList());

        return await GetWithdrawalsRequestStatusResponse(withdrawalRequests);
    }

    public override async Task<GetWithdrawalsRequestStatusResponse> GetWithdrawalsRequestStatusByTxHash(GetWithdrawalsRequestStatusByTxHashRequest request, ServerCallContext context)
    {
        var withdrawalRequest = await _walletWithdrawalRequestRepository.GetByTxHash(request.TxHash);
        if (withdrawalRequest == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Withdrawal request not found"));
        }

        return await GetWithdrawalsRequestStatusResponse(new List<WalletWithdrawalRequest> { withdrawalRequest });
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

    public override async Task<AddTagsResponse> AddTags(AddTagsRequest request, ServerCallContext context)
    {
        if (request.Tags.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Tags are required"));
        }

        foreach (var tag in request.Tags)
        {
            if (!OutPoint.TryParse(tag.UtxoOutpoint, out var outpoint))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"Invalid output index {tag.UtxoOutpoint}"));
            }
            // We overwrite the tag with the correct outpoint format, because OutPoint.TryParse also accepts `:` as separator
            tag.UtxoOutpoint = outpoint!.ToString();
        }

        var tags = request.Tags.Select(tag => new UTXOTag
        {
            Outpoint = tag.UtxoOutpoint,
            Key = tag.Key,
            Value = tag.Value
        }).ToList();

        var result = await _utxoTagRepository.UpsertRangeAsync(tags);
        if (!result.Item1)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Error adding tags"));
        }

        return new AddTagsResponse();
    }

    public override async Task<RequestRebalanceResponse> RequestRebalance(RequestRebalanceRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.NodePubkey))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "node_pubkey is required"));

        if (request.AmountSats <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "amount_sats must be > 0"));

        if (!request.HasSourceChannelId || request.SourceChannelId <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "source_channel_id is required"));

        if (!request.HasTargetPubkey || string.IsNullOrWhiteSpace(request.TargetPubkey))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "target_pubkey is required"));

        var node = await _nodeRepository.GetByPubkey(request.NodePubkey);
        if (node == null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Node with pubkey {request.NodePubkey} not found"));

        var domainRequest = new RebalanceRequest(
            NodeId: node.Id,
            SourceChannelId: request.SourceChannelId,
            TargetPubkey: request.TargetPubkey,
            AmountSats: request.AmountSats,
            MaxFeePct: request.HasMaxFeePct ? request.MaxFeePct : null,
            TimeoutSeconds: request.TimeoutSeconds,
            IsManual: true,
            UserRequestorId: null,
            AmountBackoffRatio: request.HasAmountBackoffRatio ? request.AmountBackoffRatio : null,
            MaxAttempts: request.HasMaxAttempts ? request.MaxAttempts : null,
            RetryMaxFeePct: request.HasRetryMaxFeePct ? request.RetryMaxFeePct : null);

        Rebalance rebalance;
        try
        {
            rebalance = await _rebalanceService.RebalanceAsync(domainRequest, context.CancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RequestRebalance failed for node {NodePubkey}", request.NodePubkey);
            throw new RpcException(new Status(StatusCode.Internal, $"Rebalance failed: {ex.Message}"));
        }

        var response = new RequestRebalanceResponse
        {
            RebalanceId = rebalance.Id,
            Status = MapRebalanceStatus(rebalance.Status),
            AttemptNumber = rebalance.AttemptNumber,
        };
        if (rebalance.FeePaidSats.HasValue) response.FeePaidSats = rebalance.FeePaidSats.Value;
        if (rebalance.EffectivePpm.HasValue) response.EffectivePpm = rebalance.EffectivePpm.Value;
        if (rebalance.SatsAmount != rebalance.RequestedAmountSats) response.ActualAmountSats = rebalance.SatsAmount;
        return response;
    }

    public override async Task<GetRebalanceResponse> GetRebalance(GetRebalanceRequest request, ServerCallContext context)
    {
        if (request.RebalanceId <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "rebalance_id is required"));

        var rebalance = await _rebalanceRepository.GetById(request.RebalanceId);
        if (rebalance == null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Rebalance {request.RebalanceId} not found"));

        return MapRebalance(rebalance);
    }

    public override async Task<GetRebalancesResponse> GetRebalances(GetRebalancesRequest request, ServerCallContext context)
    {
        if (request.PageNumber < 1)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "page_number must be >= 1"));
        if (request.PageSize <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "page_size must be > 0"));

        int? nodeIdFilter = null;
        if (request.HasNodePubkey && !string.IsNullOrWhiteSpace(request.NodePubkey))
        {
            var node = await _nodeRepository.GetByPubkey(request.NodePubkey);
            if (node == null)
                return new GetRebalancesResponse { TotalCount = 0 };
            nodeIdFilter = node.Id;
        }

        var statusFilter = request.HasStatus ? MapRebalanceStatus(request.Status) : (RebalanceStatus?)null;
        var fromDate = request.HasFromUnix ? DateTimeOffset.FromUnixTimeSeconds(request.FromUnix) : (DateTimeOffset?)null;
        var toDate = request.HasToUnix ? DateTimeOffset.FromUnixTimeSeconds(request.ToUnix) : (DateTimeOffset?)null;

        var (rebalances, totalCount) = await _rebalanceRepository.GetPaginatedAsync(
            pageNumber: request.PageNumber,
            pageSize: request.PageSize,
            status: statusFilter,
            nodeId: nodeIdFilter,
            sourceChannelId: request.HasSourceChannelId ? request.SourceChannelId : null,
            userId: request.HasUserId ? request.UserId : null,
            isManual: request.HasIsManual ? request.IsManual : null,
            fromDate: fromDate,
            toDate: toDate);

        var response = new GetRebalancesResponse { TotalCount = totalCount };
        response.Rebalances.AddRange(rebalances.Select(MapRebalance));
        return response;
    }

    private static GetRebalanceResponse MapRebalance(Rebalance rebalance)
    {
        var response = new GetRebalanceResponse
        {
            RebalanceId = rebalance.Id,
            NodePubkey = rebalance.SourceNodePubKey ?? rebalance.Node?.PubKey ?? string.Empty,
            Status = MapRebalanceStatus(rebalance.Status),
            IsManual = rebalance.IsManual,
            AttemptNumber = rebalance.AttemptNumber,
            RequestedAmountSats = rebalance.RequestedAmountSats,
            AmountSats = rebalance.SatsAmount,
            MaxFeePct = rebalance.MaxFeePct,
            CreationDatetimeUnix = rebalance.CreationDatetime.ToUnixTimeSeconds(),
            UpdateDatetimeUnix = rebalance.UpdateDatetime.ToUnixTimeSeconds(),
        };
        if (rebalance.RetryMaxFeePct.HasValue) response.RetryMaxFeePct = rebalance.RetryMaxFeePct.Value;
        if (rebalance.FeePaidSats.HasValue) response.FeePaidSats = rebalance.FeePaidSats.Value;
        if (rebalance.EffectivePpm.HasValue) response.EffectivePpm = rebalance.EffectivePpm.Value;
        if (rebalance.SourceChanIdLnd.HasValue) response.SourceChanId = rebalance.SourceChanIdLnd.Value;
        if (rebalance.TargetPubkey != null) response.TargetPubkey = rebalance.TargetPubkey;
        if (rebalance.AmountBackoffRatio.HasValue) response.AmountBackoffRatio = rebalance.AmountBackoffRatio.Value;
        if (rebalance.MaxAttempts.HasValue) response.MaxAttempts = rebalance.MaxAttempts.Value;
        return response;
    }

    private static REBALANCE_STATUS MapRebalanceStatus(RebalanceStatus status) => status switch
    {
        RebalanceStatus.Pending => REBALANCE_STATUS.RebalancePending,
        RebalanceStatus.InFlight => REBALANCE_STATUS.RebalanceInFlight,
        RebalanceStatus.Succeeded => REBALANCE_STATUS.RebalanceSucceeded,
        RebalanceStatus.Failed => REBALANCE_STATUS.RebalanceFailed,
        RebalanceStatus.NoRoute => REBALANCE_STATUS.RebalanceNoRoute,
        RebalanceStatus.Timeout => REBALANCE_STATUS.RebalanceTimeout,
        RebalanceStatus.InsufficientBalance => REBALANCE_STATUS.RebalanceInsufficientBalance,
        RebalanceStatus.ExceededFeeLimit => REBALANCE_STATUS.RebalanceExceededFeeLimit,
        _ => REBALANCE_STATUS.RebalanceFailed,
    };

    private static RebalanceStatus MapRebalanceStatus(REBALANCE_STATUS status) => status switch
    {
        REBALANCE_STATUS.RebalancePending => RebalanceStatus.Pending,
        REBALANCE_STATUS.RebalanceInFlight => RebalanceStatus.InFlight,
        REBALANCE_STATUS.RebalanceSucceeded => RebalanceStatus.Succeeded,
        REBALANCE_STATUS.RebalanceFailed => RebalanceStatus.Failed,
        REBALANCE_STATUS.RebalanceNoRoute => RebalanceStatus.NoRoute,
        REBALANCE_STATUS.RebalanceTimeout => RebalanceStatus.Timeout,
        REBALANCE_STATUS.RebalanceInsufficientBalance => RebalanceStatus.InsufficientBalance,
        REBALANCE_STATUS.RebalanceExceededFeeLimit => RebalanceStatus.ExceededFeeLimit,
        _ => RebalanceStatus.Failed,
    };

    private bool ValidateBitcoinAddress(string address)
    {
        try
        {
            BitcoinAddress.Create(address, CurrentNetworkHelper.GetCurrentNetwork());
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    public override async Task<SetChannelFeePolicyResponse> SetChannelFeePolicy(SetChannelFeePolicyRequest request, ServerCallContext context)
    {
        try
        {
            await _lightningService.SetChannelFeePolicy(request.ChanPoint, request.NodePubkey, request.BaseFeeMsat, request.FeeRatePpm, request.TimeLockDelta, request.InboundFeePolicy?.BaseFeeMsat, request.InboundFeePolicy?.FeeRatePpm);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
        catch (Exception e)
        {
            throw new RpcException(new Status(StatusCode.Internal, e.Message));
        }

        return new SetChannelFeePolicyResponse();
    }

    /// <summary>
    /// Spark wallets (SPARK_SIGNER=wallet) have no on-chain keys: the methods that take an on-chain wallet refuse them
    /// </summary>
    private static void RefuseSparkWallet(Wallet? wallet)
    {
        if (wallet is { Kind: WalletKind.Spark })
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Wallet {wallet.Id} is a Spark wallet, not an on-chain wallet"));
    }

    public override async Task<RequestSwapOutResponse> RequestSwapOut(RequestSwapOutRequest request, ServerCallContext context)
    {
        var provider = (SwapProvider)(int)request.Provider;
        if (!Enum.IsDefined(provider))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown swap provider {(int)request.Provider}"));

        if (request.AmountSats <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "amount_sats must be > 0"));

        if (request.HasMaxRoutingFeesPercent && request.MaxRoutingFeesPercent is <= 0 or > 100)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "max_routing_fees_percent must be > 0 and <= 100"));

        string? referenceId = null;
        if (request.HasReferenceId)
        {
            if (string.IsNullOrWhiteSpace(request.ReferenceId))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "reference_id must not be empty"));

            referenceId = request.ReferenceId;
            var existing = await _swapOutRepository.GetByReferenceId(referenceId);
            if (existing != null)
            {
                if (existing.NodeId != request.NodeId || existing.Provider != provider || existing.SatsAmount != request.AmountSats ||
                    existing.DestinationWalletId != request.WalletId)
                {
                    throw new RpcException(new Status(StatusCode.AlreadyExists,
                        $"reference_id {referenceId} was already used for swap {existing.Id}, with other parameters"));
                }

                return ToRequestSwapOutResponse(existing);
            }
        }

        var node = (await _nodeRepository.GetAllConfiguredByProvider(provider)).FirstOrDefault(n => n.Id == request.NodeId);
        if (node == null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"Node {request.NodeId} is not a managed node configured for {provider.GetDisplayName()}"));

        var wallet = await _walletRepository.GetById(request.WalletId);
        if (wallet == null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Wallet {request.WalletId} not found"));
        RefuseSparkWallet(wallet);

        var derivationStrategy = wallet.GetDerivationStrategy();
        if (derivationStrategy == null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, $"Wallet {wallet.Id} has no derivation strategy"));

        // Loop and 40swap send to an address given now; a Spark swap reserves its own when it exits
        string? address = null;
        if (provider != SwapProvider.Spark)
        {
            var reserved = await _nbXplorerService.GetUnusedAsync(derivationStrategy, DerivationFeature.Deposit, 0, true,
                context.CancellationToken);
            if (reserved == null)
                throw new RpcException(new Status(StatusCode.Internal, $"Could not reserve a deposit address of wallet {wallet.Id}"));
            address = reserved.Address.ToString();
        }

        var swapRequest = new SwapOutRequest
        {
            Amount = request.AmountSats,
            Address = address,
            MaxRoutingFeesPercent = request.HasMaxRoutingFeesPercent
                ? (decimal)request.MaxRoutingFeesPercent
                : node.MaxSwapRoutingFeeRatio * 100,
            MaxServiceFeesPercent = Constants.SWAP_MAX_SERVICE_FEES_PERCENT,
            MaxMinerFees = Constants.SWAP_MAX_MINER_FEES_SATS,
            SweepConfTarget = Constants.SWEEP_CONF_TARGET,
            PrepayAmtSat = Constants.SWAP_PREPAY_AMOUNT_SATS,
        };

        var swapOut = new SwapOut
        {
            Provider = provider,
            NodeId = node.Id,
            DestinationWalletId = wallet.Id,
            SatsAmount = request.AmountSats,
            IsManual = true,
            ReferenceId = referenceId,
        };

        SwapOutCreation creation;
        try
        {
            creation = await _swapsService.CreateSwapOutAsync(node, swapOut, swapRequest, context.CancellationToken);
        }
        catch (Exception e) when (e is not RpcException and not OperationCanceledException)
        {
            _logger.LogError(e, "Swap-out of {Amount} sats from node {NodeId} with {Provider} failed", request.AmountSats, node.Id, provider);
            var code = e switch
            {
                SparkUnavailableException => StatusCode.Unavailable,
                ArgumentException => StatusCode.InvalidArgument,
                InvalidOperationException => StatusCode.FailedPrecondition,
                _ => StatusCode.Internal
            };
            throw new RpcException(new Status(code, e.Message));
        }

        var auditDetails = new
        {
            NodeId = node.Id,
            NodeName = node.Name,
            Provider = provider.ToString(),
            AmountSats = request.AmountSats,
            DestinationWalletId = wallet.Id,
            DestinationAddress = swapRequest.Address,
            ProviderId = creation.Response.Id,
            ReferenceId = referenceId,
            IsManual = true,
            Source = "gRPC",
            Error = creation.SaveError
        };

        if (!creation.Saved)
        {
            await _auditService.LogAsync(AuditActionType.SwapOutInitiated, AuditEventType.Failure, AuditObjectType.SwapOut,
                creation.Response.Id, auditDetails);
            throw new RpcException(new Status(StatusCode.Internal,
                $"Swap {creation.Response.Id} was created with {provider.GetDisplayName()} but could not be saved: {creation.SaveError}"));
        }

        await _auditService.LogAsync(AuditActionType.SwapOutInitiated, AuditEventType.Success, AuditObjectType.SwapOut,
            creation.Response.Id, auditDetails);

        return ToRequestSwapOutResponse(swapOut);
    }

    public override async Task<GetSwapOutResponse> GetSwapOut(GetSwapOutRequest request, ServerCallContext context)
    {
        var swap = request.SwapCase switch
        {
            GetSwapOutRequest.SwapOneofCase.SwapId => await _swapOutRepository.GetById(request.SwapId),
            GetSwapOutRequest.SwapOneofCase.ReferenceId when !string.IsNullOrWhiteSpace(request.ReferenceId) =>
                await _swapOutRepository.GetByReferenceId(request.ReferenceId),
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "swap_id or reference_id is required"))
        };

        if (swap == null)
            throw new RpcException(new Status(StatusCode.NotFound, "Swap not found"));

        var response = new GetSwapOutResponse
        {
            SwapId = swap.Id,
            Provider = (SWAP_PROVIDER)(int)swap.Provider,
            ProviderId = swap.ProviderId ?? string.Empty,
            Status = (SWAP_OUT_STATUS)(int)swap.Status,
            AmountSats = swap.SatsAmount,
            ServiceFeeSats = swap.ServiceFeeSats ?? 0,
            LightningFeeSats = swap.LightningFeeSats ?? 0,
            OnchainFeeSats = swap.OnChainFeeSats ?? 0,
        };
        if (swap.TxId != null) response.TxId = swap.TxId;
        if (swap.ErrorDetails != null) response.Error = swap.ErrorDetails;
        if (swap.DestinationWalletId != null) response.DestinationWalletId = swap.DestinationWalletId.Value;
        if (swap.DestinationAddress != null) response.DestinationAddress = swap.DestinationAddress;
        if (swap.PaymentHash != null) response.PaymentHash = swap.PaymentHash;
        if (swap.ReferenceId != null) response.ReferenceId = swap.ReferenceId;
        return response;
    }

    private static RequestSwapOutResponse ToRequestSwapOutResponse(SwapOut swap)
    {
        var response = new RequestSwapOutResponse
        {
            SwapId = swap.Id,
            ProviderId = swap.ProviderId ?? string.Empty,
            Status = (SWAP_OUT_STATUS)(int)swap.Status,
            DestinationAddress = swap.DestinationAddress ?? string.Empty,
        };
        if (swap.ErrorDetails != null) response.Error = swap.ErrorDetails;
        return response;
    }

    // ── Spark wallets (SPARK_SIGNER=wallet; temporary) ─────────────────────────────────

    public override Task<CreateSparkWalletResponse> CreateSparkWallet(CreateSparkWalletRequest request, ServerCallContext context) =>
        SparkWalletCallAsync("Creating a Spark wallet", async () =>
        {
            // The mnemonic is shown once, on the Wallets page; never over the API
            var (wallet, _) = await _sparkWallets.CreateAsync(request.Name, request.HasAccount ? request.Account : 0,
                request.HasMaxBalanceSats ? request.MaxBalanceSats : null, context.CancellationToken);
            return new CreateSparkWalletResponse { WalletId = wallet.Id, IdentityPublicKey = wallet.SparkIdentityPublicKey ?? string.Empty };
        });

    public override Task<GetSparkWalletsResponse> GetSparkWallets(GetSparkWalletsRequest request, ServerCallContext context) =>
        SparkWalletCallAsync("Listing the Spark wallets", async () =>
        {
            var response = new GetSparkWalletsResponse();
            foreach (var wallet in await _sparkWallets.ListAsync(request.IncludeArchived, context.CancellationToken))
            {
                var info = new SparkWalletInfo
                {
                    Id = wallet.Id,
                    Name = wallet.Name,
                    IdentityPublicKey = wallet.IdentityPublicKey ?? string.Empty,
                    Account = wallet.Account,
                    EffectiveMaxBalanceSats = wallet.EffectiveMaxBalanceSats,
                    Archived = wallet.IsArchived
                };
                if (wallet.MaxBalanceSats is { } maxBalance) info.MaxBalanceSats = maxBalance;
                info.NodeIds.AddRange(wallet.NodeIds);
                response.Wallets.Add(info);
            }

            return response;
        });

    public override Task<GetSparkWalletBalanceResponse> GetSparkWalletBalance(GetSparkWalletBalanceRequest request,
        ServerCallContext context) =>
        SparkWalletCallAsync("Getting a Spark wallet's balance", async () =>
        {
            var balance = await _sparkWallets.GetBalanceAsync(request.WalletId, context.CancellationToken);
            return new GetSparkWalletBalanceResponse
            {
                OwnedSats = balance.Owned,
                AvailableSats = balance.Available,
                IncomingSats = balance.Incoming,
                FrozenSats = balance.Frozen
            };
        });

    public override Task<WithdrawAllSparkWalletResponse> WithdrawAllSparkWallet(WithdrawAllSparkWalletRequest request,
        ServerCallContext context) =>
        SparkWalletCallAsync("Withdrawing a Spark wallet", async () =>
        {
            var exit = await _sparkWallets.WithdrawAllAsync(request.WalletId, request.DestinationWalletId, context.CancellationToken);
            return new WithdrawAllSparkWalletResponse { TxId = exit.Txid, PayoutSats = exit.PayoutSats, FeeSats = exit.FeeSats };
        });

    public override Task<ArchiveSparkWalletResponse> ArchiveSparkWallet(ArchiveSparkWalletRequest request, ServerCallContext context) =>
        SparkWalletCallAsync("Archiving a Spark wallet", async () =>
        {
            await _sparkWallets.ArchiveAsync(request.WalletId, context.CancellationToken);
            return new ArchiveSparkWalletResponse();
        });

    /// <summary>Runs a Spark wallet action, its refusals mapped to statuses</summary>
    private async Task<T> SparkWalletCallAsync<T>(string action, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (e is not RpcException and not OperationCanceledException)
        {
            var code = e switch
            {
                SparkUnavailableException => StatusCode.Unavailable,
                ArgumentException => StatusCode.InvalidArgument,
                InvalidOperationException => StatusCode.FailedPrecondition,
                _ => StatusCode.Internal
            };
            _logger.LogError(e, "{Action} through gRPC failed", action);
            throw new RpcException(new Status(code, code == StatusCode.Internal ? $"{action} failed" : e.Message));
        }
    }
}
