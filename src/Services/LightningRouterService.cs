// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
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
            { HttpHandler = httpHandler, LoggerFactory = NullLoggerFactory.Instance });

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
        RouteFeeResponse? routeFeeResponse = null;
        try
        {
            client ??= GetRouterClient(node.Endpoint);
            routeFeeResponse = await client.EstimateRouteFeeAsync(routeFeeRequest,
                new Metadata
                {
                    {
                        "macaroon", node.ChannelAdminMacaroon
                    }
                });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while estimating route fee for node {NodeId}", node.Id);
            return null;
        }

        return routeFeeResponse;
    }
}

