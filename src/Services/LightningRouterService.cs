using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using Routerrpc;

namespace NodeGuard.Services;

public interface ILightningRouterService
{
    public Router.RouterClient GetRouterClient(string? endpoint);
    public Task<RouteFeeResponse?> EstimateRouteFee(Node node, RouteFeeRequest routeFeeRequest, Router.RouterClient? client = null);
}

public class LightningRouterService : ILightningRouterService
{
    private readonly ILogger<LightningRouterService> _logger;
    private readonly ConcurrentDictionary<string, GrpcChannel> _clients = new();

    public LightningRouterService(ILogger<LightningRouterService> logger)
    {
        _logger = logger;
    }

    private GrpcChannel CreateRouterClient(string? endpoint)
    {
        var httpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        var grpcChannel = GrpcChannel.ForAddress($"https://{endpoint}",
            new GrpcChannelOptions
                {HttpHandler = httpHandler, LoggerFactory = NullLoggerFactory.Instance});

        _logger.LogInformation("New grpc channel created for router endpoint {endpoint}", endpoint);

        return grpcChannel;
    }

    public Router.RouterClient GetRouterClient(string? endpoint)
    {
        if (endpoint == null)
        {
            throw new ArgumentNullException(nameof(endpoint));
        }

        lock (_clients)
        {
            var found = _clients.TryGetValue(endpoint, out var client);
            if (!found)
            {
                _logger.LogInformation("Router client not found for endpoint {endpoint}, creating a new one", endpoint);
                var newClient = CreateRouterClient(endpoint);
                var added = _clients.TryAdd(endpoint, newClient);
                _logger.LogDebug("Router client for endpoint {endpoint} was added: {added}", endpoint, added ? "true" : "false");
                return new Router.RouterClient(newClient);
            }

            _logger.LogInformation("Router client found for endpoint {endpoint}", endpoint);
            return new Router.RouterClient(client);
        }
    }

    public async Task<RouteFeeResponse?> EstimateRouteFee(Node node, RouteFeeRequest routeFeeRequest, Router.RouterClient? client = null)
    {
        CustomArgumentNullException.ThrowIfNull(node.ChannelAdminMacaroon, nameof(node.ChannelAdminMacaroon), "LND Macaroon for {NodeName} is not well configured", node.Name);

        client ??= GetRouterClient(node.Endpoint);

        return await client.EstimateRouteFeeAsync(routeFeeRequest,
            new Metadata
            {
                {
                    "macaroon", node.ChannelAdminMacaroon
                }
            });
    }
}

