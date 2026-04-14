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

using System.Numerics;
using Lnrpc;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Quartz;
using Routerrpc;

namespace NodeGuard.Jobs;

[DisallowConcurrentExecution]
/// <summary>
/// Long-lived per-node worker that subscribes to lnd HTLC events and persists
/// the forwarding lifecycle for events relevant to routing analytics.
///
/// lnd classifies HTLC events at two levels:
/// - <c>EventType</c> is the coarse direction/category (Send, Receive, Forward).
/// - <c>EventCase</c> is the protobuf oneof payload that is actually populated
///   for that event instance (for example ForwardEvent, SettleEvent, or LinkFailEvent).
///
/// We need both because <c>Forward</c> only tells us this HTLC belongs to the
/// forwarding path, while the concrete oneof case tells us which lifecycle step
/// or terminal outcome data is present on the message.
/// </summary>
public class NodeHtlcSubscribeJob : IJob
{
    private readonly ILogger<NodeHtlcSubscribeJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly ILightningClientService _lightningClientService;
    private readonly ILightningRouterService _lightningRouterService;
    private readonly IForwardingHtlcEventRepository _forwardingHtlcEventRepository;

    public NodeHtlcSubscribeJob(ILogger<NodeHtlcSubscribeJob> logger,
        INodeRepository nodeRepository,
        ILightningClientService lightningClientService,
        ILightningRouterService lightningRouterService,
        IForwardingHtlcEventRepository forwardingHtlcEventRepository)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _lightningClientService = lightningClientService;
        _lightningRouterService = lightningRouterService;
        _forwardingHtlcEventRepository = forwardingHtlcEventRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.JobDetail.JobDataMap;
        var nodeId = data.GetInt("nodeId");
        _logger.LogInformation("Starting {JobName} for node {NodeId}... ", nameof(NodeHtlcSubscribeJob), nodeId);

