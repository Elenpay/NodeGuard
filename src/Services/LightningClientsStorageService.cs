using System.Collections.Concurrent;
using Grpc.Net.Client;
using Lnrpc;

namespace FundsManager.Services;

public interface ILightningClientsStorageService
{
    public Lightning.LightningClient GetLightningClient(string? endpoint);
}
public class LightningClientsStorageService: ILightningClientsStorageService
{
    private readonly ILogger<LightningClientsStorageService> _logger;
    private readonly ConcurrentDictionary<string, Lightning.LightningClient> _clients = new();

    public LightningClientsStorageService(ILogger<LightningClientsStorageService> logger)
    {
        _logger = logger;
    }

    private static Lightning.LightningClient CreateLightningClient(string? endpoint)
    {
        //Setup of grpc lnd api client (Lightning.proto)
        //Hack to allow self-signed https grpc calls
        var httpHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        var grpcChannel = GrpcChannel.ForAddress($"https://{endpoint}",
            new GrpcChannelOptions { HttpHandler = httpHandler });

        return new Lightning.LightningClient(grpcChannel);
    }

    public Lightning.LightningClient GetLightningClient(string? endpoint)
    {
        if (endpoint == null)
        {
            throw new ArgumentNullException(nameof(endpoint));
        }

        lock(_clients)
        {
            var found = _clients.TryGetValue(endpoint, out var client);
            if (!found)
            {
                _logger.LogInformation("Client not found for endpoint {endpoint}, creating a new one", endpoint);
                var newClient = CreateLightningClient(endpoint);
                var added = _clients.TryAdd(endpoint, newClient);
                _logger.LogDebug("Client for endpoint {endpoint} was added: {added}", endpoint, added ? "true" : "false");
                return newClient;
            }
            _logger.LogInformation("Client found for endpoint {endpoint}", endpoint);
            return client!;
        }
    }
}