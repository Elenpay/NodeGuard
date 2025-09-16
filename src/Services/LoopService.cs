using System.Collections.Concurrent;
using Google.Protobuf;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Looprpc;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;

namespace NodeGuard.Services;

public interface ILoopService
{
    GrpcChannel CreateClient(string endpoint);
    SwapClient.SwapClientClient GetClient(Node node);
    Task<bool> PingAsync(Node node, CancellationToken cancellationToken = default);
    Task<SwapResponse> CreateSwapOutAsync(Node node, SwapOutRequest request, CancellationToken cancellationToken= default);
    Task<SwapResponse> GetSwapAsync(Node node, string swapId, CancellationToken cancellationToken= default);
    Task<OutQuoteResponse> LoopOutQuoteAsync(Node node, long amt, int confTarget, CancellationToken cancellationToken = default);
}

public class LoopService : ILoopService
{
    private readonly ILogger<LoopService> _logger;
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
    private readonly ConcurrentDictionary<string, SwapClient.SwapClientClient> _clients = new();

    public LoopService(ILogger<LoopService> logger)
    {
        _logger = logger;
    }

    public GrpcChannel CreateClient(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Endpoint cannot be null or empty.", nameof(endpoint));
        }

        var handler = new HttpClientHandler
        {
            // Accept any server certificate for development purposes
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        // Create a new gRPC channel for the specified endpoint
        var channel = GrpcChannel.ForAddress($"https://{endpoint}",
            new GrpcChannelOptions
            {
                HttpHandler = handler,
                LoggerFactory = NullLoggerFactory.Instance
            });

        _logger.LogInformation("Creating gRPC client for Loop server at {Endpoint}", endpoint);

        return channel;
    }
    
    public SwapClient.SwapClientClient GetClient(Node node)
    {
        if (string.IsNullOrEmpty(node.LoopdEndpoint) || string.IsNullOrEmpty(node.LoopdMacaroon))
        {
            throw new ArgumentException("Node Loopd endpoint or macaroon is not set.");
        }

        var clientKey = $"{node.LoopdEndpoint}";
        return _clients.GetOrAdd(clientKey, _ =>
        {
            var channel = _channels.GetOrAdd(node.LoopdEndpoint, CreateClient(node.LoopdEndpoint));
            var invoker = channel.Intercept(new GRPCMacaroonInterceptor(node.LoopdMacaroon));
            return new SwapClient.SwapClientClient(invoker);
        });
    }

    public async Task<bool> PingAsync(Node node, CancellationToken cancellationToken= default)
    {
        if (string.IsNullOrEmpty(node.LoopdEndpoint) || string.IsNullOrEmpty(node.LoopdMacaroon))
        {
            throw new ArgumentException("Node Loopd endpoint or macaroon is not set.");
        }

        var client = GetClient(node);

        var response = await client.GetInfoAsync(new GetInfoRequest(), null, null, cancellationToken);
        if (string.IsNullOrEmpty(response.Version))
        {
            return false;
        }

        return true;
    }

