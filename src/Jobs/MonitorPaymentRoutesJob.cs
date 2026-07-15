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

using Lnrpc;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NodeGuard.Services;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Polls each managed node's outbound payments via LND's <c>ListPayments</c> gRPC and
/// persists new ones (with their route hops) for route visualisation. Port of
/// LightningEye's <c>PaymentTracker</c> (app/services/tracker.py).
///
/// <para>The Python tracker held its <c>index_offset</c> cursor in memory (reset on
/// restart, re-scanned from 0). Quartz jobs are stateless per execution and the
/// <see cref="PaymentRoute"/> entity has no cursor column, so this job paginates from
/// <c>index_offset = 0</c> every run and relies on
/// <see cref="IPaymentRouteRepository.InsertIfNewAsync"/> for idempotency — behaviour
/// identical to the original.</para>
///
/// <para>Fails safe on a fresh/default environment: with no managed nodes (or nodes
/// missing a macaroon/endpoint) the loop body never runs and the job is a no-op.</para>
/// </summary>
[DisallowConcurrentExecution]
public class MonitorPaymentRoutesJob : IJob
{
    private const int MaxPaymentsPerPage = 100;

    private readonly ILogger<MonitorPaymentRoutesJob> _logger;
    private readonly INodeRepository _nodeRepository;
    private readonly ILightningClientService _lightningClientService;
    private readonly IPaymentRouteRepository _paymentRouteRepository;

    public MonitorPaymentRoutesJob(ILogger<MonitorPaymentRoutesJob> logger,
        INodeRepository nodeRepository,
        ILightningClientService lightningClientService,
        IPaymentRouteRepository paymentRouteRepository)
    {
        _logger = logger;
        _nodeRepository = nodeRepository;
        _lightningClientService = lightningClientService;
        _paymentRouteRepository = paymentRouteRepository;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("Starting {JobName}... ", nameof(MonitorPaymentRoutesJob));
        try
        {
            var managedNodes = await _nodeRepository.GetAllManagedByNodeGuard(false);

            foreach (var node in managedNodes)
            {
                // Fail safe: skip anything we can't reach. On a default environment this
                // means the job does nothing rather than erroring.
                if (string.IsNullOrWhiteSpace(node.ChannelAdminMacaroon) ||
                    string.IsNullOrWhiteSpace(node.Endpoint))
                {
                    continue;
                }

                try
                {
                    await TrackNodePaymentsAsync(node);
                }
                catch (Exception ex)
                {
                    // One node failing must not abort the rest (mirror of MonitorSwapsJob).
                    _logger.LogError(ex,
                        "Unexpected error while tracking payment routes for node {NodeId}. Monitoring will continue for other nodes",
                        node.Id);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(MonitorPaymentRoutesJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(MonitorPaymentRoutesJob));
    }

    /// <summary>
    /// Port of tracker.py <c>_poll</c>: paginates ListPayments by index_offset from 0,
    /// persisting each new terminal payment until a page comes back empty.
    /// </summary>
    private async Task TrackNodePaymentsAsync(Node node)
    {
        ulong indexOffset = 0;
        var savedTotal = 0;

        while (true)
        {
            var request = new ListPaymentsRequest
            {
                IndexOffset = indexOffset,
                MaxPayments = MaxPaymentsPerPage,
                Reversed = false,
                // Matches the Python default: LND won't return IN_FLIGHT/INITIATED payments.
                IncludeIncomplete = false
            };

            var response = await _lightningClientService.ListPayments(node, request);
            // The ListPayments wrapper returns null on error; don't NRE, just stop this node.
            if (response == null || response.Payments.Count == 0)
            {
                break;
            }

            foreach (var payment in response.Payments)
            {
                if (await SavePaymentAsync(node, payment))
                {
                    savedTotal++;
                }
            }

            // Advance the cursor for the next page (port of last_index_offset handling).
            var newIndex = response.LastIndexOffset;
            if (newIndex <= indexOffset)
            {
                break;
            }
            indexOffset = newIndex;
        }

        if (savedTotal > 0)
        {
            _logger.LogInformation("Saved {Count} new payment route(s) for node {NodeId}", savedTotal, node.Id);
        }
    }

    /// <summary>
    /// Port of tracker.py <c>_save_payment</c>: parses one LND payment into a
    /// <see cref="PaymentRoute"/> (+ hops) and inserts it if new. Returns true when a new
    /// payment was persisted. Non-terminal statuses (IN_FLIGHT / INITIATED / UNKNOWN) are
    /// skipped, exactly as the Python tracker ignored anything but SUCCEEDED/FAILED.
    /// </summary>
    private async Task<bool> SavePaymentAsync(Node node, Payment raw)
    {
        var payHash = raw.PaymentHash?.Trim();
        if (string.IsNullOrEmpty(payHash))
        {
            return false;
        }

        var status = PaymentRouteMapping.FromLndPaymentStatus(raw.Status);
        if (status == PaymentRouteStatus.Unknown)
        {
            return false;
        }

        var paymentRoute = new PaymentRoute
        {
            PaymentHash = payHash,
            OriginNodePubKey = node.PubKey,
            Status = status,
            CreatedAt = PaymentRouteMapping.CreatedAtFromCreationTimeNs(raw.CreationTimeNs),
            AmountMsat = raw.ValueMsat,
            Destination = ExtractDestination(raw),
            Hops = BuildHops(node, payHash, raw)
        };

        var (inserted, _) = await _paymentRouteRepository.InsertIfNewAsync(paymentRoute);
        return inserted;
    }

    /// <summary>
    /// Port of tracker.py <c>_save_hops</c> applied over every HTLC attempt. The first hop
    /// always leaves from our own node; each subsequent hop starts from the previous
    /// destination. Hops without a pubkey or channel id are skipped.
    /// </summary>
    private static List<PaymentRouteHop> BuildHops(Node node, string payHash, Payment raw)
    {
        var hops = new List<PaymentRouteHop>();

        foreach (var attempt in raw.Htlcs)
        {
            var route = attempt.Route;
            if (route == null)
            {
                continue;
            }

            // The first hop always leaves from our node (ORIGIN).
            var prevNode = node.PubKey;
            var seq = 0;

            foreach (var hop in route.Hops)
            {
                var toNode = hop.PubKey;
                var channelId = hop.ChanId;
                if (string.IsNullOrEmpty(toNode) || channelId == 0)
                {
                    continue;
                }

                hops.Add(new PaymentRouteHop
                {
                    PaymentHash = payHash,
                    AttemptIndex = (int)attempt.AttemptId,
                    HopSequence = seq,
                    ChannelId = channelId,
                    FromNode = prevNode,
                    ToNode = toNode,
                    AmountMsat = hop.AmtToForwardMsat
                });

                prevNode = toNode;
                seq++;
            }
        }

        return hops;
    }

    /// <summary>
    /// Port of tracker.py <c>_extract_destination</c>: the pubkey of the final hop of the
    /// first attempt that has a route.
    /// </summary>
    private static string? ExtractDestination(Payment raw)
    {
        foreach (var htlc in raw.Htlcs)
        {
            var routeHops = htlc.Route?.Hops;
            if (routeHops is { Count: > 0 })
            {
                return routeHops[^1].PubKey;
            }
        }

        return null;
    }
}
