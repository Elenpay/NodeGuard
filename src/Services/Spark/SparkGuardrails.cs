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
using System.Diagnostics.Metrics;
using NodeGuard.Data.Models;
using NSpark.Models;

namespace NodeGuard.Services.Spark;

/// <summary>A Spark wallet's balance when the swap monitor last looked.</summary>
public sealed record SparkBalanceSnapshot(long OwnedSats, long AvailableSats, long IncomingSats, DateTimeOffset At);

/// <summary>An alert raised once, to be audited by the caller.</summary>
public sealed record SparkAlert(AuditActionType Action, AuditObjectType ObjectType, string ObjectId, string Message, object Details);

/// <summary>
/// Watches that Spark balances only pass through: sats stuck in a Spark wallet that none of its swaps in
/// flight accounts for, and swaps whose exit is overdue. Run by the swap monitor, which audits the alerts; each
/// alert is raised once. Each wallet's balance is exported as the <c>nodeguard.spark.balance</c> gauge (meter
/// <see cref="MeterName"/>), tagged <c>spark.wallet</c>.
/// </summary>
public interface ISparkGuardrails
{
    /// <summary>The wallet's balance when the swap monitor last looked, if it did.</summary>
    SparkBalanceSnapshot? LastBalance(SparkWalletRef wallet);

    /// <summary>When the wallet started holding sats that none of its Spark swaps in flight accounts for, if it does.</summary>
    DateTimeOffset? StuckSince(SparkWalletRef wallet);

    Task<IReadOnlyList<SparkAlert>> CheckAsync(IReadOnlyCollection<SwapOut> pendingSwaps, CancellationToken ct = default);
}

public sealed class SparkGuardrails : ISparkGuardrails, IDisposable
{
    public const string MeterName = "NodeGuard.Spark";
    public static readonly TimeSpan StuckAlertAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan ExitOverdueAfter = TimeSpan.FromHours(6);

    private readonly SparkSettings _settings;
    private readonly ISparkWalletService _spark;
    private readonly ILogger<SparkGuardrails> _logger;
    private readonly TimeProvider _time;
    private readonly Meter _meter;
    private readonly HashSet<int> _overdueAlerted = [];
    private readonly ConcurrentDictionary<SparkWalletRef, Watch> _watches = new();

    private sealed class Watch
    {
        public SparkBalanceSnapshot? LastBalance;
        public DateTimeOffset? StuckSince;
        public bool StuckAlerted;
    }

    public SparkGuardrails(SparkSettings settings, ISparkWalletService spark, ILogger<SparkGuardrails> logger, TimeProvider time)
    {
        _settings = settings;
        _spark = spark;
        _logger = logger;
        _time = time;
        _meter = new Meter(MeterName);
        _meter.CreateObservableGauge("nodeguard.spark.balance",
            () => _watches.Select(w => new Measurement<long>(w.Value.LastBalance?.OwnedSats ?? 0,
                new KeyValuePair<string, object?>("spark.wallet", w.Key.WalletId?.ToString() ?? "remote-signer"))),
            "sats", "Sats held by each of NodeGuard's Spark wallets");
    }

    public SparkBalanceSnapshot? LastBalance(SparkWalletRef wallet) =>
        _watches.TryGetValue(wallet, out var watch) ? watch.LastBalance : null;

    public DateTimeOffset? StuckSince(SparkWalletRef wallet) =>
        _watches.TryGetValue(wallet, out var watch) ? watch.StuckSince : null;

    /// <summary>What the pending Spark swaps not paid yet will still bring into the wallet.</summary>
    public static long UnpaidSats(IEnumerable<SwapOut> pendingSwaps) =>
        PendingSpark(pendingSwaps).Where(s => s.LightningFeeSats is null).Sum(s => s.SatsAmount);

    /// <summary>
    /// What the wallet holds for the pending Spark swaps that have not exited yet: what each one brought in, or its
    /// amount until it is known.
    /// </summary>
    public static long DueSats(IEnumerable<SwapOut> pendingSwaps) =>
        PendingSpark(pendingSwaps).Where(s => s.TxId is null).Sum(s => s.SparkReceivedSats ?? s.SatsAmount);

    private static IEnumerable<SwapOut> PendingSpark(IEnumerable<SwapOut> swaps) =>
        swaps.Where(s => s.Provider == SwapProvider.Spark && s.Status == SwapOutStatus.Pending);

    public async Task<IReadOnlyList<SparkAlert>> CheckAsync(IReadOnlyCollection<SwapOut> pendingSwaps, CancellationToken ct = default)
    {
        var alerts = new List<SparkAlert>();
        if (!_settings.Enabled) return alerts;

        var now = _time.GetUtcNow();
        var sparkSwaps = pendingSwaps.Where(s => s.Provider == SwapProvider.Spark && s.Status == SwapOutStatus.Pending).ToList();

        var wallets = await _spark.GetWalletsAsync(ct);
        foreach (var gone in _watches.Keys.Except(wallets.Select(w => w.Ref)))
        {
            _watches.TryRemove(gone, out _);
        }

        foreach (var wallet in wallets)
        {
            if (!(await _spark.EnsureReadyAsync(wallet.Ref, ct)).IsReady) continue;

            SatsBalance balance;
            try
            {
                balance = (await _spark.GetBalanceAsync(wallet.Ref, ct)).SatsBalance;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Could not check the balance of {SparkWallet} ({Name})", wallet.Ref, wallet.Name);
                continue;
            }

            var watch = _watches.GetOrAdd(wallet.Ref, _ => new Watch());
            watch.LastBalance = new SparkBalanceSnapshot(balance.Owned, balance.Available, balance.Incoming, now);

            var held = balance.Owned + balance.Incoming;
            if (held > wallet.MaxBalanceSats)
            {
                _logger.LogError("{SparkWallet} ({Name}) holds {HeldSats} sats, over its max balance ({MaxBalanceSats})",
                    wallet.Ref, wallet.Name, held, wallet.MaxBalanceSats);
            }

            // Each swap's sats are its own until it exits: only what none of the wallet's swaps in flight accounts for
            // is stuck
            var due = DueSats(sparkSwaps.Where(s =>
                string.Equals(s.SparkIdentity, wallet.IdentityPublicKey, StringComparison.OrdinalIgnoreCase)));
            var stuck = held - due;
            if (stuck > 0)
            {
                watch.StuckSince ??= now;
                var stuckFor = now - watch.StuckSince.Value;
                _logger.LogWarning("{SparkWallet} ({Name}) holds {StuckSats} sats that no Spark swap in flight accounts for, for {StuckMinutes} minutes",
                    wallet.Ref, wallet.Name, stuck, (int)stuckFor.TotalMinutes);

                if (stuckFor >= StuckAlertAfter && !watch.StuckAlerted)
                {
                    watch.StuckAlerted = true;
                    alerts.Add(new SparkAlert(AuditActionType.SparkBalanceStuck, AuditObjectType.Wallet,
                        wallet.Ref.WalletId?.ToString() ?? wallet.IdentityPublicKey ?? "spark",
                        $"{wallet.Name} has held {stuck} sats that no swap in flight accounts for since {watch.StuckSince:u}",
                        new
                        {
                            SparkWallet = wallet.Name, StuckSats = stuck, HeldSats = held, DueSats = due, balance.Available,
                            balance.Incoming, balance.Frozen, watch.StuckSince
                        }));
                }
            }
            else
            {
                watch.StuckSince = null;
                watch.StuckAlerted = false;
            }
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
