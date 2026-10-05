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

using System.Diagnostics.Metrics;
using NodeGuard.Data.Models;

namespace NodeGuard.Services.Spark;

/// <summary>The Spark transit wallet's balance when the swap monitor last looked.</summary>
public sealed record SparkBalanceSnapshot(long OwnedSats, long AvailableSats, long IncomingSats, DateTimeOffset At);

/// <summary>An alert raised once, to be audited by the caller.</summary>
public sealed record SparkAlert(AuditActionType Action, AuditObjectType ObjectType, string ObjectId, string Message, object Details);

/// <summary>
/// Watches that Spark balances only pass through: sats that linger in the transit wallet with no swap
/// in flight, and swaps whose exit is overdue. Run by the swap monitor, which audits the alerts; each
/// alert is raised once. The balance is cached for the Swaps page and exported as the
/// <c>nodeguard.spark.balance</c> gauge (meter <see cref="MeterName"/>).
/// </summary>
public interface ISparkGuardrails
{
    SparkBalanceSnapshot? LastBalance { get; }

    /// <summary>When the wallet started holding sats with no Spark swap in flight, if it does.</summary>
    DateTimeOffset? LingeringSince { get; }

    Task<IReadOnlyList<SparkAlert>> CheckAsync(IReadOnlyCollection<SwapOut> pendingSwaps, CancellationToken ct = default);
}

public sealed class SparkGuardrails : ISparkGuardrails, IDisposable
{
    public const string MeterName = "NodeGuard.Spark";
    public static readonly TimeSpan LingerAlertAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan ExitOverdueAfter = TimeSpan.FromHours(6);

    private readonly SparkSettings _settings;
    private readonly ISparkWalletService _spark;
    private readonly ILogger<SparkGuardrails> _logger;
    private readonly TimeProvider _time;
    private readonly Meter _meter;
    private readonly HashSet<int> _overdueAlerted = [];
    private bool _lingerAlerted;

    public SparkGuardrails(SparkSettings settings, ISparkWalletService spark, ILogger<SparkGuardrails> logger, TimeProvider time)
    {
        _settings = settings;
        _spark = spark;
        _logger = logger;
        _time = time;
        _meter = new Meter(MeterName);
        _meter.CreateObservableGauge("nodeguard.spark.balance", () => LastBalance?.OwnedSats ?? 0, "sats",
            "Sats held by NodeGuard's Spark transit wallet");
    }

    public SparkBalanceSnapshot? LastBalance { get; private set; }

    public DateTimeOffset? LingeringSince { get; private set; }

    public async Task<IReadOnlyList<SparkAlert>> CheckAsync(IReadOnlyCollection<SwapOut> pendingSwaps, CancellationToken ct = default)
    {
        var alerts = new List<SparkAlert>();
        if (!_settings.Enabled || !_spark.Status.IsReady) return alerts;

        var now = _time.GetUtcNow();
        var balance = (await _spark.GetBalanceAsync(ct)).SatsBalance;
        LastBalance = new SparkBalanceSnapshot(balance.Owned, balance.Available, balance.Incoming, now);

        var sparkSwaps = pendingSwaps.Where(s => s.Provider == SwapProvider.Spark && s.Status == SwapOutStatus.Pending).ToList();
        var held = balance.Owned + balance.Incoming;

        if (held > _settings.MaxBalanceSats)
        {
            _logger.LogError("The Spark wallet holds {HeldSats} sats, over SPARK_MAX_BALANCE_SATS ({MaxBalanceSats})",
                held, _settings.MaxBalanceSats);
        }

        if (held > 0 && sparkSwaps.Count == 0)
        {
            LingeringSince ??= now;
            var lingering = now - LingeringSince.Value;
            _logger.LogWarning("The Spark wallet holds {HeldSats} sats with no Spark swap in flight, for {LingeringMinutes} minutes",
                held, (int)lingering.TotalMinutes);

            if (lingering >= LingerAlertAfter && !_lingerAlerted)
            {
                _lingerAlerted = true;
                alerts.Add(new SparkAlert(AuditActionType.SparkBalanceLingering, AuditObjectType.Wallet,
                    _spark.Status.IdentityPublicKey ?? "spark",
                    $"The Spark wallet has held {held} sats with no swap in flight since {LingeringSince:u}",
                    new { HeldSats = held, balance.Available, balance.Incoming, balance.Frozen, LingeringSince }));
            }
        }
        else
        {
            LingeringSince = null;
            _lingerAlerted = false;
        }

        foreach (var swap in sparkSwaps.Where(s => s.LightningFeeSats is not null))
        {
            var age = now - swap.CreationDatetime;
            if (age < ExitOverdueAfter || !_overdueAlerted.Add(swap.Id)) continue;

            _logger.LogError("Spark swap {SwapId} was paid {AgeHours} hours ago and its payout is not confirmed yet (exit {TxId})",
                swap.Id, (int)age.TotalHours, swap.TxId ?? "not started");
            alerts.Add(new SparkAlert(AuditActionType.SparkExitOverdue, AuditObjectType.SwapOut, swap.ProviderId ?? swap.Id.ToString(),
                $"Spark swap {swap.Id} has no confirmed payout {(int)age.TotalHours} hours after it was paid",
                new { SwapId = swap.Id, swap.NodeId, swap.SatsAmount, swap.TxId, swap.DestinationAddress, swap.CreationDatetime }));
        }

        return alerts;
    }

    public void Dispose() => _meter.Dispose();
}
