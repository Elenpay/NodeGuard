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
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NSpark;
using NSpark.Models;
using NSpark.Services;

namespace NodeGuard.Services.Spark;

public enum SparkWalletState
{
    /// <summary>Spark is disabled (SPARK_ENABLED=false).</summary>
    Disabled,

    /// <summary>Not connected yet.</summary>
    NotConnected,

    Ready,

    /// <summary>The last attempt failed; see the reason. Retried at most once a minute.</summary>
    Unavailable
}

public sealed record SparkWalletStatus(SparkWalletState State, string? IdentityPublicKey = null, string? Reason = null)
{
    public bool IsReady => State == SparkWalletState.Ready;
}

/// <summary>A Spark wallet swaps can go through, as recorded (no connection needed).</summary>
/// <param name="MaxBalanceSats">The most it may hold, counting a new swap: its own max balance, else SPARK_MAX_BALANCE_SATS.</param>
public sealed record SparkWalletEntry(SparkWalletRef Ref, string Name, string? IdentityPublicKey, long MaxBalanceSats, bool IsArchived = false);

/// <summary>
/// NodeGuard's Spark wallets: the Spark wallet rows (SPARK_SIGNER=wallet) or the remote signer's single wallet
/// (SPARK_SIGNER=remote). Their keys stay with their signer (see <see cref="ISparkSignerProvider"/>). Each wallet
/// connects on first use and its operations are serialized, as withdrawals have no idempotency key; different
/// wallets run in parallel over one operator connection.
/// </summary>
public interface ISparkWalletService
{
    /// <summary>The wallet's status when last used: NotConnected until something connects it.</summary>
    SparkWalletStatus GetStatus(SparkWalletRef wallet);

    /// <summary>The wallets swaps can go through: the Spark wallets not archived, or the remote signer's.</summary>
    Task<IReadOnlyList<SparkWalletEntry>> GetWalletsAsync(CancellationToken ct = default);

    /// <summary>The wallet's entry, archived or not; null if it is not a Spark wallet.</summary>
    Task<SparkWalletEntry?> GetWalletAsync(SparkWalletRef wallet, CancellationToken ct = default);

    /// <summary>The wallet with this Spark identity, archived or not: where a swap's sats went.</summary>
    Task<SparkWalletEntry?> FindByIdentityAsync(string identityPublicKey, CancellationToken ct = default);

    /// <summary>Connects (or retries a failed connection) and returns the resulting status.</summary>
    Task<SparkWalletStatus> EnsureReadyAsync(SparkWalletRef wallet, CancellationToken ct = default);

    /// <exception cref="SparkUnavailableException">Spark is disabled, or the wallet is unavailable.</exception>
    Task<LightningInvoice> CreateInvoiceAsync(SparkWalletRef wallet, long amountSats, string memo, int expirySecs,
        CancellationToken ct = default);

    /// <summary>The SSP's status of a Lightning receive request, if it reports one.</summary>
    Task<string?> GetReceiveStatusAsync(SparkWalletRef wallet, string requestId, CancellationToken ct = default);

    /// <summary>
    /// The SSP's Lightning receive request, with the transfer that brought its payment in once the SSP reports
    /// it; null if the SSP does not know the request.
    /// </summary>
    Task<LightningReceiveRequest?> GetReceiveRequestAsync(SparkWalletRef wallet, string requestId, CancellationToken ct = default);

    Task<PendingTransferClaim> ClaimPendingAsync(SparkWalletRef wallet, CancellationToken ct = default);

    /// <summary>A transfer of any type, with its leaves; null if the operators do not know it.</summary>
    Task<SparkTransfer?> GetTransferAsync(SparkWalletRef wallet, string transferId, CancellationToken ct = default);

    /// <summary>The wallet's transfers in <paramref name="direction"/> created after <paramref name="since"/>, with their leaves.</summary>
    Task<IReadOnlyList<SparkTransfer>> GetTransfersAsync(SparkWalletRef wallet, TransferDirection direction, DateTimeOffset since,
        CancellationToken ct = default);

