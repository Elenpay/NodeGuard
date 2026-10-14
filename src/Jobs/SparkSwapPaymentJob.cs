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

using System.Globalization;
using NodeGuard.Services.Spark;
using Quartz;

namespace NodeGuard.Jobs;

/// <summary>
/// One-shot Quartz job that pays a retry of a Spark swap's payment once its backoff delay is over. Its invoice was
/// issued and saved on the swap when the previous attempt failed; the job carries what paying it needs. Recovered
/// after a restart: <see cref="ISparkSwapService.PayScheduledAsync"/> never pays twice.
/// </summary>
public class SparkSwapPaymentJob : IJob
{
    private const string SwapIdKey = "swapId";
    private const string PaymentHashKey = "paymentHash";
    private const string PaymentRequestKey = "paymentRequest";
    private const string MaxFeePercentKey = "maxFeePercent";
    private const string ChannelsOutKey = "channelsOut";
    private const string TimeoutSecondsKey = "timeoutSeconds";

    private readonly ILogger<SparkSwapPaymentJob> _logger;
    private readonly ISparkSwapService _sparkSwaps;

    public SparkSwapPaymentJob(ILogger<SparkSwapPaymentJob> logger, ISparkSwapService sparkSwaps)
    {
        _logger = logger;
        _sparkSwaps = sparkSwaps;
    }

    /// <summary>Schedules the payment of attempt <paramref name="attempt"/> of a swap's payment at <paramref name="fireAt"/>.</summary>
    public static async Task ScheduleAsync(ISchedulerFactory schedulerFactory, DateTimeOffset fireAt, int swapId, int attempt,
        string paymentHash, string paymentRequest, decimal maxFeePercent, ulong[]? channelsOut, int timeoutSeconds)
    {
        var data = new JobDataMap
        {
            { SwapIdKey, swapId },
            { PaymentHashKey, paymentHash },
            { PaymentRequestKey, paymentRequest },
            { MaxFeePercentKey, maxFeePercent.ToString(CultureInfo.InvariantCulture) },
            { ChannelsOutKey, string.Join(',', channelsOut ?? []) },
            { TimeoutSecondsKey, timeoutSeconds }
        };

        var job = JobBuilder.Create<SparkSwapPaymentJob>()
            .WithIdentity($"{nameof(SparkSwapPaymentJob)}-{swapId}-a{attempt}")
            .UsingJobData(data)
            .RequestRecovery()
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{nameof(SparkSwapPaymentJob)}Trigger-{swapId}-a{attempt}")
            .StartAt(fireAt)
            .Build();

        var scheduler = await schedulerFactory.GetScheduler();
        await scheduler.ScheduleJob(job, trigger);
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var data = context.MergedJobDataMap;
        var swapId = data.GetIntValue(SwapIdKey);
        _logger.LogInformation("Paying the scheduled retry of Spark swap {SwapId}", swapId);

        try
        {
            var channels = data.GetString(ChannelsOutKey);
            await _sparkSwaps.PayScheduledAsync(swapId,
                data.GetString(PaymentHashKey)!,
                data.GetString(PaymentRequestKey)!,
                decimal.Parse(data.GetString(MaxFeePercentKey)!, CultureInfo.InvariantCulture),
                string.IsNullOrEmpty(channels) ? null : channels.Split(',').Select(ulong.Parse).ToArray(),
                data.GetIntValue(TimeoutSecondsKey),
                context.CancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error paying the scheduled retry of Spark swap {SwapId}", swapId);
            throw new JobExecutionException(e, refireImmediately: false);
        }
    }
}
