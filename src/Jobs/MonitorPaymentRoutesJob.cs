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
using Grpc.Core;
using Grpc.Net.Client;
using Lnrpc;
using Microsoft.Extensions.Logging.Abstractions;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Routerrpc;

namespace NodeGuard.Jobs;

/// <summary>
/// Long-running listener that keeps one LND payment-tracking stream open per managed node and
/// persists each payment (with its route hops) as LND reports it, for route visualisation.
///
/// <para><b>Why a stream and not polling.</b> This started life as a Quartz job polling
/// <c>ListPayments</c>. That can never see failed HTLC attempts: LND deletes them the moment a
/// payment reaches a terminal state (unless the node runs with
/// <c>--keep-failed-payment-attempts</c>), and the deletion is synchronous with that transition, so
/// no polling interval is fast enough. Measured on a regtest node, a payment that burned 34 failed
/// attempts across four-hop routes reported <c>htlcs = 0</c> from <c>ListPayments</c> immediately
/// afterwards. The router's payment stream delivers the same payment's terminal update with all 34
/// attempts still attached, which is the only place that data is observable.</para>
///
/// <para><b>Why the whole payment is re-persisted per update.</b> Every update carries the
/// payment's complete attempt list, and that list is append-only: verified over a 72-update MPP
/// stream, position <c>i</c> always refers to the same <c>attempt_id</c> and the list never shrank.
/// So <see cref="PaymentRouteHop.AttemptIndex"/> can stay an ordinal into that list, and each
/// update can safely replace the stored hop set rather than having to merge into it.</para>
///
/// <para><b>Coverage is best-effort by construction.</b> The stream only reports payments while we
/// are attached. A payment that reaches a terminal state while NodeGuard is down loses its attempt
/// detail permanently — LND will already have pruned it, so nothing can backfill it later. There is
/// deliberately no <c>ListPayments</c> catch-up sweep here; historical payments predating this
/// service are not imported.</para>
///
/// <para>Fails safe on a fresh/default environment: with no managed nodes (or nodes missing a
/// macaroon/endpoint) no listener is ever started and the service idles.</para>
/// </summary>
public sealed class MonitorPaymentRoutesJob : BackgroundService
{
    /// <summary>How often the set of managed nodes is re-read so listeners follow node changes.</summary>
    private static readonly TimeSpan NodeReconcileInterval = TimeSpan.FromMinutes(1);

    /// <summary>Backoff before re-opening a stream that dropped, so a node that is down does not spin.</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly ILogger<MonitorPaymentRoutesJob> _logger;

    /// <summary>
    /// Repositories are resolved per use from a fresh scope rather than injected. A
    /// <see cref="BackgroundService"/> is a singleton, so injecting them directly captures
    /// non-singleton services and the container's scope validation rejects the whole graph at
    /// startup ("Cannot consume scoped service ... from singleton IHostedService").
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Live listeners by node id — the "managed node =&gt; payment listener" mapping.</summary>
    private readonly ConcurrentDictionary<int, NodeListener> _listeners = new();

    /// <summary>
    /// gRPC channels by node endpoint. Deliberately owned here rather than borrowed from
    /// <c>LightningRouterService</c>: the payment watcher keeps its own connection so a stream that
    /// dies (and the channel eviction that follows) cannot disturb the routing/liquidity code paths
    /// that share that service.
    /// </summary>
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();

    public MonitorPaymentRoutesJob(ILogger<MonitorPaymentRoutesJob> logger,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting {ServiceName}... ", nameof(MonitorPaymentRoutesJob));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileListenersAsync(stoppingToken);
            }
            catch (Exception e)
            {
                // Never let a bad reconcile pass kill the service; the next one retries.
                _logger.LogError(e, "Error reconciling payment route listeners");
            }