    Task<WalletBalance> GetBalanceAsync(SparkWalletRef wallet, CancellationToken ct = default);

    /// <summary>Exits every spendable sat to <paramref name="onChainAddress"/> in one cooperative exit.</summary>
    Task<WithdrawAllResult> WithdrawAllAsync(SparkWalletRef wallet, string onChainAddress, long maxFeeSats,
        CancellationToken ct = default);

    /// <summary>
    /// Exits exactly <paramref name="leafIds"/> to <paramref name="onChainAddress"/> in one cooperative exit, with no
    /// leaf swap (so the remote signer can sign it too). The fee comes out of the leaves' total.
    /// </summary>
    /// <exception cref="SparkLeavesNotSpendableException">A leaf is not owned or not spendable; nothing moved.</exception>
    Task<WithdrawLeavesResult> WithdrawLeavesAsync(SparkWalletRef wallet, IReadOnlyCollection<string> leafIds, string onChainAddress,
        long maxFeeSats, CancellationToken ct = default);

    /// <summary>Drops the wallet's connection (an archived wallet); it reconnects if used again.</summary>
    void Forget(SparkWalletRef wallet);
}

public sealed class SparkWalletService : ISparkWalletService, IAsyncDisposable
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private const int TransferPageSize = 100;
    private const int MaxTransferPages = 20;

    private readonly SparkSettings _settings;
    private readonly ISparkSignerProvider _signerProvider;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SparkWalletService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<SparkWalletRef, Slot> _slots = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly HttpClient _http = new();
    private SocketsHttpHandler? _operatorHandler;
    private SparkConnection? _connection;
    private SparkOptions? _options;

    /// <summary>One wallet's connection, status and lock.</summary>
    private sealed class Slot(SparkWalletStatus status)
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public SparkWallet? Wallet;
        public DateTimeOffset NextAttempt = DateTimeOffset.MinValue;
        public volatile SparkWalletStatus Status = status;
    }

    public SparkWalletService(SparkSettings settings, ISparkSignerProvider signerProvider, IServiceScopeFactory scopes,
        ILogger<SparkWalletService> logger, ILoggerFactory loggerFactory, TimeProvider time)
    {
        _settings = settings;
        _signerProvider = signerProvider;
        _scopes = scopes;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _time = time;
    }

    public SparkWalletStatus GetStatus(SparkWalletRef wallet) =>
        !_settings.Enabled ? new SparkWalletStatus(SparkWalletState.Disabled)
        : _slots.TryGetValue(wallet, out var slot) ? slot.Status
        : new SparkWalletStatus(SparkWalletState.NotConnected);

    public async Task<IReadOnlyList<SparkWalletEntry>> GetWalletsAsync(CancellationToken ct = default)
    {
        if (!_settings.Enabled) return [];
        if (_settings.SignerMode == SparkSignerMode.Remote) return [RemoteSignerEntry()];

        return (await SparkWalletRowsAsync(includeArchived: false)).Select(Entry).ToList();
    }

    public async Task<SparkWalletEntry?> GetWalletAsync(SparkWalletRef wallet, CancellationToken ct = default)
    {
        if (!_settings.Enabled) return null;
        if (_settings.SignerMode == SparkSignerMode.Remote) return wallet.WalletId is null ? RemoteSignerEntry() : null;
        if (wallet.WalletId is not { } id) return null;

        using var scope = _scopes.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IWalletRepository>().GetById(id);
        return row is { Kind: WalletKind.Spark } ? Entry(row) : null;
    }

    public async Task<SparkWalletEntry?> FindByIdentityAsync(string identityPublicKey, CancellationToken ct = default)
    {
        if (!_settings.Enabled) return null;
        if (_settings.SignerMode == SparkSignerMode.Remote)
        {
            return string.Equals(_settings.IdentityPublicKey, identityPublicKey, StringComparison.OrdinalIgnoreCase)
                ? RemoteSignerEntry()
                : null;
        }

        var row = (await SparkWalletRowsAsync(includeArchived: true)).FirstOrDefault(w =>
            string.Equals(w.SparkIdentityPublicKey, identityPublicKey, StringComparison.OrdinalIgnoreCase));
        return row is null ? null : Entry(row);
    }

    private SparkWalletEntry RemoteSignerEntry() =>
        new(SparkWalletRef.RemoteSigner, "Remote signer", _settings.IdentityPublicKey, _settings.MaxBalanceSats);

    private SparkWalletEntry Entry(Wallet row) =>
        new(new SparkWalletRef(row.Id), row.Name, row.SparkIdentityPublicKey, _settings.MaxBalanceFor(row), row.IsArchived);

    private async Task<List<Wallet>> SparkWalletRowsAsync(bool includeArchived)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWalletRepository>().GetSparkWallets(includeArchived);
    }

    public async Task<SparkWalletStatus> EnsureReadyAsync(SparkWalletRef wallet, CancellationToken ct = default)
    {
        if (!_settings.Enabled) return GetStatus(wallet);

        var slot = SlotOf(wallet);
        await slot.Lock.WaitAsync(ct);
        try
        {
            await ConnectIfNeededAsync(wallet, slot, ct);
            return slot.Status;
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    public Task<LightningInvoice> CreateInvoiceAsync(SparkWalletRef wallet, long amountSats, string memo, int expirySecs,
        CancellationToken ct = default) =>
        WithWalletAsync(wallet, w => w.CreateLightningInvoiceAsync(amountSats, memo, expirySecs, ct: ct), ct);

    public Task<string?> GetReceiveStatusAsync(SparkWalletRef wallet, string requestId, CancellationToken ct = default) =>
        WithWalletAsync(wallet, w => w.GetLightningReceiveRequestStatusAsync(requestId, ct), ct);

    public Task<LightningReceiveRequest?> GetReceiveRequestAsync(SparkWalletRef wallet, string requestId,
        CancellationToken ct = default) =>
        WithWalletAsync(wallet, w => w.GetLightningReceiveRequestAsync(requestId, ct), ct);

    public Task<PendingTransferClaim> ClaimPendingAsync(SparkWalletRef wallet, CancellationToken ct = default) =>
        WithWalletAsync(wallet, w => w.ClaimPendingTransfersAsync(ct), ct);

    public Task<SparkTransfer?> GetTransferAsync(SparkWalletRef wallet, string transferId, CancellationToken ct = default) =>
        WithWalletAsync(wallet, w => w.GetTransferAsync(transferId, ct), ct);

    public Task<IReadOnlyList<SparkTransfer>> GetTransfersAsync(SparkWalletRef wallet, TransferDirection direction,
        DateTimeOffset since, CancellationToken ct = default) =>
        WithWalletAsync(wallet, async w =>
        {
            var transfers = new List<SparkTransfer>();
            long offset = 0;
            for (var page = 0; page < MaxTransferPages; page++)
            {
                var result = await w.GetTransfersAsync(TransferPageSize, offset, createdAfter: since, direction: direction, ct: ct);
                transfers.AddRange(result.Transfers);
                if (result.Transfers.Count < TransferPageSize || result.Offset <= offset) break;
                offset = result.Offset;
            }

            return (IReadOnlyList<SparkTransfer>)transfers;
        }, ct);

    public Task<WalletBalance> GetBalanceAsync(SparkWalletRef wallet, CancellationToken ct = default) =>
        WithWalletAsync(wallet, w => w.GetBalanceAsync(ct), ct);

    public Task<WithdrawAllResult> WithdrawAllAsync(SparkWalletRef wallet, string onChainAddress, long maxFeeSats,
        CancellationToken ct = default) =>
        WithWalletAsync(wallet, w => w.WithdrawAllAsync(onChainAddress, maxFeeSats, ct), ct);

    public Task<WithdrawLeavesResult> WithdrawLeavesAsync(SparkWalletRef wallet, IReadOnlyCollection<string> leafIds,
        string onChainAddress, long maxFeeSats, CancellationToken ct = default) =>
        WithWalletAsync(wallet, w => w.WithdrawLeavesAsync(leafIds, onChainAddress, maxFeeSats, ct), ct);

    public void Forget(SparkWalletRef wallet) => _slots.TryRemove(wallet, out _);

    private Slot SlotOf(SparkWalletRef wallet) =>
        _slots.GetOrAdd(wallet, _ => new Slot(new SparkWalletStatus(SparkWalletState.NotConnected)));

    private async Task<T> WithWalletAsync<T>(SparkWalletRef wallet, Func<SparkWallet, Task<T>> operation, CancellationToken ct)
    {
        if (!_settings.Enabled) throw new SparkUnavailableException("Spark is not enabled (SPARK_ENABLED)");

        var slot = SlotOf(wallet);
        await slot.Lock.WaitAsync(ct);
        try
        {
            await ConnectIfNeededAsync(wallet, slot, ct);
            if (slot.Wallet is null) throw new SparkUnavailableException(slot.Status.Reason ?? $"{wallet} is unavailable");

            return await operation(slot.Wallet);
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    /// <summary>Called under the slot's lock.</summary>
    private async Task ConnectIfNeededAsync(SparkWalletRef wallet, Slot slot, CancellationToken ct)
    {
        if (slot.Wallet is not null) return;
        if (slot.Status.State == SparkWalletState.Unavailable && _time.GetUtcNow() < slot.NextAttempt) return;

        try
        {
            var (connection, options) = await ConnectionAsync(ct);
            var signer = await _signerProvider.GetSignerAsync(wallet, options, ct);
            slot.Wallet = await connection.CreateWalletAsync(signer, ct);
            slot.Status = new SparkWalletStatus(SparkWalletState.Ready, slot.Wallet.IdentityPublicKeyHex);

            _logger.LogInformation(
                "{SparkWallet} ready: identity {Identity} on {Network}, {SignerMode} signer, {OperatorCount} operators, SSP {Ssp}",
                wallet, slot.Wallet.IdentityPublicKeyHex, _settings.Network, _settings.SignerMode, options.SigningOperators.Length,
                options.SspUrl);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var reason = e is SparkUnavailableException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            slot.Status = new SparkWalletStatus(SparkWalletState.Unavailable, Reason: reason);
            slot.NextAttempt = _time.GetUtcNow() + RetryAfter;
            _logger.LogError(e, "{SparkWallet} is unavailable: {Reason}", wallet, reason);
        }
    }

    /// <summary>The operator connection all wallets share, opened on first use.</summary>
    private async Task<(SparkConnection, SparkOptions)> ConnectionAsync(CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            if (_connection is null)
            {
                var options = _settings.ToSparkOptions(await SspIdentityAsync(ct));
                _operatorHandler ??= _settings.OperatorCertsDirectory is { } certs ? PinnedHandler(certs) : null;
                _connection = new SparkConnection(Options.Create(options), _http, _loggerFactory, _operatorHandler);
                _options = options;
            }

            return (_connection, _options!);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// The SSP identity: configured, else Lightspark's default on mainnet (NSpark knows it), else
    /// published by the SSP itself at /identity (open-ssp, regtest only).
    /// </summary>
    private async Task<string?> SspIdentityAsync(CancellationToken ct)
    {
        if (_settings.SspIdentityPublicKey is { } configured) return configured;
        if (_settings.IsMainnet) return null;

        using var document = JsonDocument.Parse(await _http.GetStringAsync(new Uri(new Uri(_settings.SspUrl), "/identity"), ct));
        return document.RootElement.GetProperty("identityPublicKey").GetString();
    }

    /// <summary>Trusts exactly the certificates in <paramref name="directory"/> (self-signed regtest operators).</summary>
    private static SocketsHttpHandler PinnedHandler(string directory)
    {
        var pinned = Directory.GetFiles(directory, "*.crt")
            .Select(path => X509Certificate2.CreateFromPem(File.ReadAllText(path)))
            .ToList();

        return new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && pinned.Any(p => p.RawData.AsSpan().SequenceEqual(certificate.GetRawCertData()))
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _operatorHandler?.Dispose();
        _http.Dispose();
        _connectionLock.Dispose();
    }
}
