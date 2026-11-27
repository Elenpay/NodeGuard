
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services.Spark;

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
      /// <summary>
      /// The on-chain transaction paying the destination, when the provider reports it (Spark)
      /// </summary>
      public string? TxId { get; set; }
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

      /// <summary>
      /// Moves a pending Spark swap forward (its exit is NodeGuard's to drive) and returns its state
      /// </summary>
      Task<SwapResponse> AdvanceSwapAsync(Node node, SwapOut swap, CancellationToken cancellationToken = default);
      Task<SwapOutQuoteResponse> GetSwapOutQuoteAsync(Node node, SwapProvider provider, SwapOutQuoteRequest request, CancellationToken cancellationToken = default);
   }

   public class SwapsService : ISwapsService
   {
      private readonly ILoopService _loopService;
      private readonly IFortySwapService _fortySwapService;
      private readonly ILightningService _lightningService;
      private readonly ISwapOutRepository _swapOutRepository;
      private readonly ISparkSwapService _sparkSwapService;
      public SwapsService(ILoopService loopService, IFortySwapService fortySwapService, ILightningService lightningService,
         ISwapOutRepository swapOutRepository, ISparkSwapService sparkSwapService)
      {
         _loopService = loopService;
         _fortySwapService = fortySwapService;
         _lightningService = lightningService;
         _swapOutRepository = swapOutRepository;
         _sparkSwapService = sparkSwapService;
      }

      public async Task<SwapOutCreation> CreateSwapOutAsync(Node node, SwapOut swapOut, SwapOutRequest request, CancellationToken cancellationToken = default)
      {
         // Spark records the swap itself, before paying for it
         if (swapOut.Provider == SwapProvider.Spark)
         {
            return await _sparkSwapService.CreateSwapOutAsync(node, swapOut, request, cancellationToken);
         }

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
            SwapProvider.Spark => throw new NotSupportedException("Spark swaps are advanced with AdvanceSwapAsync."),
            _ => throw new NotSupportedException($"Swap provider {provider} is not supported.")
         };
      }

      public Task<SwapResponse> AdvanceSwapAsync(Node node, SwapOut swap, CancellationToken cancellationToken = default)
      {
         return swap.Provider == SwapProvider.Spark
            ? _sparkSwapService.AdvanceSwapAsync(node, swap, cancellationToken)
            : throw new NotSupportedException($"Swap provider {swap.Provider} reports its own progress: use GetSwapAsync.");
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