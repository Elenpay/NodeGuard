
using NodeGuard.Data.Models;

namespace NodeGuard.Services
{
   public class SwapOutRequest
   {
      public long Amount { get; set; }
      public string Address { get; set; }
      public long? MaxFees { get; set; }
      public ulong[]? ChannelsOut { get; set; }
      public ulong? RoutingFeeLimitPPM { get; set; }
   }

   public class SwapResponse
   {
      public byte[] Id { get; set; }
      public string HtlcAddress { get; set; }
      public long Amount { get; set; }
      public long OffchainFee { get; set; }
      public long OnchainFee { get; set; }
      public long ServerFee { get; set; }
      public SwapOutStatus Status { get; set; }
   }

   public interface ISwapsService
   {
      Task<SwapResponse> CreateSwapOutAsync(Node node, SwapProvider provider, SwapOutRequest request);
      Task<SwapResponse> GetSwapAsync(Node node, SwapProvider provider, string swapId);
   }

   public class SwapsService : ISwapsService
   {
      private readonly ILoopService _loopService;

      public SwapsService(ILoopService loopService)
      {
         _loopService = loopService;
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
   }
}