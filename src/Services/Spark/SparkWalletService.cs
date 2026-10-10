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

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NSpark;
using NSpark.Models;
using NSpark.Services;

namespace NodeGuard.Services.Spark;

public enum SparkWalletState
{
    /// <summary>SPARK_ENABLED is not set.</summary>
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

/// <summary>
/// NodeGuard's transit Spark wallet. Its keys stay with the signer (see <see cref="ISparkSignerProvider"/>);
/// wallet operations are serialized, as withdrawals have no idempotency key.
/// </summary>
public interface ISparkWalletService
{
    SparkWalletStatus Status { get; }

    /// <summary>Connects (or retries a failed connection) and returns the resulting status.</summary>
    Task<SparkWalletStatus> EnsureReadyAsync(CancellationToken ct = default);

    /// <exception cref="SparkUnavailableException">Spark is disabled or unavailable.</exception>
    Task<LightningInvoice> CreateInvoiceAsync(long amountSats, string memo, int expirySecs, CancellationToken ct = default);

    /// <summary>The SSP's status of a Lightning receive request, if it reports one.</summary>
    Task<string?> GetReceiveStatusAsync(string requestId, CancellationToken ct = default);

    /// <summary>
    /// The SSP's Lightning receive request, with the transfer that brought its payment in once the SSP reports
    /// it; null if the SSP does not know the request.
    /// </summary>
    Task<LightningReceiveRequest?> GetReceiveRequestAsync(string requestId, CancellationToken ct = default);

    Task<PendingTransferClaim> ClaimPendingAsync(CancellationToken ct = default);

    /// <summary>A transfer of any type, with its leaves; null if the operators do not know it.</summary>
    Task<SparkTransfer?> GetTransferAsync(string transferId, CancellationToken ct = default);

    /// <summary>The wallet's transfers in <paramref name="direction"/> created after <paramref name="since"/>, with their leaves.</summary>
    Task<IReadOnlyList<SparkTransfer>> GetTransfersAsync(TransferDirection direction, DateTimeOffset since,
        CancellationToken ct = default);

    /// <summary>The leaves that can be spent now (renewing those due first).</summary>
    Task<IReadOnlyList<SparkLeaf>> GetSpendableLeavesAsync(CancellationToken ct = default);

    Task<WalletBalance> GetBalanceAsync(CancellationToken ct = default);

    /// <summary>Exits every spendable sat to <paramref name="onChainAddress"/> in one cooperative exit.</summary>
    Task<WithdrawAllResult> WithdrawAllAsync(string onChainAddress, long maxFeeSats, CancellationToken ct = default);

    /// <summary>
    /// Exits exactly <paramref name="leafIds"/> to <paramref name="onChainAddress"/> in one cooperative exit, with no
    /// leaf swap (so the remote signer can sign it too). The fee comes out of the leaves' total.
    /// </summary>
    /// <exception cref="SparkLeavesNotSpendableException">A leaf is not owned or not spendable; nothing moved.</exception>
    Task<WithdrawLeavesResult> WithdrawLeavesAsync(IReadOnlyCollection<string> leafIds, string onChainAddress, long maxFeeSats,
        CancellationToken ct = default);
}

public sealed class SparkWalletService : ISparkWalletService, IAsyncDisposable
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private const int TransferPageSize = 100;
    private const int MaxTransferPages = 20;

    private readonly SparkSettings _settings;
    private readonly ISparkSignerProvider _signerProvider;
    private readonly ILogger<SparkWalletService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HttpClient _http = new();
    private SocketsHttpHandler? _operatorHandler;
    private SparkConnection? _connection;
    private SparkWallet? _wallet;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private volatile SparkWalletStatus _status;

    public SparkWalletService(SparkSettings settings, ISparkSignerProvider signerProvider, ILogger<SparkWalletService> logger,
        ILoggerFactory loggerFactory, TimeProvider time)
    {
        _settings = settings;
        _signerProvider = signerProvider;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _time = time;
        _status = new SparkWalletStatus(settings.Enabled ? SparkWalletState.NotConnected : SparkWalletState.Disabled);
    }

    public SparkWalletStatus Status => _status;

    public async Task<SparkWalletStatus> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (!_settings.Enabled) return _status;

