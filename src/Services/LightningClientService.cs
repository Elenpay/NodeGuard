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
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using NBitcoin;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using Channel = NodeGuard.Data.Models.Channel;

namespace NodeGuard.Services;

public interface ILightningClientService
{
    public Lightning.LightningClient GetLightningClient(string? endpoint);

    /// <summary>
    /// List channels from a specific node
    /// </summary>
    /// <param name="node"></param>
    /// <param name="client"></param>
    /// <returns></returns>
    public Task<ListChannelsResponse?> ListChannels(Node node, Lightning.LightningClient? client = null);

    /// <summary>
    /// Closes a channel
    /// </summary>
    /// <param name="node"></param>
    /// <param name="channel"></param>
    /// <param name="forceClose"></param>
    /// <param name="client"></param>
    /// <returns></returns>
    public AsyncServerStreamingCall<CloseStatusUpdate>? CloseChannel(Node node, Channel channel, bool forceClose = false, Lightning.LightningClient? client = null);

    public AsyncServerStreamingCall<ChannelEventUpdate> SubscribeChannelEvents(Node node, Lightning.LightningClient? client = null);
    public Task<LightningNode?> GetNodeInfo(Node node, string pubKey, Lightning.LightningClient? client = null);
    public Task ConnectToPeer(Node node, string peerPubKey, Lightning.LightningClient? client = null);
    public AsyncServerStreamingCall<OpenStatusUpdate> OpenChannel(Node node, OpenChannelRequest openChannelRequest, Lightning.LightningClient? client = null);
    public void FundingStateStep(Node node, PSBT finalizedPSBT, byte[] pendingChannelId, Lightning.LightningClient? client = null);
}

public class LightningClientService : ILightningClientService
{
    private readonly ILogger<LightningClientService> _logger;
    private readonly ConcurrentDictionary<string, GrpcChannel> _clients = new();

    public LightningClientService(ILogger<LightningClientService> logger)
    {
        _logger = logger;
    }

    private static GrpcChannel CreateLightningClient(string? endpoint)
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