            try
            {
                await Task.Delay(NodeReconcileInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await StopAllListenersAsync();
        _logger.LogInformation("{ServiceName} ended", nameof(MonitorPaymentRoutesJob));
    }

    /// <summary>
    /// Brings the live listener set in line with the managed nodes: starts one for every eligible
    /// node that has none, and stops those whose node is gone or no longer reachable.
    /// </summary>
    private async Task ReconcileListenersAsync(CancellationToken stoppingToken)
    {
        List<Node> managedNodes;
        using (var scope = _scopeFactory.CreateScope())
        {
            var nodeRepository = scope.ServiceProvider.GetRequiredService<INodeRepository>();
            managedNodes = await nodeRepository.GetAllManagedByNodeGuard(false);
        }

        var eligible = managedNodes
            // Fail safe: skip anything we can't reach. On a default environment this means no
            // listener is started rather than an error being thrown.
            .Where(n => !string.IsNullOrWhiteSpace(n.ChannelAdminMacaroon) &&
                        !string.IsNullOrWhiteSpace(n.Endpoint))
            .ToList();

        foreach (var node in eligible)
        {
            if (_listeners.TryGetValue(node.Id, out var running))
            {
                // A listener normally runs until cancelled, so a completed one means its loop fell
                // over; drop it here and let the code below start a replacement.
                var faulted = running.Task is { IsCompleted: true };

                // The node's connection details are captured when its stream starts, so an
                // endpoint or macaroon edit has to restart the listener to take effect.
                var reconnectionNeeded = running.Endpoint != node.Endpoint ||
                                         running.Macaroon != node.ChannelAdminMacaroon;

                if (!faulted && !reconnectionNeeded)
                {
                    continue;
                }

                _logger.LogInformation(
                    "Restarting payment listener for node {NodeId} (faulted: {Faulted}, connection changed: {Changed})",
                    node.Id, faulted, reconnectionNeeded);
                _listeners.TryRemove(node.Id, out _);
                await running.StopAsync();
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var listener = new NodeListener(cts, node.Endpoint, node.ChannelAdminMacaroon);
            if (!_listeners.TryAdd(node.Id, listener))
            {
                cts.Dispose();
                continue;
            }

            _logger.LogInformation("Subscribing to payments of node {NodeId} ({NodeName})", node.Id, node.Name);
            listener.Task = ListenToNodeAsync(node, cts.Token);
        }

        var eligibleIds = eligible.Select(n => n.Id).ToHashSet();
        foreach (var (nodeId, listener) in _listeners)
        {
            if (eligibleIds.Contains(nodeId))
            {
                continue;
            }

            _logger.LogInformation("Node {NodeId} is no longer tracked, stopping its payment listener", nodeId);
            _listeners.TryRemove(nodeId, out _);
            await listener.StopAsync();
        }
    }

    /// <summary>
    /// Keeps a payment stream open for one node, re-opening it after any failure until cancelled.
    /// </summary>
    private async Task ListenToNodeAsync(Node node, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConsumePaymentStreamAsync(node, cancellationToken);

                // A clean end of stream is still unexpected while we want to keep watching.
                _logger.LogWarning("Payment stream for node {NodeId} ended, reconnecting", node.Id);
            }
            // Shutting down is not a failure. Grpc.Net surfaces a cancelled call as an RpcException
            // wrapping the OperationCanceledException rather than letting the latter through, so
            // both shapes have to be recognised or every clean stop logs an error.
            catch (Exception e) when (cancellationToken.IsCancellationRequested &&
                                      e is OperationCanceledException or
                                          RpcException { StatusCode: StatusCode.Cancelled })
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Payment stream for node {NodeId} failed. Reconnecting in {Delay}s. Monitoring continues for other nodes",
                    node.Id, ReconnectDelay.TotalSeconds);
            }

            // The channel may be half-open after a stream failure; drop it so the retry dials fresh.
            InvalidateChannel(node.Endpoint);

            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Opens the node's payment stream and persists every update it delivers.
    ///
    /// <para>Uses <c>TrackPayments</c> (all of the node's payments) rather than
    /// <c>TrackPaymentV2</c>, which takes a single <c>payment_hash</c> and so cannot express a
    /// per-node subscription: knowing the hashes up front would require the polling this service
    /// exists to replace.</para>
    ///
    /// <para><c>NoInflightUpdates</c> is set so LND streams only each payment's final update.
    /// Intermediate updates would be discarded anyway — <see cref="HandlePaymentUpdateAsync"/>
    /// persists terminal payments only — and there is nothing to gain by reading them: a payment's
    /// attempt list is cumulative, so the final update already carries every attempt it ever made.
    /// One MPP payment measured on regtest produced 74 intermediate updates against a single
    /// terminal one, all with the same 34 attempts by the end, so suppressing them is a large
    /// reduction in stream volume for no loss of data.</para>
    /// </summary>
    private async Task ConsumePaymentStreamAsync(Node node, CancellationToken cancellationToken)
    {
        // Reconcile only starts listeners for nodes that have both, but assert it here so a node
        // edited to drop its macaroon mid-stream fails loudly on the next reconnect.
        ArgumentException.ThrowIfNullOrWhiteSpace(node.ChannelAdminMacaroon);

        var routerClient = GetRouterClient(node.Endpoint);

        using var stream = routerClient.TrackPayments(
            new TrackPaymentsRequest { NoInflightUpdates = true },
            new Metadata { { "macaroon", node.ChannelAdminMacaroon } },
            cancellationToken: cancellationToken);

        await foreach (var payment in stream.ResponseStream.ReadAllAsync(cancellationToken))
        {
            try
            {
                await HandlePaymentUpdateAsync(node, payment);
            }
            catch (Exception e)
            {
                // One malformed/unsaveable payment must not tear down the whole stream.
                _logger.LogError(e, "Error persisting payment {PaymentHash} of node {NodeId}",
                    payment.PaymentHash, node.Id);
            }
        }
    }

