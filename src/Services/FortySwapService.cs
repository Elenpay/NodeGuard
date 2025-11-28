/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using System.Collections.Concurrent;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NodeGuard.Data.Models;

namespace NodeGuard.Services;

public interface IFortySwapService
{
    GrpcChannel CreateClient(string endpoint);
    FortySwap.SwapService.SwapServiceClient GetClient(Node node);
    Task<SwapResponse> CreateSwapOutAsync(Node node, SwapOutRequest request, CancellationToken cancellationToken = default);
    Task<SwapResponse> GetSwapAsync(Node node, string swapId, CancellationToken cancellationToken = default);
}

public class FortySwapService : IFortySwapService
{
    private readonly ILogger<FortySwapService> _logger;
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
    private readonly ConcurrentDictionary<string, FortySwap.SwapService.SwapServiceClient> _clients = new();

    public FortySwapService(ILogger<FortySwapService> logger)
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
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        // Create a new gRPC channel for the specified endpoint
        var channel = GrpcChannel.ForAddress($"http://{endpoint}",
            new GrpcChannelOptions
            {
                HttpHandler = handler,
                LoggerFactory = NullLoggerFactory.Instance
            });

        _logger.LogInformation("Creating gRPC client for 40swap server at {Endpoint}", endpoint);

        return channel;
    }
    
    public FortySwap.SwapService.SwapServiceClient GetClient(Node node)
    {
        if (string.IsNullOrEmpty(node.FortySwapEndpoint))
        {
            throw new ArgumentException("Node 40swap endpoint is not set.");
        }

        var clientKey = $"{node.FortySwapEndpoint}";
        return _clients.GetOrAdd(clientKey, _ =>
        {
            var channel = _channels.GetOrAdd(node.FortySwapEndpoint, endpoint => CreateClient(endpoint));
            return new FortySwap.SwapService.SwapServiceClient(channel);
        });
    }

    public async Task<SwapResponse> CreateSwapOutAsync(Node node, SwapOutRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(node.FortySwapEndpoint))
        {
            throw new ArgumentException("Node 40swap endpoint is not set.");
        }
        if (request.Amount <= 0)
        {
            throw new ArgumentException("Swap amount must be greater than zero.");
        }

        if (string.IsNullOrEmpty(request.Address))
        {
            throw new ArgumentException("Destination address must be provided for swap out.");
        }

        var client = GetClient(node);

        var swapReq = new FortySwap.SwapOutRequest
        {
            Chain = FortySwap.Chain.Bitcoin,
            AmountSats = (ulong)request.Amount,
            Address = request.Address,
            MaxRoutingFeePercent = request.MaxRoutingFeesPercent.HasValue ? (float)request.MaxRoutingFeesPercent.Value : 0.5f
        };

        if (request.MaxRoutingFeesPercent.HasValue)
        {
            swapReq.MaxRoutingFeePercent = (float)request.MaxRoutingFeesPercent.Value;
        }

        _logger.LogInformation("Creating 40swap Out request for amount {Amount} to address {Address}", request.Amount, request.Address);
        var response = await client.SwapOutAsync(swapReq, null, null, cancellationToken);

        return await GetSwapAsync(node, response.SwapId, cancellationToken);
    }

    public async Task<SwapResponse> GetSwapAsync(Node node, string swapId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(node.FortySwapEndpoint))
        {
            throw new ArgumentException("Node 40swap endpoint is not set.");
        }

        var client = GetClient(node);

        var response = await client.GetSwapOutAsync(new FortySwap.GetSwapOutRequest
        {
            Id = swapId,
        }, null, null, cancellationToken);

        return new SwapResponse
        {
            Id = System.Text.Encoding.UTF8.GetBytes(response.Id),
            HtlcAddress = string.Empty, // 40swap doesn't expose this directly
            Amount = Money.Coins((decimal)response.InputAmount).Satoshi,
            OffchainFee = (long)response.OffchainFeeSats,
            OnchainFee = (long)response.OnchainFeeSats,
            ServerFee = (long)response.ServiceFeeSats,
            ErrorMessage = response.HasOutcome ? response.Outcome : null,
            Status = MapSwapStatus(response.Status)
        };
    }

    private SwapOutStatus MapSwapStatus(FortySwap.Status status)
    {
        return status switch
        {
            FortySwap.Status.Created => SwapOutStatus.Pending,
            FortySwap.Status.InvoicePaymentIntentReceived => SwapOutStatus.Pending,
            FortySwap.Status.ContractFundedUnconfirmed => SwapOutStatus.Pending,
            FortySwap.Status.ContractFunded => SwapOutStatus.Pending,
            FortySwap.Status.InvoicePaid => SwapOutStatus.Pending,
            FortySwap.Status.ContractClaimedUnconfirmed => SwapOutStatus.Pending,
            FortySwap.Status.Done => SwapOutStatus.Completed,
            FortySwap.Status.ContractRefundedUnconfirmed => SwapOutStatus.Failed,
            FortySwap.Status.ContractExpired => SwapOutStatus.Failed,
            _ => SwapOutStatus.Pending
        };
    }
}
