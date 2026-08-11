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

using Grpc.Core;
using NodeGuard.Data.Models;
using Quartz;

namespace NodeGuard.Helpers;

public static class SubscriptionStreamRunner
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Keeps a long-lived lnd streaming subscription alive for the lifetime of a Quartz job.
    /// lnd's streaming RPCs are live-only with no replay, so any gap between the stream dropping
    /// and us resubscribing loses events permanently. This resubscribes on *any* stream
    /// termination — a thrown error OR a clean end-of-stream (MoveNext -> false, which a graceful
    /// lnd shutdown/release produces) — with exponential backoff, invalidating the cached gRPC
    /// channel on error so a half-open connection is rebuilt on the next attempt. It stops only
    /// when the job is cancelled (NodeGuard shutting down) or <paramref name="getEligibleNode"/>
    /// returns null (the node was removed or is no longer eligible).
    /// </summary>
    /// <param name="getEligibleNode">Fetches the node and returns it if the subscription should
    /// (still) run, or null to stop the worker. Called once per (re)subscription, not per event.</param>
    /// <param name="subscribe">Opens the streaming call for the given node.</param>
    /// <param name="handleEvent">Processes a single received event. It must swallow its own
    /// per-event errors; anything it throws is treated as a stream failure and triggers a
    /// channel invalidation + resubscribe.</param>
    /// <param name="invalidateClient">Evicts the cached gRPC channel for the given endpoint.</param>
    /// <param name="initialBackoff">Delay before the first reconnect attempt (defaults to 2s;
    /// overridable mainly for tests).</param>
    /// <param name="maxBackoff">Upper bound the backoff grows to (defaults to 30s).</param>
    public static async Task RunAsync<TResponse>(
        IJobExecutionContext context,
        ILogger logger,
        string jobName,
        int nodeId,
        Func<Task<Node?>> getEligibleNode,
        Func<Node, AsyncServerStreamingCall<TResponse>> subscribe,
        Func<TResponse, Node, Task> handleEvent,
        Action<string?> invalidateClient,
        TimeSpan? initialBackoff = null,
        TimeSpan? maxBackoff = null)
    {
        var initial = initialBackoff ?? InitialBackoff;
        var max = maxBackoff ?? MaxBackoff;
        var backoff = initial;

        while (!context.CancellationToken.IsCancellationRequested)
        {
            Node? node = null;
            try
            {
                node = await getEligibleNode();
                if (node == null)
                {
                    logger.LogInformation("Node {NodeId} is no longer eligible for {JobName}; stopping worker", nodeId, jobName);
                    return;
                }

                using var stream = subscribe(node);
                while (await stream.ResponseStream.MoveNext(context.CancellationToken))
                {
                    // A received event proves the connection is healthy: reset the backoff.
                    backoff = initial;
                    await handleEvent(stream.ResponseStream.Current, node);
                }

                // MoveNext returned false: lnd closed the stream cleanly (e.g. it is restarting).
                // Fall through to backoff + resubscribe instead of exiting the worker.
                logger.LogWarning("{JobName} stream for node {NodeId} ended; will resubscribe", jobName, nodeId);
            }
            catch (Exception) when (context.CancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("{JobName} for node {NodeId} cancelled (shutdown)", jobName, nodeId);
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error in {JobName} for node {NodeId}; will resubscribe", jobName, nodeId);
                // Drop the (possibly half-open) cached channel so the next attempt rebuilds it.
                invalidateClient(node?.Endpoint);
            }

            try
            {
                await Task.Delay(backoff, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(max.TotalSeconds, backoff.TotalSeconds * 2));
        }
    }
}