    /// <summary>
    /// Persists one payment update: parses the LND payment into a <see cref="PaymentRoute"/>
    /// (+ hops) and upserts it. Returns true when a new payment row was created.
    ///
    /// <para>Non-terminal statuses (IN_FLIGHT / INITIATED / UNKNOWN) are skipped, as they were
    /// under polling. Nothing is lost by waiting: an update's attempt list is cumulative, so the
    /// terminal update carries every attempt the payment ever made — including the failed ones —
    /// and <see cref="PaymentRouteStatus"/> has no in-flight member to record them under anyway.</para>
    /// </summary>
    public async Task<bool> HandlePaymentUpdateAsync(Node node, Payment raw)
    {
        var paymentRoute = MapToPaymentRoute(node, raw);
        if (paymentRoute == null)
        {
            return false;
        }

        using var scope = _scopeFactory.CreateScope();
        var paymentRouteRepository = scope.ServiceProvider.GetRequiredService<IPaymentRouteRepository>();

        var (inserted, _) = await paymentRouteRepository.UpsertAsync(paymentRoute);
        return inserted;
    }

    /// <summary>
    /// The pure LND-payment → <see cref="PaymentRoute"/> projection, split out from the persistence
    /// so it can be exercised without a container or a database. Returns null for an update that
    /// should not be stored: no payment hash, or a non-terminal status.
    /// </summary>
    public static PaymentRoute? MapToPaymentRoute(Node node, Payment raw)
    {
        var payHash = raw.PaymentHash?.Trim();
        if (string.IsNullOrEmpty(payHash))
        {
            return null;
        }

        var status = PaymentRouteMapping.FromLndPaymentStatus(raw.Status);
        if (status == PaymentRouteStatus.Unknown)
        {
            return null;
        }

        return new PaymentRoute
        {
            PaymentHash = payHash,
            OriginNodePubKey = node.PubKey,
            Status = status,
            CreatedAt = PaymentRouteMapping.CreatedAtFromCreationTimeNs(raw.CreationTimeNs),
            AmountMsat = raw.ValueMsat,
            Destination = ExtractDestination(raw),
            Hops = BuildHops(node, payHash, raw)
        };
    }

    /// <summary>
    /// Flattens every HTLC attempt of a payment into hop rows. The first hop always leaves from our
    /// own node; each subsequent hop starts from the previous destination. Hops without a pubkey or
    /// channel id are skipped.
    ///
    /// <para>Each attempt's outcome and failure detail are denormalised onto its hops so the graph
    /// can distinguish "this hop forwarded fine", "this hop broke" and "never reached" instead of
    /// painting a whole attempt from the payment's final status.</para>
    ///
    /// <para>Note that a payment with no HTLC attempts at all yields no hops — that is the normal
    /// shape for pathfinding-stage failures (NO_ROUTE, INSUFFICIENT_BALANCE), where LND never
    /// dispatched an HTLC and so has no route to report.</para>
    /// </summary>
    private static List<PaymentRouteHop> BuildHops(Node node, string payHash, Payment raw)
    {
        var hops = new List<PaymentRouteHop>();

        for (var attemptIndex = 0; attemptIndex < raw.Htlcs.Count; attemptIndex++)
        {
            var attempt = raw.Htlcs[attemptIndex];
            var route = attempt.Route;
            if (route == null)
            {
                continue;
            }

            var attemptStatus = PaymentRouteMapping.FromLndHtlcStatus(attempt.Status);
            // Singular message field: null whenever the attempt carries no failure detail.
            var failure = attempt.Failure;

            // The first hop always leaves from our node (ORIGIN).
            var prevNode = node.PubKey;

            // seq is the hop's position in LND's route, counted even for hops we skip persisting.
            // It must stay aligned with Failure.failure_source_index, which indexes that same
            // route: PaymentRoutesGraphService.HopStatusFor compares HopSequence + 1 against it to
            // decide which hop broke, so dropping a position here would point the failure at the
            // wrong node for every later hop.
            for (var seq = 0; seq < route.Hops.Count; seq++)
            {
                var hop = route.Hops[seq];
                var toNode = hop.PubKey;
                var channelId = hop.ChanId;
                if (string.IsNullOrEmpty(toNode) || channelId == 0)
                {
                    continue;
                }

                hops.Add(new PaymentRouteHop
                {
                    PaymentHash = payHash,
                    // Ordinal within this payment's attempt list, NOT attempt.AttemptId (a
                    // node-global uint64 that would both overflow int and render as
                    // "attempt 4021" in the UI trace). Safe as an identity across stream updates
                    // because that list is append-only — see the type remarks.
                    AttemptIndex = attemptIndex,
                    HopSequence = seq,
                    ChannelId = channelId,
                    FromNode = prevNode,
                    ToNode = toNode,
                    AmountMsat = hop.AmtToForwardMsat,
                    AttemptStatus = attemptStatus,
                    FailureSourceIndex = failure != null ? (int)failure.FailureSourceIndex : null,
                    FailureCode = PaymentRouteMapping.FailureCodeName(failure)
                });

                prevNode = toNode;
            }
        }

        return hops;
    }