        await _lock.WaitAsync(ct);
        try
        {
            await ConnectIfNeededAsync(ct);
            return _status;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<LightningInvoice> CreateInvoiceAsync(long amountSats, string memo, int expirySecs, CancellationToken ct = default) =>
        WithWalletAsync(w => w.CreateLightningInvoiceAsync(amountSats, memo, expirySecs, ct: ct), ct);

    public Task<string?> GetReceiveStatusAsync(string requestId, CancellationToken ct = default) =>
        WithWalletAsync(w => w.GetLightningReceiveRequestStatusAsync(requestId, ct), ct);

    public Task<LightningReceiveRequest?> GetReceiveRequestAsync(string requestId, CancellationToken ct = default) =>
        WithWalletAsync(w => w.GetLightningReceiveRequestAsync(requestId, ct), ct);

    public Task<PendingTransferClaim> ClaimPendingAsync(CancellationToken ct = default) =>
        WithWalletAsync(w => w.ClaimPendingTransfersAsync(ct), ct);

    public Task<SparkTransfer?> GetTransferAsync(string transferId, CancellationToken ct = default) =>
        WithWalletAsync(w => w.GetTransferAsync(transferId, ct), ct);

    public Task<IReadOnlyList<SparkTransfer>> GetTransfersAsync(TransferDirection direction, DateTimeOffset since,
        CancellationToken ct = default) =>
        WithWalletAsync(async w =>
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

    public Task<IReadOnlyList<SparkLeaf>> GetSpendableLeavesAsync(CancellationToken ct = default) =>
        WithWalletAsync(w => w.GetSpendableLeavesAsync(ct), ct);

    public Task<WalletBalance> GetBalanceAsync(CancellationToken ct = default) =>
        WithWalletAsync(w => w.GetBalanceAsync(ct), ct);

    public Task<WithdrawAllResult> WithdrawAllAsync(string onChainAddress, long maxFeeSats, CancellationToken ct = default) =>
        WithWalletAsync(w => w.WithdrawAllAsync(onChainAddress, maxFeeSats, ct), ct);

    public Task<WithdrawLeavesResult> WithdrawLeavesAsync(IReadOnlyCollection<string> leafIds, string onChainAddress,
        long maxFeeSats, CancellationToken ct = default) =>
        WithWalletAsync(w => w.WithdrawLeavesAsync(leafIds, onChainAddress, maxFeeSats, ct), ct);

    private async Task<T> WithWalletAsync<T>(Func<SparkWallet, Task<T>> operation, CancellationToken ct)
    {
        if (!_settings.Enabled) throw new SparkUnavailableException("Spark is not enabled (SPARK_ENABLED)");

        await _lock.WaitAsync(ct);
        try
        {
            await ConnectIfNeededAsync(ct);
            if (_wallet is null) throw new SparkUnavailableException(_status.Reason ?? "Spark is unavailable");

            return await operation(_wallet);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Called under the lock.</summary>
    private async Task ConnectIfNeededAsync(CancellationToken ct)
    {
        if (_wallet is not null) return;
        if (_status.State == SparkWalletState.Unavailable && _time.GetUtcNow() < _nextAttempt) return;

        try
        {
            var options = _settings.ToSparkOptions(await SspIdentityAsync(ct));
            var signer = await _signerProvider.GetSignerAsync(options, ct);

            _operatorHandler ??= _settings.OperatorCertsDirectory is { } certs ? PinnedHandler(certs) : null;
            _connection ??= new SparkConnection(Options.Create(options), _http, _loggerFactory, _operatorHandler);
            _wallet = await _connection.CreateWalletAsync(signer, ct);
            _status = new SparkWalletStatus(SparkWalletState.Ready, _wallet.IdentityPublicKeyHex);

            _logger.LogInformation(
                "Spark wallet ready: identity {Identity} on {Network}, {SignerMode} signer, {OperatorCount} operators, SSP {Ssp}",
                _wallet.IdentityPublicKeyHex, _settings.Network, _settings.SignerMode, options.SigningOperators.Length, options.SspUrl);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var reason = e is SparkUnavailableException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            _status = new SparkWalletStatus(SparkWalletState.Unavailable, Reason: reason);
            _nextAttempt = _time.GetUtcNow() + RetryAfter;
            _logger.LogError(e, "Spark is unavailable: {Reason}", reason);
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
        _lock.Dispose();
    }
}
