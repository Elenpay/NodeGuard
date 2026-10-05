
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Services
{
   public class SwapOutRequest
   {
      public long Amount { get; set; }
      public string? Address { get; set; }
      public decimal? MaxServiceFeesPercent { get; set; }
      public long? MaxMinerFees { get; set; }
      public ulong[]? ChannelsOut { get; set; }
      public decimal? MaxRoutingFeesPercent { get; set; }
      public long? PrepayAmtSat { get; set; }
      public int SwapPublicationDeadlineMinutes { get; set; } = 60;
      public int SweepConfTarget { get; set; } = 400;
   }

   public class SwapResponse
   {
      public required string Id { get; set; }
      public required string HtlcAddress { get; set; }
      /// <summary>
      /// Hex payment hash of the Lightning payment that funds the swap, when the provider exposes it
      /// </summary>
      public string? PaymentHash { get; set; }
      public long Amount { get; set; }
      public long OffchainFee { get; set; }
      public long OnchainFee { get; set; }
      public long ServerFee { get; set; }
      public SwapOutStatus Status { get; set; }
      public string? ErrorMessage { get; set; }
   }

   /// <summary>
   /// A swap-out created with its provider, and whether its record could be saved
   /// </summary>
   public sealed record SwapOutCreation(SwapResponse Response, bool Saved, string? SaveError);

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
      /// <summary>
      /// Creates the swap with <paramref name="swapOut"/>'s provider and records <paramref name="swapOut"/>, filled with
      /// the provider's id, status, fees and payment hash and the request's destination address. Check
      /// <see cref="SwapOutCreation.Saved"/>: the provider may have the swap even when the record could not be saved.
      /// </summary>
      Task<SwapOutCreation> CreateSwapOutAsync(Node node, SwapOut swapOut, SwapOutRequest request, CancellationToken cancellationToken = default);
      Task<SwapResponse> GetSwapAsync(Node node, SwapProvider provider, string swapId, CancellationToken cancellationToken = default);
      Task<SwapOutQuoteResponse> GetSwapOutQuoteAsync(Node node, SwapProvider provider, SwapOutQuoteRequest request, CancellationToken cancellationToken = default);
   }

   public class SwapsService : ISwapsService
   {
      private readonly ILoopService _loopService;
      private readonly IFortySwapService _fortySwapService;
      private readonly ILightningService _lightningService;
      private readonly ISwapOutRepository _swapOutRepository;
      public SwapsService(ILoopService loopService, IFortySwapService fortySwapService, ILightningService lightningService,
         ISwapOutRepository swapOutRepository)
      {
         _loopService = loopService;
         _fortySwapService = fortySwapService;
         _lightningService = lightningService;
         _swapOutRepository = swapOutRepository;
      }

      public async Task<SwapOutCreation> CreateSwapOutAsync(Node node, SwapOut swapOut, SwapOutRequest request, CancellationToken cancellationToken = default)
      {
         var response = swapOut.Provider switch
         {
            SwapProvider.Loop => await _loopService.CreateSwapOutAsync(node, request, cancellationToken),
            SwapProvider.FortySwap => await _fortySwapService.CreateSwapOutAsync(node, request, cancellationToken),
            _ => throw new NotSupportedException($"Swap provider {swapOut.Provider} is not supported.")
         };

         swapOut.ProviderId = response.Id;
         swapOut.Status = response.Status;
         swapOut.ServiceFeeSats = response.ServerFee;
         swapOut.OnChainFeeSats = response.OnchainFee;
         swapOut.LightningFeeSats = response.OffchainFee;
         swapOut.DestinationAddress = request.Address;
         swapOut.PaymentHash = response.PaymentHash;

         var (saved, error) = await _swapOutRepository.AddAsync(swapOut);
         return new SwapOutCreation(response, saved, error);
      }

      public async Task<SwapResponse> GetSwapAsync(Node node, SwapProvider provider, string swapId, CancellationToken cancellationToken = default)
      {
         return provider switch
         {
            SwapProvider.Loop => await _loopService.GetSwapAsync(node, swapId, cancellationToken),
            SwapProvider.FortySwap => await _fortySwapService.GetSwapAsync(node, swapId, cancellationToken),
            _ => throw new NotSupportedException($"Swap provider {provider} is not supported.")
         };
      }

      public async Task<SwapOutQuoteResponse> GetSwapOutQuoteAsync(Node node, SwapProvider provider, SwapOutQuoteRequest request, CancellationToken cancellationToken = default)
      {
         return provider switch
         {
            SwapProvider.Loop => await GetLoopQuoteAsync(node, request, cancellationToken),
            SwapProvider.FortySwap => throw new NotSupportedException("Quote is not supported for 40swap provider."),
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
         
         if (lnResponse == null || lnResponse.FailureReason != Lnrpc.PaymentFailureReason.FailureReasonNone)
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