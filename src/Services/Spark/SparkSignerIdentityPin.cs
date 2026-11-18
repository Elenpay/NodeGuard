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

namespace NodeGuard.Services.Spark;

/// <summary>
/// The Spark identity the remote signer must serve (SPARK_SIGNER=remote): SPARK_IDENTITY_PUBKEY when set, else the
/// identity saved on the first successful handshake. Swaps record the identity that received them, so a signer that
/// started serving another identity would leave them unable to exit: NodeGuard refuses it instead.
/// </summary>
public interface ISparkSignerIdentityPin
{
    /// <summary>
    /// The identity the signer must serve: SPARK_IDENTITY_PUBKEY, else the saved one; null until the first handshake
    /// saves it.
    /// </summary>
    Task<string?> ExpectedAsync(CancellationToken ct = default);

    /// <summary>
    /// Holds <paramref name="servedIdentity"/> (from a handshake whose policy already matched) to the expected
    /// identity, or saves it when there is none yet.
    /// </summary>
    /// <exception cref="SparkUnavailableException">The signer serves another identity, or SPARK_IDENTITY_PUBKEY
    /// disagrees with the saved one.</exception>
    Task PinOrVerifyAsync(string servedIdentity, CancellationToken ct = default);
}

/// <summary>
/// Trust on first use, as Spark wallet rows record their identity at creation. The saved row is keyed by
/// SPARK_SEED_FINGERPRINT and SPARK_ACCOUNT, so a deliberate seed change saves a new one; to accept another identity
/// for the same seed, delete its row (docs/spark-swaps.md) and restart. A singleton: the identity, once known, is cached.
/// </summary>
public sealed class SparkSignerIdentityPin : ISparkSignerIdentityPin
{
    private readonly SparkSettings _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SparkSignerIdentityPin> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private SparkSignerIdentity? _saved;

    public SparkSignerIdentityPin(SparkSettings settings, IServiceScopeFactory scopes, ILogger<SparkSignerIdentityPin> logger)
    {
        _settings = settings;
        _scopes = scopes;
        _logger = logger;
    }

    private string SeedFingerprint => _settings.SeedFingerprint
                                      ?? throw new SparkUnavailableException("SPARK_SEED_FINGERPRINT is not set");

    public async Task<string?> ExpectedAsync(CancellationToken ct = default)
    {
        if (_settings.SignerMode != SparkSignerMode.Remote) return null;
        return _settings.IdentityPublicKey ?? (await SavedAsync(ct))?.IdentityPublicKey;
    }

    public async Task PinOrVerifyAsync(string servedIdentity, CancellationToken ct = default)
    {
        var served = servedIdentity.ToLowerInvariant();

        await _lock.WaitAsync(ct);
        try
        {
            var saved = await SavedUnlockedAsync();
            var configured = _settings.IdentityPublicKey;

            if (configured is not null && saved is not null && !Same(configured, saved.IdentityPublicKey))
            {
                throw new SparkUnavailableException(
                    $"SPARK_IDENTITY_PUBKEY is {configured}, but NodeGuard saved {saved.IdentityPublicKey} for seed {saved.SeedFingerprint} " +
                    $"account {saved.Account} on {saved.CreationDatetime:u}: fix the variable, or reset the saved identity (docs/spark-swaps.md)");
            }

            if (configured is not null && !Same(configured, served))
            {
                throw new SparkUnavailableException(
                    $"the remote signer serves Spark identity {served}, but SPARK_IDENTITY_PUBKEY is {configured}");
            }

            if (saved is not null)
            {
                if (!Same(saved.IdentityPublicKey, served))
                {
                    throw new SparkUnavailableException(
                        $"the remote signer serves Spark identity {served}, but NodeGuard saved {saved.IdentityPublicKey} for seed " +
                        $"{saved.SeedFingerprint} account {saved.Account} on {saved.CreationDatetime:u}. If the change is deliberate, " +
                        "move the Spark balance first, then reset the saved identity (docs/spark-swaps.md)");
                }

                return;
            }

            await SaveUnlockedAsync(served);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<SparkSignerIdentity?> SavedAsync(CancellationToken ct)
    {
        if (_saved is not null) return _saved;

        await _lock.WaitAsync(ct);
        try
        {
            return await SavedUnlockedAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Called under the lock. Caches the row once found: it only changes when deleted, with a restart.</summary>
    private async Task<SparkSignerIdentity?> SavedUnlockedAsync()
    {
        if (_saved is not null) return _saved;

        using var scope = _scopes.CreateScope();
        _saved = await scope.ServiceProvider.GetRequiredService<ISparkSignerIdentityRepository>()
            .GetAsync(SeedFingerprint, _settings.Account);
        return _saved;
    }

    /// <summary>Called under the lock, when nothing is saved yet.</summary>
    private async Task SaveUnlockedAsync(string served)
    {
        using var scope = _scopes.CreateScope();
        var (saved, created) = await scope.ServiceProvider.GetRequiredService<ISparkSignerIdentityRepository>()
            .GetOrAddAsync(SeedFingerprint, _settings.Account, served);
        _saved = saved;

        if (!created)
        {
            // Another NodeGuard saved one between the read and the insert: hold this handshake to it
            if (!Same(saved.IdentityPublicKey, served))
            {
                throw new SparkUnavailableException(
                    $"the remote signer serves Spark identity {served}, but NodeGuard saved {saved.IdentityPublicKey} for seed " +
                    $"{saved.SeedFingerprint} account {saved.Account} on {saved.CreationDatetime:u}");
            }

            return;
        }

        _logger.LogWarning(
            "Saved the remote signer's Spark identity {Identity} for seed {SeedFingerprint} account {Account}: every later handshake " +
            "must serve it. Check it against seed-ceremony verify --spark-account {Account} when convenient; to change it, see docs/spark-swaps.md",
            served, saved.SeedFingerprint, saved.Account, saved.Account);

        try
        {
            await scope.ServiceProvider.GetRequiredService<IAuditService>().LogSystemAsync(AuditActionType.Create, AuditEventType.Success,
                AuditObjectType.Wallet, $"spark-signer/{saved.SeedFingerprint}/{saved.Account}",
                new { SeedFingerprint = saved.SeedFingerprint, saved.Account, IdentityPublicKey = served, Reason = "Saved on the first remote signer handshake" });
        }
        catch (Exception e)
        {
            // The identity is saved; a failed audit entry must not take Spark down
            _logger.LogError(e, "Could not audit the saved Spark identity {Identity}", served);
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
