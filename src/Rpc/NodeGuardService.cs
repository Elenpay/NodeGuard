using AutoMapper;
using FundsManager.Data.Models;
using FundsManager.Data.Repositories;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using FundsManager.Jobs;
using FundsManager.Services;
using Grpc.Core;
using NBitcoin;
using NBXplorer.DerivationStrategy;
using Nodeguard;
using Quartz;
using LiquidityRule = Nodeguard.LiquidityRule;
using Wallet = FundsManager.Data.Models.Wallet;

namespace FundsManager.Rpc;

public interface INodeGuardService
{
    Task<GetLiquidityRulesResponse> GetLiquidityRules(GetLiquidityRulesRequest request,
        ServerCallContext context);

    Task<GetNewWalletAddressResponse> GetNewWalletAddress(GetNewWalletAddressRequest request,
        ServerCallContext context);

    Task<RequestWithdrawalResponse> RequestWithdrawal(RequestWithdrawalRequest request, ServerCallContext context);

    Task<GetAvailableWalletsResponse> GetAvailableWallets(GetAvailableWalletsRequest request, ServerCallContext context);
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
    private readonly IScheduler _scheduler;

    public NodeGuardService(ILogger<NodeGuardService> logger,
        ILiquidityRuleRepository liquidityRuleRepository,
        IWalletRepository walletRepository,
        IMapper mapper,
        IWalletWithdrawalRequestRepository walletWithdrawalRequestRepository,
        IBitcoinService bitcoinService,
        INBXplorerService nbXplorerService,
        ISchedulerFactory schedulerFactory
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
                LiquidityRules = { liquidityRules.Select(x => _mapper.Map<LiquidityRule>(x)).ToList() }
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
}