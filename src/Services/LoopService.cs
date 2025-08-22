using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Looprpc;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;

namespace NodeGuard.Services;

public interface ILoopService
{
    GrpcChannel CreateClient(string endpoint);
    SwapClient.SwapClientClient GetClient(string endpoint);
    Task<bool> PingAsync(Node node, SwapClient.SwapClientClient? client = null);
    Task<SwapResponse> CreateSwapOutAsync(Node node, SwapOutRequest request, SwapClient.SwapClientClient? client = null);
    Task<SwapResponse> GetSwapAsync(Node node, string swapId, SwapClient.SwapClientClient? client = null);
}

public class LoopService : ILoopService
{
    private readonly ILogger<LoopService> _logger;
    private readonly ConcurrentDictionary<string, GrpcChannel> _clients = new();

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
    
    public SwapClient.SwapClientClient GetClient(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Endpoint cannot be null or empty.", nameof(endpoint));
        }

        // Use a thread-safe dictionary to cache clients
        return new SwapClient.SwapClientClient(_clients.GetOrAdd(endpoint, CreateClient(endpoint)));
    }

    public async Task<bool> PingAsync(Node node, SwapClient.SwapClientClient? client = null)
    {
        if (string.IsNullOrEmpty(node.LoopEndpoint) || string.IsNullOrEmpty(node.LoopMacaroon))
        {
            throw new ArgumentException("Node Loop endpoint or macaroon is not set.");
        }

        client ??= GetClient(node.LoopEndpoint);

        // Ping the Loop server to check connectivity
        // var response = client.GetInfo(new GetInfoRequest(),
        // new Metadata
        // {
        //  { "macaroon", node.LoopMacaroon }
        // });

        var response = await client.GetInfoAsync(new GetInfoRequest(),
        new Metadata
        {
            { "macaroon", node.LoopMacaroon }
        });
        if (string.IsNullOrEmpty(response.Version))
        {
            return false;
        }

        return true;
    }

    public async Task<SwapResponse> CreateSwapOutAsync(Node node, SwapOutRequest request, SwapClient.SwapClientClient? client = null)
    {
        if (string.IsNullOrEmpty(node.LoopEndpoint) || string.IsNullOrEmpty(node.LoopMacaroon))
        {
            throw new ArgumentException("Node Loop endpoint or macaroon is not set.");
        }

        client ??= GetClient(node.LoopEndpoint);

        var loopReq = new LoopOutRequest
        {
            Amt = request.Amount,
            Dest = request.Address,
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

        // Request loop out
        _logger.LogInformation("Creating Loop Out request for amount {Amount} to address {Address}", request.Amount, request.Address);
        var response = await client.LoopOutAsync(loopReq);

        return await GetSwapAsync(node, response.IdBytes.ToStringUtf8(), client);
    }

    public async Task<SwapResponse> GetSwapAsync(Node node, string swapId, SwapClient.SwapClientClient? client = null)
    {
        if (string.IsNullOrEmpty(node.LoopEndpoint) || string.IsNullOrEmpty(node.LoopMacaroon))
        {
            throw new ArgumentException("Node Loop endpoint or macaroon is not set.");
        }

        client ??= GetClient(node.LoopEndpoint);

        var response = await client.SwapInfoAsync(new SwapInfoRequest
        {
            Id = ByteString.CopyFromUtf8(swapId),
        });

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
}