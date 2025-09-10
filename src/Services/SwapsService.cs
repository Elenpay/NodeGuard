
using NodeGuard.Data.Models;

namespace NodeGuard.Services
{
   public class SwapOutRequest
   {
      public long Amount { get; set; }
      public string? Address { get; set; }
      public long? MaxFees { get; set; }
      public ulong[]? ChannelsOut { get; set; }
      public ulong? RoutingFeeLimitPPM { get; set; }
   }

   public class SwapResponse
   {
      public required byte[] Id { get; set; }
      public required string HtlcAddress { get; set; }
      public long Amount { get; set; }
      public long OffchainFee { get; set; }
      public long OnchainFee { get; set; }
      public long ServerFee { get; set; }
      public SwapOutStatus Status { get; set; }
   }

   public class SwapOutQuoteRequest
   {
      public long Amount { get; set; }
   }

   public class SwapOutQuoteResponse
   {
      public long ServiceFees { get; set; }
      public long OffChainFees { get; set; }
      public long OnChainFees { get; set; }
      public bool CouldEstimateRoutingFees { get; set; } = false;
   }

   public interface ISwapsService
   {
      Task<SwapResponse> CreateSwapOutAsync(Node node, SwapProvider provider, SwapOutRequest request);
      Task<SwapResponse> GetSwapAsync(Node node, SwapProvider provider, string swapId);
      Task<SwapOutQuoteResponse> GetSwapOutQuoteAsync(Node node, SwapProvider provider, SwapOutQuoteRequest request);
   }

   public class SwapsService : ISwapsService
   {
      private readonly ILoopService _loopService;
      private readonly ILightningService _lightningService;
      public SwapsService(ILoopService loopService, ILightningService lightningService)
      {
         _loopService = loopService;
         _lightningService = lightningService;
      }

      public async Task<SwapResponse> CreateSwapOutAsync(Node node, SwapProvider provider, SwapOutRequest request)
      {
         return provider switch
         {
            SwapProvider.Loop => await _loopService.CreateSwapOutAsync(node, request),
            _ => throw new NotSupportedException($"Swap provider {provider} is not supported.")
         };
      }

      public async Task<SwapResponse> GetSwapAsync(Node node, SwapProvider provider, string swapId)
      {
         return provider switch
         {
            SwapProvider.Loop => await _loopService.GetSwapAsync(node, swapId),
            _ => throw new NotSupportedException($"Swap provider {provider} is not supported.")
         };
      }

      public async Task<SwapOutQuoteResponse> GetSwapOutQuoteAsync(Node node, SwapProvider provider, SwapOutQuoteRequest request)
      {
         return provider switch
         {
            SwapProvider.Loop => await GetLoopQuoteAsync(node, request),
            _ => throw new NotSupportedException($"Swap provider {provider} is not supported.")
         };
      }

      private async Task<SwapOutQuoteResponse> GetLoopQuoteAsync(Node node, SwapOutQuoteRequest request)
      {
         var quoteRequest = new Looprpc.QuoteRequest
         {
            Amt = request.Amount,
            ConfTarget = 6
         };

         var loopResponse = await _loopService.LoopOutQuoteAsync(node, quoteRequest);

         var quoute = new SwapOutQuoteResponse
         {
            ServiceFees = loopResponse.SwapFeeSat,
            OnChainFees = loopResponse.HtlcSweepFeeSat
         };

         var lnResponse = await _lightningService.EstimateRouteFee(node.PubKey, request.Amount, null, 30);
         ArgumentNullException.ThrowIfNull(lnResponse, nameof(lnResponse));
         
         if (lnResponse.FailureReason != Lnrpc.PaymentFailureReason.FailureReasonNone)
         {
            quoute.CouldEstimateRoutingFees = false;
            quoute.OffChainFees = 0;
         }
         else
         {
            quoute.CouldEstimateRoutingFees = true;
            quoute.OffChainFees = lnResponse.RoutingFeeMsat / 1000;
         }

         return quoute;
      }
   }
}