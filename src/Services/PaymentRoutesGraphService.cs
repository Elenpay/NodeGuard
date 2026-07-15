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

using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Services;

// ── Response DTOs (shape kept compatible with the LightningEye frontend) ────────
public record PaymentGraphNode(string Id, bool IsOrigin, List<PaymentGraphNodePayment> Payments, string? Alias = null);

public record PaymentGraphNodePayment(string Id, string Status);

public record PaymentGraphChannel(
    string Id,
    string From,
    string To,
    string PaymentId,
    string PaymentStatus,
    string HopStatus,
    string? FailureCode,
    int AttemptIndex,
    int HopSequence);

public record PaymentGraph(List<PaymentGraphNode> Nodes, List<PaymentGraphChannel> Channels);

/// <summary>
/// Transforms tracked payments and their hops into the { nodes, channels } graph
/// consumed by the route-visualisation frontend. Port of LightningEye's
/// <c>graph_builder.py</c>. Serving surface is expected to be the gRPC API
/// (see nodeguard.proto), not a Blazor page.
/// </summary>
public interface IPaymentRoutesGraphService
{
    Task<PaymentGraph> BuildGraphAsync(string originNodePubKey, DateTimeOffset start, DateTimeOffset end);
}

public class PaymentRoutesGraphService : IPaymentRoutesGraphService
{
    private readonly IPaymentRouteRepository _paymentRouteRepository;

    public PaymentRoutesGraphService(IPaymentRouteRepository paymentRouteRepository)
    {
        _paymentRouteRepository = paymentRouteRepository;
    }

    public async Task<PaymentGraph> BuildGraphAsync(string originNodePubKey, DateTimeOffset start, DateTimeOffset end)
    {
        var payments = await _paymentRouteRepository.GetByCreatedAtRangeAsync(start, end);
        if (payments.Count == 0)
        {
            return EmptyGraph(originNodePubKey);
        }

        var hops = payments.SelectMany(p => p.Hops).ToList();
        return Assemble(originNodePubKey, payments, hops);
    }

    /// <summary>
    /// Per-hop status for a payment. Faithful port of graph_builder._hop_status_for.
    /// <para>dest_pos = hopIndex + 1 (position of the node that RECEIVES this hop);
    /// F = failure_source_index (position in the route that reported the failure).</para>
    /// dest_pos &lt; F → "ok"; == F → "failed_here"; &gt; F → "unreached".
    /// </summary>
    public static (string hopStatus, string? failureCode) HopStatusFor(
        PaymentRouteStatus payStatus, int hopIndex, int? failureSourceIndex, string? code)
    {
        if (payStatus == PaymentRouteStatus.Success)
        {
            return ("success", null);
        }

        // Failed with no idea where → old behaviour (everything red).
        if (failureSourceIndex is null)
        {
            return ("failed", null);
        }

        var destPos = hopIndex + 1;
        var f = failureSourceIndex.Value;

        if (destPos < f) return ("ok", null);
        if (destPos == f) return ("failed_here", code);
        return ("unreached", null);
    }

    // ── Assembly (port of graph_builder._assemble, own-tables source) ───────────
    private static PaymentGraph Assemble(string originId, List<PaymentRoute> payments, List<PaymentRouteHop> hops)
    {
        var payStatus = payments.ToDictionary(p => p.PaymentHash, p => p.Status);

        // ── Nodes ───────────────────────────────────────────────────────────────
        var nodePays = new Dictionary<string, Dictionary<string, PaymentRouteStatus>>
        {
            [originId] = new()
        };
        foreach (var p in payments)
        {
            nodePays[originId][p.PaymentHash] = p.Status;
        }

        foreach (var hop in hops)
        {
            var status = payStatus.GetValueOrDefault(hop.PaymentHash, PaymentRouteStatus.Failed);
            foreach (var nodeId in new[] { hop.FromNode, hop.ToNode })
            {
                if (!nodePays.TryGetValue(nodeId, out var pays))
                {
                    pays = new Dictionary<string, PaymentRouteStatus>();
                    nodePays[nodeId] = pays;
                }
                pays[hop.PaymentHash] = status;
            }
        }

        var nodes = nodePays.Select(kv => new PaymentGraphNode(
            Id: kv.Key,
            IsOrigin: kv.Key == originId,
            Payments: kv.Value.Select(p => new PaymentGraphNodePayment(p.Key, StatusString(p.Value))).ToList()
        )).ToList();

        // ── Channels (edges) ──────────────────────────────────────────────────────
        var seen = new HashSet<(string, ulong, int, int)>();
        var channels = new List<PaymentGraphChannel>();
        foreach (var hop in hops)
        {
            var key = (hop.PaymentHash, hop.ChannelId, hop.AttemptIndex, hop.HopSequence);
            if (!seen.Add(key))
            {
                continue;
            }

            var pStatus = payStatus.GetValueOrDefault(hop.PaymentHash, PaymentRouteStatus.Failed);
            // Own-tables source has no per-hop failure data, so derive from payment status
            // (matches the Python fallback: "success" if success else "failed").
            var hopStatus = pStatus == PaymentRouteStatus.Success ? "success" : "failed";

            channels.Add(new PaymentGraphChannel(
                Id: hop.ChannelId.ToString(),
                From: hop.FromNode,
                To: hop.ToNode,
                PaymentId: hop.PaymentHash,
                PaymentStatus: StatusString(pStatus),
                HopStatus: hopStatus,
                FailureCode: null,
                AttemptIndex: hop.AttemptIndex,
                HopSequence: hop.HopSequence));
        }

        return new PaymentGraph(nodes, channels);
    }

    private static PaymentGraph EmptyGraph(string originId)
        => new(new List<PaymentGraphNode> { new(originId, true, new List<PaymentGraphNodePayment>()) },
               new List<PaymentGraphChannel>());

    private static string StatusString(PaymentRouteStatus status) => status switch
    {
        PaymentRouteStatus.Success => "success",
        _ => "failed"
    };
}
