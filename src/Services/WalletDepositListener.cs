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

using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBXplorer.Models;
using NodeGuard.Data;
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using Nodeguard;

namespace NodeGuard.Services;

/// <summary>
/// Follows NBXplorer's event feed and publishes deposit events for NodeGuard's on-chain wallets: a transaction that
/// pays a wallet without spending any of its coins is detected when first seen, then confirmed once it reaches
/// <see cref="Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS"/>, or replaced if an RBF replacement takes its
/// place. Live only, like the rest of the wallet events: the feed is followed from NBXplorer's latest event at
/// startup, and deposits detected before a restart are not followed to confirmation.
/// </summary>
public class WalletDepositListener : BackgroundService
{
    internal static readonly TimeSpan PendingDepositLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan WalletMapRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private const int EventBatchSize = 100;

    private sealed record PendingDeposit(int WalletId, uint256 TxId, long AmountSats,
        IReadOnlyList<DepositOutput> Outputs, DateTimeOffset FirstSeen);

    private readonly INBXplorerService _nbXplorerService;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IWalletEventHub _hub;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WalletDepositListener> _logger;
    private readonly int _confirmationsRequired;

    private readonly Dictionary<(int WalletId, uint256 TxId), PendingDeposit> _pending = new();
    private Dictionary<string, int> _walletsByScheme = new();
    private DateTimeOffset _walletsLoadedAt = DateTimeOffset.MinValue;

    public WalletDepositListener(INBXplorerService nbXplorerService,
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IWalletEventHub hub,
        TimeProvider timeProvider,
        ILogger<WalletDepositListener> logger)
        : this(nbXplorerService, dbContextFactory, hub, timeProvider, logger, Constants.TRANSACTION_CONFIRMATION_MINIMUM_BLOCKS)
    {
    }

