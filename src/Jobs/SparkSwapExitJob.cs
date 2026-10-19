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
using NodeGuard.Helpers;
using NodeGuard.Services;
using NodeGuard.Services.Spark;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// Exits paid Spark swaps on-chain, every SPARK_EXIT_INTERVAL_MINUTES. Paid swaps wait in their Spark wallet until
/// it is full, then exit together: one cooperative exit per destination wallet, of exactly the leaves their own
/// payments brought in, to one new address. Exits go strictly one at a time, oldest first, each step saved before the
/// next (paid, then leaves attributed, then exit sent, then payout confirmed). MonitorSwapsJob follows a Spark swap
/// until its payment settles; from then on the swap is this job's.
/// </summary>
[DisallowConcurrentExecution]
public class SparkSwapExitJob : IJob
{
    private readonly ILogger<SparkSwapExitJob> _logger;
    private readonly ISwapOutRepository _swapOutRepository;
    private readonly ISparkSwapService _sparkSwapService;
    private readonly IAuditService _auditService;
    private readonly SparkSettings _sparkSettings;

    public SparkSwapExitJob(ILogger<SparkSwapExitJob> logger, ISwapOutRepository swapOutRepository,
        ISparkSwapService sparkSwapService, IAuditService auditService, SparkSettings sparkSettings)
    {
        _logger = logger;
        _swapOutRepository = swapOutRepository;
        _sparkSwapService = sparkSwapService;
        _auditService = auditService;
        _sparkSettings = sparkSettings;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        if (!_sparkSettings.Enabled) return;

        var ct = context.CancellationToken;
        try
        {
            var pending = await _swapOutRepository.GetAllPending();
            var swaps = pending
                .Where(s => s.Provider == SwapProvider.Spark && s.LightningFeeSats is not null)
                .OrderBy(s => s.CreationDatetime)
                .ThenBy(s => s.Id)
                .ToList();
            if (swaps.Count == 0) return;

            _logger.LogInformation("Starting {JobName}: {Count} paid Spark swaps", nameof(SparkSwapExitJob), swaps.Count);

            // Every swap gets its own transfer's leaves before any exit, so no exit takes leaves another swap is due
            foreach (var swap in swaps.Where(s => s.SparkTransferId is null && s.TxId is null))
            {
                await RunAsync($"Spark swap {swap.Id}", "attributing its transfer", () => _sparkSwapService.AttributeAsync(swap, ct));
            }

            // Batches already exiting, the swaps sharing a reserved address: their payout is followed, or their exit
            // retried with exactly them
            var exiting = swaps
                .Where(s => s.DestinationAddress is not null)
                .GroupBy(s => (s.SparkIdentity, s.DestinationWalletId, s.DestinationAddress))
                .Select(g => (IReadOnlyList<SwapOut>)g.ToList())
                .ToList();
            foreach (var batch in exiting)
            {
                await ExitAsync(batch, ct);
            }

            // New batches: the paid swaps of each Spark wallet that is full
            var planned = await RunAsync("Spark swaps", "planning their exits", () => _sparkSwapService.PlanExitsAsync(pending, ct));
            foreach (var batch in planned ?? [])
            {
                await ExitAsync(batch, ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(SparkSwapExitJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(SparkSwapExitJob));
    }

    private async Task ExitAsync(IReadOnlyList<SwapOut> batch, CancellationToken ct)
    {
        var label = batch.Count == 1 ? $"Spark swap {batch[0].Id}" : $"Spark swaps {string.Join(", ", batch.Select(s => s.Id))}";
        var step = await RunAsync(label, "exiting", () => _sparkSwapService.ExitBatchAsync(batch, ct));
        if (step != SparkExitStep.Completed) return;

        foreach (var swap in batch)
        {
            await AuditCompletionAsync(swap);
        }
    }

    /// <summary>Runs one step; a failure is logged and leaves its swaps for the next run.</summary>
    private async Task<T?> RunAsync<T>(string swaps, string what, Func<Task<T>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "{Swaps}: error {What}; retried on the next run", swaps, what);
            return default;
        }
    }

    private Task AuditCompletionAsync(SwapOut swap) =>
        _auditService.LogSystemAsync(
            AuditActionType.SwapOutCompleted,
            AuditEventType.Success,
            AuditObjectType.SwapOut,
            swap.ProviderId,
            new
            {
                SwapId = swap.Id,
                NodeId = swap.NodeId,
                Provider = swap.Provider.ToString(),
                ProviderId = swap.ProviderId,
                AmountSats = swap.SatsAmount,
                PayoutSats = swap.PayoutSats,
                TotalFeeSats = swap.TotalFees.Satoshi,
                ServiceFeeSats = swap.ServiceFeeSats,
                LightningFeeSats = swap.LightningFeeSats,
                OnChainFeeSats = swap.OnChainFeeSats,
                SparkTransferId = swap.SparkTransferId,
                TxId = swap.TxId,
                IsManual = swap.IsManual,
                OldStatus = SwapOutStatus.Pending.ToString(),
                NewStatus = SwapOutStatus.Completed.ToString()
            });
}