    public async Task<SwapResponse> CreateSwapOutAsync(Node node, SwapOutRequest request, CancellationToken cancellationToken= default)
    {
        if (string.IsNullOrEmpty(node.LoopdEndpoint) || string.IsNullOrEmpty(node.LoopdMacaroon))
        {
            throw new ArgumentException("Node Loopd endpoint or macaroon is not set.");
        }

        var client = GetClient(node);

        // Denominator for fee rate calculations, representing parts per million (PPM).
        const long FeeRateTotalPartsPPM = 1_000_000;

        // Define route independent max routing fee in satoshis. We have currently no way
        // to get a reliable estimate of the routing fees. Best we can do is
        // the minimum routing fees, which is not very indicative.
        var maxRoutingFeeBaseSats = 10;

        // Rate in parts per million (1%)
        var maxRoutingFeeRatePPM = 10000;

        // CalcFee returns the swap fee for a given swap amount.
        var calcFee = (long amount, long feeBase, long feeRate) => {
            return feeBase + amount * feeRate / FeeRateTotalPartsPPM;
        };

        // Took from loopd/cmd/loop/main.go
        var getMaxRoutingFee = (long amt) => {
            return calcFee(amt, maxRoutingFeeBaseSats, maxRoutingFeeRatePPM);
        };

        var loopReq = new LoopOutRequest
        {
            Amt = request.Amount,
            Dest = request.Address,
            MaxMinerFee = request.MaxMinerFees ?? 0,
            MaxPrepayAmt = request.PrepayAmtSat ?? 0,
            MaxSwapFee = request.MaxServiceFees ?? 0,
            MaxPrepayRoutingFee = request.MaxRoutingFeesPercent != null  ? calcFee(request.PrepayAmtSat ?? 0, maxRoutingFeeBaseSats, request.MaxRoutingFeesPercent.Value * 10000) : getMaxRoutingFee(request.PrepayAmtSat ?? 0),
            MaxSwapRoutingFee = request.MaxRoutingFeesPercent != null ? calcFee(request.Amount, maxRoutingFeeBaseSats, request.MaxRoutingFeesPercent.Value * 10000) : getMaxRoutingFee(request.Amount),
            SweepConfTarget = request.SweepConfTarget,
            HtlcConfirmations = 3,
            SwapPublicationDeadline = (ulong)DateTimeOffset.UtcNow.AddMinutes(request.SwapPublicationDeadlineMinutes).ToUnixTimeSeconds(),
            Label = $"Loop Out {request.Amount} sats on date {DateTime.UtcNow} to {request.Address} via NodeGuard",
            Initiator = "NodeGuard",
        };

        if (request.MaxServiceFees.HasValue)
        {
            loopReq.MaxSwapFee = (long)request.MaxServiceFees.Value;
            _logger.LogDebug("Max fees set to {MaxFees}", request.MaxServiceFees.Value);
        }

        if (request.ChannelsOut != null && request.ChannelsOut.Length > 0)
        {
            loopReq.OutgoingChanSet.AddRange(request.ChannelsOut);
            _logger.LogDebug("Outgoing channels set: {Channels}", string.Join(", ", request.ChannelsOut));
        }

        _logger.LogInformation("Creating Loop Out request for amount {Amount} to address {Address}", request.Amount, request.Address);
        var response = await client.LoopOutAsync(loopReq, null, null, cancellationToken);

        return await GetSwapAsync(node, Convert.ToHexString(response.IdBytes.ToByteArray()).ToLowerInvariant(), cancellationToken);
    }

    public async Task<SwapResponse> GetSwapAsync(Node node, string swapId, CancellationToken cancellationToken= default)
    {
        if (string.IsNullOrEmpty(node.LoopdEndpoint) || string.IsNullOrEmpty(node.LoopdMacaroon))
        {
            throw new ArgumentException("Node Loopd endpoint or macaroon is not set.");
        }

        var client = GetClient(node);

        var response = await client.SwapInfoAsync(new SwapInfoRequest
        {
            Id = ByteString.CopyFrom(Convert.FromHexString(swapId)),
        }, null, null, cancellationToken);

        return new SwapResponse
        {
            Id = response.IdBytes.ToByteArray(),
            HtlcAddress = response.HtlcAddressP2Wsh,
            Amount = response.Amt,
            OffchainFee = response.CostOffchain,
            OnchainFee = response.CostOnchain,
            ServerFee = response.CostServer,
            Status = response.State switch
            {
                SwapState.Initiated => SwapOutStatus.Pending,
                SwapState.InvoiceSettled => SwapOutStatus.Pending,
                SwapState.PreimageRevealed => SwapOutStatus.Pending,
                SwapState.HtlcPublished => SwapOutStatus.Pending,
                SwapState.Failed => SwapOutStatus.Failed,
                SwapState.Success => SwapOutStatus.Completed,
                _ => throw new ArgumentOutOfRangeException()
            }
        };
    }

    public async Task<OutQuoteResponse> LoopOutQuoteAsync(Node node, long amt, int confTarget, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(node.LoopdEndpoint) || string.IsNullOrEmpty(node.LoopdMacaroon))
        {
            throw new ArgumentException("Node Loopd endpoint or macaroon is not set.");
        }

        var client = GetClient(node);

        var quoteRequest = new QuoteRequest
         {
            Amt = amt,
            ConfTarget = 6,
            ExternalHtlc = true,
            Private = false
         };

        _logger.LogInformation("Requesting Loop Out quote for amount {Amount} satoshis", quoteRequest.Amt);
        return await client.LoopOutQuoteAsync(quoteRequest, null, null, cancellationToken);
    }
}