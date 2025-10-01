
using NodeGuard.Data.Models;

namespace NodeGuard.Services
{
   public class SwapOutRequest
   {
      public long Amount { get; set; }
      public string? Address { get; set; }
      public long? MaxServiceFees { get; set; }
      public long? MaxMinerFees { get; set; }
      public ulong[]? ChannelsOut { get; set; }
      public decimal? MaxRoutingFeesPercent { get; set; }
      public long? PrepayAmtSat { get; set; }
      public int SwapPublicationDeadlineMinutes { get; set; } = 60;
      public int SweepConfTarget { get; set; } = 400;
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
      public int ConfTarget { get; set; }
   }

   public class SwapOutQuoteResponse
   {
      public long ServiceFees { get; set; }
      public long OffChainFees { get; set; }
      public long OnChainFees { get; set; }
      public bool CouldEstimateRoutingFees { get; set; } = false;
      public long HtlcSweepFeeSat { get; set; }
      public long PrepayAmtSat { get; set; }
   }

   public interface ISwapsService
   {
      Task<SwapResponse> CreateSwapOutAsync(Node node, SwapProvider provider, SwapOutRequest request, CancellationToken cancellationToken = default);
      Task<SwapResponse> GetSwapAsync(Node node, SwapProvider provider, string swapId, CancellationToken cancellationToken = default);
      Task<SwapOutQuoteResponse> GetSwapOutQuoteAsync(Node node, SwapProvider provider, SwapOutQuoteRequest request, CancellationToken cancellationToken = default);
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

      public async Task<SwapResponse> CreateSwapOutAsync(Node node, SwapProvider provider, SwapOutRequest request, CancellationToken cancellationToken = default)
      {
         return provider switch
         {
            SwapProvider.Loop => await _loopService.CreateSwapOutAsync(node, request, cancellationToken),
            _ => throw new NotSupportedException($"Swap provider {provider} is not supported.")
         };
      }

      public async Task<SwapResponse> GetSwapAsync(Node node, SwapProvider provider, string swapId, CancellationToken cancellationToken = default)
      {
         return provider switch
         {
            SwapProvider.Loop => await _loopService.GetSwapAsync(node, swapId, cancellationToken),
            _ => throw new NotSupportedException($"Swap provider {provider} is not supported.")
         };
      }

      public async Task<SwapOutQuoteResponse> GetSwapOutQuoteAsync(Node node, SwapProvider provider, SwapOutQuoteRequest request, CancellationToken cancellationToken = default)
      {
         return provider switch
         {
            SwapProvider.Loop => await GetLoopQuoteAsync(node, request, cancellationToken),
            _ => throw new NotSupportedException($"Swap provider {provider} is not supported.")
         };
      }

      private async Task<SwapOutQuoteResponse> GetLoopQuoteAsync(Node node, SwapOutQuoteRequest request, CancellationToken cancellationToken)
      {
         var loopResponse = await _loopService.LoopOutQuoteAsync(node, request.Amount, request.ConfTarget, cancellationToken);

         var quote = new SwapOutQuoteResponse
         {
            ServiceFees = loopResponse.SwapFeeSat,
            OnChainFees = loopResponse.HtlcSweepFeeSat,
            HtlcSweepFeeSat = loopResponse.HtlcSweepFeeSat,
            PrepayAmtSat = loopResponse.PrepayAmtSat
         };

         var lnResponse = await _lightningService.EstimateRouteFee(node.PubKey, request.Amount, null, 30);
         ArgumentNullException.ThrowIfNull(lnResponse, nameof(lnResponse));
         
         if (lnResponse.FailureReason != Lnrpc.PaymentFailureReason.FailureReasonNone)
         {
            quote.CouldEstimateRoutingFees = false;
            quote.OffChainFees = 0;
         }
         else
         {
            quote.CouldEstimateRoutingFees = true;
            quote.OffChainFees = lnResponse.RoutingFeeMsat / 1000;
         }

         return quote;
      }
   }
}