        return grpcChannel;
    }

    public Lightning.LightningClient GetLightningClient(string? endpoint)
    {
        if (endpoint == null)
        {
            throw new ArgumentNullException(nameof(endpoint));
        }

        // Atomic operation for TryGetValue and TryAdd
        lock (_clients)
        {
            var found = _clients.TryGetValue(endpoint, out var client);
            if (!found)
            {
                _logger.LogInformation("Client not found for endpoint {endpoint}, creating a new one", endpoint);
                var newClient = CreateLightningClient(endpoint);
                var added = _clients.TryAdd(endpoint, newClient);
                _logger.LogDebug("Client for endpoint {endpoint} was added: {added}", endpoint, added ? "true" : "false");
                return new Lightning.LightningClient(newClient);
            }

            _logger.LogInformation("Client found for endpoint {endpoint}", endpoint);
            return new Lightning.LightningClient(client);
        }
    }

    public async Task<ListChannelsResponse?> ListChannels(Node node, Lightning.LightningClient? client = null)
    {
        //This method is here to avoid a circular dependency between the LightningService and the ChannelRepository
        ListChannelsResponse? listChannelsResponse = null;
        try
        {
            client ??= GetLightningClient(node.Endpoint);
            listChannelsResponse = await client.ListChannelsAsync(new ListChannelsRequest(),
                new Metadata
                {
                    {
                        "macaroon", node.ChannelAdminMacaroon
                    }
                });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while listing channels for node {NodeId}", node.Id);
            return null;
        }

        return listChannelsResponse;
    }

    public AsyncServerStreamingCall<CloseStatusUpdate>? CloseChannel(Node node, Channel channel, bool forceClose = false, Lightning.LightningClient? client = null)
    {
        //This method is here to avoid a circular dependency between the LightningService and the ChannelRepository
        AsyncServerStreamingCall<CloseStatusUpdate>? closeChannelResponse = null;
        try
        {
            client ??= GetLightningClient(node.Endpoint);
            return client.CloseChannel(new CloseChannelRequest
            {
                ChannelPoint = new ChannelPoint
                {
                    FundingTxidStr = channel.FundingTx,
                    OutputIndex = channel.FundingTxOutputIndex
                },
                Force = forceClose,
            }, new Metadata { { "macaroon", node.ChannelAdminMacaroon } });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while listing channels for node {NodeId}", node.Id);
        }

        return null;
    }

    public AsyncServerStreamingCall<ChannelEventUpdate> SubscribeChannelEvents(Node node, Lightning.LightningClient? client = null)
    {
        client ??= GetLightningClient(node.Endpoint);
        return client.SubscribeChannelEvents(new ChannelEventSubscription(),
            new Metadata
            {
                { "macaroon", node.ChannelAdminMacaroon }
            });
    }

    public async Task<LightningNode?> GetNodeInfo(Node node, string pubKey, Lightning.LightningClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(pubKey))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(pubKey));

        client ??= GetLightningClient(node.Endpoint);
        try
        {
            if (node.ChannelAdminMacaroon != null)
            {
                var nodeInfo = await client.GetNodeInfoAsync(new NodeInfoRequest
                {
                    PubKey = pubKey,
                    IncludeChannels = false
                }, new Metadata { { "macaroon", node.ChannelAdminMacaroon } });

                return nodeInfo?.Node;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while obtaining node info for node with pubkey: {PubKey}", pubKey);
        }

        return null;
    }

    public async Task ConnectToPeer(Node node, string peerPubKey, Lightning.LightningClient? client = null)
    {
        client ??= GetLightningClient(node.Endpoint);
        var isPeerAlreadyConnected = false;

        ConnectPeerResponse connectPeerResponse = null;
        try
        {
            var nodeInfo = await GetNodeInfo(node, peerPubKey);

            //For now, we only rely on pure tcp IPV4 connections
            var addr = nodeInfo?.Addresses.FirstOrDefault(x => x.Network == "tcp")?.Addr;

            connectPeerResponse = await client.ConnectPeerAsync(new ConnectPeerRequest
            {
                Addr = new LightningAddress { Host = addr, Pubkey = nodeInfo.PubKey },
                Perm = true
            }, new Metadata
            {
                { "macaroon", node.ChannelAdminMacaroon }
            });
        }
        //We avoid to stop the method if the peer is already connected
        catch (RpcException e)
        {
            if (e.Message.Contains("is not online"))
            {
                throw new PeerNotOnlineException($"$peer {peerPubKey} is not online");
            }

            if (!e.Message.Contains("already connected to peer"))
            {
                throw;
            }

            isPeerAlreadyConnected = true;
        }

        if (connectPeerResponse != null || isPeerAlreadyConnected)
        {
            if (isPeerAlreadyConnected)
            {
                _logger.LogInformation("Peer: {Pubkey} already connected", peerPubKey);
            }
            else
            {
                _logger.LogInformation("Peer connected to {Pubkey}", peerPubKey);
            }
        }
        else
        {
            _logger.LogError("Error, peer not connected to {Pubkey}", peerPubKey);
            throw new InvalidOperationException();
        }
    }

    public AsyncServerStreamingCall<OpenStatusUpdate> OpenChannel(Node node, OpenChannelRequest openChannelRequest, Lightning.LightningClient? client = null)
    {
        client ??= GetLightningClient(node.Endpoint);
        return client.OpenChannel(openChannelRequest,
            new Metadata { { "macaroon", node.ChannelAdminMacaroon } }
        );
    }

    public void FundingStateStep(Node node, PSBT finalizedPSBT, byte[] pendingChannelId, Lightning.LightningClient? client = null)
    {
        client ??= GetLightningClient(node.Endpoint);
        client.FundingStateStep(
            new FundingTransitionMsg
            {
                PsbtVerify = new FundingPsbtVerify
                {
                    FundedPsbt =
                        ByteString.CopyFrom(
                            Convert.FromHexString(finalizedPSBT.ToHex())),
                    PendingChanId = ByteString.CopyFrom(pendingChannelId)
                }
            }, new Metadata { { "macaroon", node.ChannelAdminMacaroon } });
    }
}