using AutoMapper;
using FundsManager.Data.Repositories;
using FundsManager.Data.Repositories.Interfaces;
using FundsManager.Helpers;
using Google.Protobuf.Collections;
using Grpc.Core;
using NBitcoin.RPC;
using NBXplorer.DerivationStrategy;
using Nodeguard;

namespace FundsManager.rpc;

/// <summary>
/// gRPC Server implementation of the NodeGuard API
/// </summary>
public class NodeGuardService : Nodeguard.NodeGuardService.NodeGuardServiceBase
{
    private readonly ILogger<NodeGuardService> _logger;
    private readonly ILiquidityRuleRepository _liquidityRuleRepository;
    private readonly IWalletRepository _walletRepository;
    private readonly IMapper _mapper;

    public NodeGuardService(ILogger<NodeGuardService> logger,
        ILiquidityRuleRepository liquidityRuleRepository,
        IWalletRepository walletRepository,
        IMapper mapper)
    {
        _logger = logger;
        _liquidityRuleRepository = liquidityRuleRepository;
        _walletRepository = walletRepository;
        _mapper = mapper;
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

        var (network,nbxplorer) = LightningHelper.GenerateNetwork();

        var btcAddress = await nbxplorer.GetUnusedAsync(wallet.GetDerivationStrategy(), DerivationFeature.Deposit, 0, false);
        
        if(btcAddress == null)
        {
            _logger.LogError("Error getting new address for wallet with id {walletId}", request.WalletId);
            throw new RpcException(new Status(StatusCode.Internal, "Error getting new address for wallet"));
        }

        var getNewWalletAddressResponse = new GetNewWalletAddressResponse()
        {
            Address = btcAddress.ScriptPubKey.GetDestinationAddress(network).ToString()
        };
        
        return getNewWalletAddressResponse;


    }
}