    internal WalletDepositListener(INBXplorerService nbXplorerService,
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IWalletEventHub hub,
        TimeProvider timeProvider,
        ILogger<WalletDepositListener> logger,
        int confirmationsRequired)
    {
        _confirmationsRequired = confirmationsRequired;
        _nbXplorerService = nbXplorerService;
        _dbContextFactory = dbContextFactory;
        _hub = hub;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal int PendingCount => _pending.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long? lastEventId = null;
        var backoff = InitialBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                lastEventId ??= await _nbXplorerService.GetLatestEventIdAsync(stoppingToken);

                var events = await _nbXplorerService.GetEventsAsync(lastEventId.Value, EventBatchSize, longPolling: true,
                    stoppingToken);
                foreach (var nbxEvent in events)
                {
                    await HandleEventAsync(nbxEvent, stoppingToken);
                    lastEventId = Math.Max(lastEventId.Value, nbxEvent.EventId);
                }

                backoff = InitialBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Wallet deposits: following NBXplorer's events failed, retrying in {Backoff}", backoff);
                await Task.Delay(backoff, _timeProvider, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    internal async Task HandleEventAsync(NewEventBase nbxEvent, CancellationToken cancellationToken)
    {
        switch (nbxEvent)
        {
            case NewTransactionEvent transaction:
                await HandleTransactionAsync(transaction, cancellationToken);
                break;
            case NewBlockEvent:
                await HandleBlockAsync(cancellationToken);
                break;
        }
    }

    private async Task HandleTransactionAsync(NewTransactionEvent transaction, CancellationToken cancellationToken)
    {
        // Events of address tracked sources (e.g. withdrawal destinations) carry no derivation scheme
        if (transaction.DerivationStrategy == null || transaction.TransactionData?.TransactionHash == null)
            return;

        var walletId = await ResolveWalletAsync(transaction.DerivationStrategy.ToString(), cancellationToken);
        if (walletId == null)
            return;

        var txId = transaction.TransactionData.TransactionHash;

        foreach (var replacedTxId in transaction.Replacing ?? [])
        {
            if (_pending.Remove((walletId.Value, replacedTxId), out var replaced))
                PublishDeposit(WALLET_EVENT_TYPE.WalletEventDepositReplaced, replaced, 0, txId);
        }

        // A deposit pays the wallet without spending its coins; anything spending them (withdrawals, channel opens,
        // RBF cancellations returning funds) is not one
        var isDeposit = transaction.Inputs.Count == 0 && transaction.Outputs.Count > 0;
        if (!isDeposit || _pending.ContainsKey((walletId.Value, txId)))
            return;

        var network = CurrentNetworkHelper.GetCurrentNetwork();
        var outputs = transaction.Outputs.Select(o => new DepositOutput
        {
            Address = o.Address?.ToString() ?? o.ScriptPubKey?.GetDestinationAddress(network)?.ToString() ?? string.Empty,
            Vout = (uint)o.Index,
            AmountSats = o.Value is Money money ? money.Satoshi : 0
        }).ToList();

        var deposit = new PendingDeposit(walletId.Value, txId, outputs.Sum(o => o.AmountSats), outputs,
            _timeProvider.GetUtcNow());
        var confirmations = transaction.BlockId == null ? 0 : Math.Max(1, transaction.TransactionData.Confirmations);

        PublishDeposit(WALLET_EVENT_TYPE.WalletEventDepositDetected, deposit, confirmations);

        if (confirmations >= _confirmationsRequired)
            PublishDeposit(WALLET_EVENT_TYPE.WalletEventDepositConfirmed, deposit, confirmations);
        else
            _pending[(deposit.WalletId, deposit.TxId)] = deposit;
    }

    private async Task HandleBlockAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        foreach (var deposit in _pending.Values.ToList())
        {
            try
            {
                var result = await _nbXplorerService.GetTransactionAsync(deposit.TxId, cancellationToken);
                if (result?.ReplacedBy != null)
                {
                    _pending.Remove((deposit.WalletId, deposit.TxId));
                    PublishDeposit(WALLET_EVENT_TYPE.WalletEventDepositReplaced, deposit, 0, result.ReplacedBy);
                }
                else if (result != null && result.Confirmations >= _confirmationsRequired)
                {
                    _pending.Remove((deposit.WalletId, deposit.TxId));
                    PublishDeposit(WALLET_EVENT_TYPE.WalletEventDepositConfirmed, deposit, result.Confirmations);
                }
                else if (now - deposit.FirstSeen > PendingDepositLifetime)
                {
                    _pending.Remove((deposit.WalletId, deposit.TxId));
                    _logger.LogWarning("Wallet deposits: stopped following unconfirmed deposit {TxId} to wallet {WalletId}",
                        deposit.TxId, deposit.WalletId);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Wallet deposits: could not check deposit {TxId} to wallet {WalletId}",
                    deposit.TxId, deposit.WalletId);
            }
        }
    }

    private void PublishDeposit(WALLET_EVENT_TYPE type, PendingDeposit deposit, long confirmations,
        uint256? replacedBy = null)
    {
        var payload = new DepositEvent
        {
            TxId = deposit.TxId.ToString(),
            AmountSats = deposit.AmountSats,
            Confirmations = (uint)Math.Max(0, confirmations),
            Outputs = { deposit.Outputs }
        };
        if (replacedBy != null) payload.ReplacedByTxId = replacedBy.ToString();

        _logger.LogInformation("Wallet deposits: {Type} {TxId} to wallet {WalletId} ({AmountSats} sats)",
            type, deposit.TxId, deposit.WalletId, deposit.AmountSats);

        _hub.Publish(new WalletEvent
        {
            Type = type,
            WalletId = deposit.WalletId,
            OccurredAtUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            Deposit = payload
        });
    }

    /// <summary>
    /// The on-chain wallet an NBXplorer derivation scheme belongs to. The map is reloaded when a scheme is unknown
    /// (a wallet finalised since), at most every <see cref="WalletMapRefreshInterval"/>.
    /// </summary>
    private async Task<int?> ResolveWalletAsync(string derivationScheme, CancellationToken cancellationToken)
    {
        if (_walletsByScheme.TryGetValue(derivationScheme, out var walletId))
            return walletId;

        var now = _timeProvider.GetUtcNow();
        if (now - _walletsLoadedAt < WalletMapRefreshInterval)
            return null;

        _walletsLoadedAt = now;
        await using var applicationDbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var wallets = await applicationDbContext.Wallets
            .AsNoTracking()
            .Include(x => x.Keys)
            .Where(x => x.Kind == WalletKind.OnChain)
            .ToListAsync(cancellationToken);

        var map = new Dictionary<string, int>();
        foreach (var wallet in wallets)
        {
            try
            {
                var scheme = wallet.GetDerivationStrategy()?.ToString();
                if (scheme != null)
                    map[scheme] = wallet.Id;
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Wallet deposits: wallet {WalletId} has no usable derivation scheme", wallet.Id);
            }
        }

        _walletsByScheme = map;
        return map.TryGetValue(derivationScheme, out walletId) ? walletId : null;
    }
}
