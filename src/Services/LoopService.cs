using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Looprpc;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using NSubstitute.Extensions;

namespace NodeGuard.Services;

public interface ILoopService
{
    GrpcChannel CreateClient(string endpoint);
    SwapClient.SwapClientClient GetClient(Node node);
    Task<bool> PingAsync(Node node, CancellationToken cancellationToken = default);
    Task<SwapResponse> CreateSwapOutAsync(Node node, SwapOutRequest request, CancellationToken cancellationToken= default);
    Task<SwapResponse> GetSwapAsync(Node node, string swapId, CancellationToken cancellationToken= default);
    Task<OutQuoteResponse> LoopOutQuoteAsync(Node node, QuoteRequest request, CancellationToken cancellationToken = default);
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
        if (string.IsNullOrEmpty(node.LoopEndpoint) || string.IsNullOrEmpty(node.LoopMacaroon))
        {
            throw new ArgumentException("Node Loop endpoint or macaroon is not set.");
        }

        var clientKey = $"{node.LoopEndpoint}";
        return _clients.GetOrAdd(clientKey, _ =>
        {
            var channel = _channels.GetOrAdd(node.LoopEndpoint, CreateClient(node.LoopEndpoint));
            var invoker = channel.Intercept(new GRPCMacaroonInterceptor(node.LoopMacaroon));
            return new SwapClient.SwapClientClient(invoker);
        });
    }

    public async Task<bool> PingAsync(Node node, CancellationToken cancellationToken= default)
    {
        if (string.IsNullOrEmpty(node.LoopEndpoint) || string.IsNullOrEmpty(node.LoopMacaroon))
        {
            throw new ArgumentException("Node Loop endpoint or macaroon is not set.");
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
        if (string.IsNullOrEmpty(node.LoopEndpoint) || string.IsNullOrEmpty(node.LoopMacaroon))
        {
            throw new ArgumentException("Node Loop endpoint or macaroon is not set.");
        }

        var client = GetClient(node);

        var loopReq = new LoopOutRequest
        {
            Amt = request.Amount,
            Dest = request.Address,
            MaxPrepayAmt = 5000, // Satoshis
        };

        if (request.MaxFees.HasValue)
        {
            loopReq.MaxSwapFee = request.MaxFees.Value;
            _logger.LogDebug("Max fees set to {MaxFees}", request.MaxFees.Value);
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
        if (string.IsNullOrEmpty(node.LoopEndpoint) || string.IsNullOrEmpty(node.LoopMacaroon))
        {
            throw new ArgumentException("Node Loop endpoint or macaroon is not set.");
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

    public async Task<OutQuoteResponse> LoopOutQuoteAsync(Node node, QuoteRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(node.LoopEndpoint) || string.IsNullOrEmpty(node.LoopMacaroon))
        {
            throw new ArgumentException("Node Loop endpoint or macaroon is not set.");
        }

        var client = GetClient(node);

        _logger.LogInformation("Requesting Loop Out quote for amount {Amount} satoshis", request.Amt);
        var response = await client.LoopOutQuoteAsync(request, null, null, cancellationToken);

        return response;
    }
}