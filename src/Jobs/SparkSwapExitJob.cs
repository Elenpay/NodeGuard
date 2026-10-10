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
/// Exits paid Spark swaps on-chain, every SPARK_EXIT_INTERVAL_MINUTES: each swap exits the leaves its own payment
/// brought in, to its own destination address. Swaps go strictly one at a time, oldest first, each step saved
/// before the next (paid, then leaves attributed, then exit sent, then payout confirmed).
/// MonitorSwapsJob follows a Spark swap until its payment settles; from then on the swap is this job's.
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
            var swaps = (await _swapOutRepository.GetAllPending())
                .Where(s => s.Provider == SwapProvider.Spark && s.LightningFeeSats is not null)
                .OrderBy(s => s.CreationDatetime)
                .ThenBy(s => s.Id)
                .ToList();
            if (swaps.Count == 0) return;

            _logger.LogInformation("Starting {JobName}: {Count} paid Spark swaps", nameof(SparkSwapExitJob), swaps.Count);

            // Every swap gets its own transfer's leaves before any exit, so no exit takes leaves another swap is due
            foreach (var swap in swaps.Where(s => s.SparkTransferId is null && s.TxId is null))
            {
                await RunAsync(swap, "attributing its transfer", () => _sparkSwapService.AttributeAsync(swap, ct));
            }

            foreach (var swap in swaps)
            {
                var step = await RunAsync(swap, "exiting", () => _sparkSwapService.ExitAsync(swap, ct));
                if (step == SparkExitStep.Completed)
                {
                    await AuditCompletionAsync(swap);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Error on {JobName}", nameof(SparkSwapExitJob));
            throw new JobExecutionException(e, false);
        }

        _logger.LogInformation("{JobName} ended", nameof(SparkSwapExitJob));
    }

    /// <summary>Runs one swap's step; a failure is logged and leaves the swap for the next run.</summary>
    private async Task<T?> RunAsync<T>(SwapOut swap, string what, Func<Task<T>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "Spark swap {SwapId}: error {What}; retried on the next run", swap.Id, what);
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