    /// <summary>
    /// The payment's final destination: the last hop of the attempt that actually settled, falling
    /// back to the first attempt with a route when none succeeded (a wholly failed payment still
    /// aimed somewhere).
    ///
    /// <para>Taking the first routed attempt unconditionally would conflate payment with attempt,
    /// the same way <see cref="BuildHops"/> avoids: a payment that failed over one route and
    /// settled over another would report the abandoned route's endpoint.</para>
    /// </summary>
    private static string? ExtractDestination(Payment raw)
    {
        var settled = raw.Htlcs.FirstOrDefault(h =>
            h.Status == HTLCAttempt.Types.HTLCStatus.Succeeded && h.Route?.Hops.Count > 0);

        var chosen = settled ?? raw.Htlcs.FirstOrDefault(h => h.Route?.Hops.Count > 0);

        return chosen?.Route.Hops[^1].PubKey;
    }

    /// <summary>
    /// Router client for a node endpoint, over a channel cached per endpoint. LND serves a
    /// self-signed certificate, hence the permissive validator — same posture as the other LND
    /// clients in the codebase.
    /// </summary>
    private Router.RouterClient GetRouterClient(string? endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var channel = _channels.GetOrAdd(endpoint, ep =>
        {
            var httpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            _logger.LogInformation("New payment watcher grpc channel created for endpoint {Endpoint}", ep);

            return GrpcChannel.ForAddress($"https://{ep}",
                new GrpcChannelOptions { HttpHandler = httpHandler, LoggerFactory = NullLoggerFactory.Instance });
        });

        return new Router.RouterClient(channel);
    }

    /// <summary>
    /// Evicts and disposes the cached channel for an endpoint so the next attempt dials fresh,
    /// rather than reusing a half-open connection that would keep hanging.
    /// </summary>
    private void InvalidateChannel(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !_channels.TryRemove(endpoint, out var channel))
        {
            return;
        }

        try
        {
            channel.Dispose();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error disposing payment watcher grpc channel for endpoint {Endpoint}", endpoint);
        }
    }

    private async Task StopAllListenersAsync()
    {
        foreach (var (nodeId, listener) in _listeners)
        {
            _listeners.TryRemove(nodeId, out _);
            await listener.StopAsync();
        }

        foreach (var endpoint in _channels.Keys)
        {
            InvalidateChannel(endpoint);
        }
    }

    public override void Dispose()
    {
        foreach (var endpoint in _channels.Keys)
        {
            InvalidateChannel(endpoint);
        }

        base.Dispose();
    }

    /// <summary>
    /// One node's stream loop plus the handle used to stop it. Also records the connection details
    /// the loop was started with, so a node edited in the UI can be detected and re-subscribed.
    /// </summary>
    private sealed class NodeListener
    {
        private readonly CancellationTokenSource _cts;

        public NodeListener(CancellationTokenSource cts, string? endpoint, string? macaroon)
        {
            _cts = cts;
            Endpoint = endpoint;
            Macaroon = macaroon;
        }

        public string? Endpoint { get; }
        public string? Macaroon { get; }

        public Task? Task { get; set; }

        public async Task StopAsync()
        {
            await _cts.CancelAsync();

            if (Task != null)
            {
                // The loop swallows its own cancellation, so awaiting here just drains it.
                try
                {
                    await Task;
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown.
                }
            }

            _cts.Dispose();
        }
    }
}