        try
        {
            var node = await _nodeRepository.GetById(nodeId, false);
            if (!IsNodeEligible(node))
            {
                _logger.LogInformation("Node {NodeId} is not eligible for HTLC monitoring", nodeId);
                return;
            }

            var stream = _lightningRouterService.SubscribeHtlcEvents(node!);
            while (await stream.ResponseStream.MoveNext(context.CancellationToken))
            {
                node = await _nodeRepository.GetById(nodeId, false);

                try
                {
                    var htlcEvent = stream.ResponseStream.Current;
                    var eventToPersist = MapForwardingEvent(node!, htlcEvent);
                    if (eventToPersist == null)
                    {
                        continue;
                    }

                    await EnrichForwardingEventAsync(node!, eventToPersist);

                    var addResult = await _forwardingHtlcEventRepository.UpsertAsync(eventToPersist);
                    if (!addResult.Item1)
                    {
                        _logger.LogWarning("HTLC event was not persisted for node {NodeId}: {Error}", nodeId, addResult.Item2);
                        continue;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error processing HTLC event for node {NodeId}. Event will be skipped", nodeId);
                    continue;
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while subscribing HTLC events for node {NodeId}", nodeId);
            await Task.Delay(5000);
            throw new JobExecutionException(e, true);
        }

        _logger.LogInformation("{JobName} ended", nameof(NodeHtlcSubscribeJob));
    }

    private static bool IsNodeEligible(Node? node)
    {
        return node != null &&
               !node.IsNodeDisabled &&
               !string.IsNullOrWhiteSpace(node.Endpoint) &&
               !string.IsNullOrWhiteSpace(node.ChannelAdminMacaroon);
    }

    /// <summary>
    /// Maps an incoming router HTLC event into the persistence model only when it is relevant
    /// for forwarded HTLC lifecycle tracking.
    ///
    /// Selection rules:
    /// 1. Event must be classified as FORWARD.
    /// 2. Event case must be a lifecycle event we persist (forward/settle/fail).
    ///
    /// In lnd's API these checks serve different purposes:
    /// - <c>EventType</c> filters to forwarded HTLCs instead of send/receive events.
    /// - <c>EventCase</c> tells us which protobuf oneof payload is populated, so we know
    ///   whether the message carries forward-progress data, settlement data, or failure data.
    ///
    /// Rationale:
    /// - ForwardEvent seeds amount/fee context so later sparse failures can still be analyzed.
    /// - Terminal outcomes finalize the same deduped HTLC row.
    /// - lnd can replay events after reconnects, so persisted records are deduped/upserted by HTLC key.
    /// </summary>
    /// <param name="node">Managed node owning the subscription stream.</param>
    /// <param name="htlcEvent">Raw event received from SubscribeHtlcEvents.</param>
    /// <returns>
    /// A populated <see cref="ForwardingHtlcEvent"/> for persisted FORWARD lifecycle events; otherwise <c>null</c>.
    /// </returns>
    internal static ForwardingHtlcEvent? MapForwardingEvent(Node node, HtlcEvent htlcEvent)
    {
        if (htlcEvent.EventType != HtlcEvent.Types.EventType.Forward)
        {
            return null;
        }

        if (!ShouldPersistEvent(htlcEvent.EventCase))
        {
            return null;
        }

        var info = htlcEvent.ForwardEvent?.Info ?? htlcEvent.LinkFailEvent?.Info;

        return new ForwardingHtlcEvent
        {
            ManagedNodePubKey = node.PubKey,
            ManagedNodeName = node.Name,
            EventTimestamp = MapEventTimestamp(htlcEvent.TimestampNs),
            IncomingChannelId = htlcEvent.IncomingChannelId,
            OutgoingChannelId = htlcEvent.OutgoingChannelId,
            IncomingHtlcId = htlcEvent.IncomingHtlcId,
            OutgoingHtlcId = htlcEvent.OutgoingHtlcId,
            EventType = MapEventType(htlcEvent.EventType),
            EventCase = MapEventCase(htlcEvent.EventCase),
            Outcome = MapOutcome(htlcEvent),
            IncomingTimelock = info?.IncomingTimelock,
            OutgoingTimelock = info?.OutgoingTimelock,
            IncomingAmountMsat = info?.IncomingAmtMsat,
            OutgoingAmountMsat = info?.OutgoingAmtMsat,
            FeeMsat = CalculateNetFeeMsat(info?.IncomingAmtMsat, info?.OutgoingAmtMsat),
            WireFailureCode = htlcEvent.LinkFailEvent != null ? (int)htlcEvent.LinkFailEvent.WireFailure : null,
            FailureDetail = htlcEvent.LinkFailEvent != null ? (int)htlcEvent.LinkFailEvent.FailureDetail : null,
            FailureString = htlcEvent.LinkFailEvent?.FailureString,
        };
    }

    /// <summary>
    /// Adds best-effort alias and policy-derived fee metadata to a forwarding event.
    ///
    /// Amounts come from the HTLC event stream itself, while aliases and routing policy
    /// details require separate graph lookups. Missing graph data should not block event
    /// persistence, so enrichment is intentionally partial and non-fatal.
    /// </summary>
    private async Task EnrichForwardingEventAsync(Node node, ForwardingHtlcEvent forwardingHtlcEvent)
    {
        var incomingChannelInfo = await GetChannelInfoAsync(node, forwardingHtlcEvent.IncomingChannelId);
        var outgoingChannelInfo = await GetChannelInfoAsync(node, forwardingHtlcEvent.OutgoingChannelId);

        await EnrichPeerAliasesAsync(node, forwardingHtlcEvent, incomingChannelInfo, outgoingChannelInfo);

        var feeMsat = forwardingHtlcEvent.FeeMsat;
        if (feeMsat == null)
        {
            return;
        }

        var incomingPolicy = incomingChannelInfo == null ? null : GetNodePolicy(node, incomingChannelInfo);
        var outgoingPolicy = outgoingChannelInfo == null ? null : GetNodePolicy(node, outgoingChannelInfo);
        ApplyFeeBreakdown(forwardingHtlcEvent, incomingPolicy, outgoingPolicy);
    }

    private async Task<ChannelEdge?> GetChannelInfoAsync(Node node, ulong channelId)
    {
        if (channelId == 0)
        {
            return null;
        }

        var channelInfo = await _lightningClientService.GetChanInfo(node, channelId);
        if (channelInfo == null)
        {
            _logger.LogDebug("Channel info lookup failed for node {NodeId} and channel {ChannelId}", node.Id, channelId);
        }

        return channelInfo;
    }

    private async Task EnrichPeerAliasesAsync(Node node,
        ForwardingHtlcEvent forwardingHtlcEvent,
        ChannelEdge? incomingChannelInfo,
        ChannelEdge? outgoingChannelInfo)
    {
        var aliasByPubKey = new Dictionary<string, string?>();

        var incomingPeerPubKey = GetRemotePeerPubKey(node, incomingChannelInfo);
        forwardingHtlcEvent.IncomingPeerAlias = await ResolvePeerAliasAsync(node, incomingPeerPubKey, aliasByPubKey);

        var outgoingPeerPubKey = GetRemotePeerPubKey(node, outgoingChannelInfo);
        forwardingHtlcEvent.OutgoingPeerAlias = await ResolvePeerAliasAsync(node, outgoingPeerPubKey, aliasByPubKey);
    }

    private async Task<string?> ResolvePeerAliasAsync(Node node,
        string? peerPubKey,
        IDictionary<string, string?> aliasByPubKey)
    {
        if (string.IsNullOrWhiteSpace(peerPubKey))
        {
            return null;
        }

        if (aliasByPubKey.TryGetValue(peerPubKey, out var cachedAlias))
        {
            return cachedAlias;
        }

        var peer = await _lightningClientService.GetNodeInfo(node, peerPubKey);
        aliasByPubKey[peerPubKey] = peer?.Alias;
        return peer?.Alias;
    }

    internal static string? GetRemotePeerPubKey(Node node, ChannelEdge? channelInfo)
    {
        if (channelInfo == null)
        {
            return null;
        }

        if (channelInfo.Node1Pub == node.PubKey)
        {
            return channelInfo.Node2Pub;
        }

        if (channelInfo.Node2Pub == node.PubKey)
        {
            return channelInfo.Node1Pub;
        }

        return null;
    }

    private RoutingPolicy? GetNodePolicy(Node node, ChannelEdge channelInfo)
    {
        if (channelInfo.Node1Pub == node.PubKey)
        {
            return channelInfo.Node1Policy;
        }

        if (channelInfo.Node2Pub == node.PubKey)
        {
            return channelInfo.Node2Policy;
        }

        _logger.LogWarning("Unable to match node {NodeId} pubkey to channel policy for channel {ChannelId}", node.Id, channelInfo.ChannelId);
        return null;
    }

    /// <summary>
    /// Filters to the protobuf oneof payloads that represent the lifecycle of a forwarded HTLC.
    ///
    /// This is intentionally narrower than checking <c>EventType == Forward</c>: some forward-classified
    /// messages are stream metadata or informational markers, while only these oneof cases carry
    /// the amount/outcome data we want to persist.
    /// </summary>
    private static bool ShouldPersistEvent(HtlcEvent.EventOneofCase eventCase)
    {
        return eventCase == HtlcEvent.EventOneofCase.ForwardEvent ||
               eventCase == HtlcEvent.EventOneofCase.SettleEvent ||
               eventCase == HtlcEvent.EventOneofCase.ForwardFailEvent ||
               eventCase == HtlcEvent.EventOneofCase.LinkFailEvent;
    }

    /// <summary>
    /// Computes the fee breakdown fields persisted on a forwarding event.
    ///
    /// <c>FeeMsat</c> is the effective fee implied by the observed HTLC amounts,
    /// <c>GrossFeeMsat</c> is the monitored channel's outbound policy fee, and
    /// <c>InboundFeeMsat</c> is stored as the residual contribution after subtracting
    /// the outbound component from the effective total fee.
    /// </summary>
    internal static void ApplyFeeBreakdown(ForwardingHtlcEvent forwardingHtlcEvent, RoutingPolicy? incomingPolicy, RoutingPolicy? outgoingPolicy)
    {
        forwardingHtlcEvent.FeeMsat = CalculateNetFeeMsat(
            forwardingHtlcEvent.IncomingAmountMsat,
            forwardingHtlcEvent.OutgoingAmountMsat);

        forwardingHtlcEvent.GrossFeeMsat = CalculateGrossFeeMsat(
            forwardingHtlcEvent.OutgoingAmountMsat,
            outgoingPolicy);

        forwardingHtlcEvent.InboundFeeMsat = CalculateInboundFeeMsat(
            forwardingHtlcEvent.FeeMsat,
            forwardingHtlcEvent.GrossFeeMsat);

        forwardingHtlcEvent.RoutingFeePpm = outgoingPolicy?.FeeRateMilliMsat;
        forwardingHtlcEvent.InboundFeePpm = incomingPolicy?.InboundFeeRateMilliMsat;
    }

    /// <summary>
    /// Calculates the effective fee carried by the HTLC from the observed amount delta.
    ///
    /// This is the most direct value available from the event stream because lnd already
    /// folded the node's full routing policy effects into the incoming and outgoing amounts.
    /// <see cref="BigInteger"/> is used to keep the subtraction overflow-safe before casting.
    /// </summary>
    internal static long? CalculateNetFeeMsat(ulong? incomingAmountMsat, ulong? outgoingAmountMsat)
    {
        if (incomingAmountMsat == null || outgoingAmountMsat == null)
        {
            return null;
        }

        var difference = new BigInteger(incomingAmountMsat.Value) - new BigInteger(outgoingAmountMsat.Value);
        if (difference < long.MinValue || difference > long.MaxValue)
        {
            return null;
        }

        return (long)difference;
    }

    /// <summary>
    /// Calculates the monitored node's outbound forwarding fee from its routing policy.
    ///
    /// This mirrors lnd's fee formula for the outgoing side of a forward: base fee plus
    /// proportional fee on the outgoing amount. <see cref="BigInteger"/> avoids overflow in
    /// the intermediate multiplication before the result is bounded back to <see cref="long"/>.
    /// </summary>
    internal static long? CalculateGrossFeeMsat(ulong? outgoingAmountMsat, RoutingPolicy? outgoingPolicy)
    {
        if (outgoingAmountMsat == null || outgoingPolicy == null)
        {
            return null;
        }

        var baseFee = new BigInteger(outgoingPolicy.FeeBaseMsat);
        var proportionalFee = new BigInteger(outgoingAmountMsat.Value) * new BigInteger(outgoingPolicy.FeeRateMilliMsat) / 1_000_000;
        var grossFee = baseFee + proportionalFee;

        if (grossFee < long.MinValue || grossFee > long.MaxValue)
        {
            return null;
        }

        return (long)grossFee;
    }

    /// <summary>
    /// Stores the inbound fee contribution as the residual between the effective fee seen
    /// on the HTLC and the monitored node's outbound policy fee.
    /// </summary>
    internal static long? CalculateInboundFeeMsat(long? feeMsat, long? grossFeeMsat)
    {
        if (feeMsat == null || grossFeeMsat == null)
        {
            return null;
        }

        return feeMsat.Value - grossFeeMsat.Value;
    }

    internal static DateTimeOffset MapEventTimestamp(ulong timestampNs)
    {
        return DateTimeOffset.UnixEpoch.AddTicks(checked((long)(timestampNs / 100)));
    }

    private static HtlcEventType MapEventType(HtlcEvent.Types.EventType eventType)
    {
        return eventType switch
        {
            HtlcEvent.Types.EventType.Send => HtlcEventType.Send,
            HtlcEvent.Types.EventType.Receive => HtlcEventType.Receive,
            HtlcEvent.Types.EventType.Forward => HtlcEventType.Forward,
            _ => HtlcEventType.Unknown
        };
    }

    private static HtlcEventCase MapEventCase(HtlcEvent.EventOneofCase eventCase)
    {
        return eventCase switch
        {
            HtlcEvent.EventOneofCase.ForwardEvent => HtlcEventCase.ForwardEvent,
            HtlcEvent.EventOneofCase.ForwardFailEvent => HtlcEventCase.ForwardFailEvent,
            HtlcEvent.EventOneofCase.SettleEvent => HtlcEventCase.SettleEvent,
            HtlcEvent.EventOneofCase.LinkFailEvent => HtlcEventCase.LinkFailEvent,
            HtlcEvent.EventOneofCase.SubscribedEvent => HtlcEventCase.SubscribedEvent,
            HtlcEvent.EventOneofCase.FinalHtlcEvent => HtlcEventCase.FinalHtlcEvent,
            _ => HtlcEventCase.Unknown
        };
    }

    private static ForwardingOutcome MapOutcome(HtlcEvent htlcEvent)
    {
        if (htlcEvent.SettleEvent != null)
        {
            return ForwardingOutcome.Settled;
        }

        if (htlcEvent.ForwardFailEvent != null || htlcEvent.LinkFailEvent != null)
        {
            return ForwardingOutcome.Failed;
        }

        return ForwardingOutcome.Unknown;
    }
}