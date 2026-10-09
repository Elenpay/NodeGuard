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

using Amazon.Runtime;
using NodeGuard.Data.Models;
using NodeGuard.Data.Repositories.Interfaces;
using NSpark;
using NSpark.RemoteSigner;
using NSpark.Signer;

namespace NodeGuard.Services.Spark;

/// <summary>Spark cannot be used: misconfigured, refused by the signer or unreachable. The message says why.</summary>
public sealed class SparkUnavailableException(string message) : Exception(message);

/// <summary>Provides a Spark wallet's signer once it is known to be the one NodeGuard expects.</summary>
public interface ISparkSignerProvider
{
    /// <exception cref="SparkUnavailableException">The signer is not the expected one, or cannot be reached.</exception>
    Task<ISparkSigner> GetSignerAsync(SparkWalletRef wallet, SparkOptions options, CancellationToken ct);
}

/// <summary>
/// SPARK_SIGNER=wallet: the keys of a Spark wallet row, derived from its decrypted mnemonic and checked against
/// the identity recorded when it was created. SPARK_SIGNER=remote: NSpark's RemoteSparkSigner over the signer's
/// Function URL (or a local emulator), accepted only after its /spark/info handshake.
/// </summary>
public sealed class SparkSignerProvider : ISparkSignerProvider
{
    private readonly SparkSettings _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly ISparkSeedProtector _protector;
    private readonly HttpClient _http;
    private readonly Func<ImmutableCredentials> _awsCredentials;
    private readonly string? _awsRegion;

    public SparkSignerProvider(SparkSettings settings, IServiceScopeFactory scopes, ISparkSeedProtector protector, HttpClient http,
        Func<ImmutableCredentials> awsCredentials, string? awsRegion)
    {
        _settings = settings;
        _scopes = scopes;
        _protector = protector;
        _http = http;
        _awsCredentials = awsCredentials;
        _awsRegion = awsRegion;
    }

    public Task<ISparkSigner> GetSignerAsync(SparkWalletRef wallet, SparkOptions options, CancellationToken ct) =>
        (_settings.SignerMode, wallet.WalletId) switch
        {
            (SparkSignerMode.Remote, null) => RemoteAsync(options, ct),
            (SparkSignerMode.Wallet, { } id) => WalletAsync(id),
            (SparkSignerMode.Remote, _) => throw new SparkUnavailableException(
                "SPARK_SIGNER=remote: Spark swaps go through the remote signer's wallet, not Spark wallets in NodeGuard"),
            _ => throw new SparkUnavailableException("SPARK_SIGNER=wallet: Spark swaps go through a Spark wallet, and none was given")
        };

    private async Task<ISparkSigner> RemoteAsync(SparkOptions options, CancellationToken ct)
    {
        ISparkSignerTransport transport = _settings.SignerRieUrl is { } rie
            ? new RieSparkSignerTransport(_http, rie)
            : new FunctionUrlSparkSignerTransport(_http, _settings.SignerEndpoint!, _awsRegion!, _awsCredentials);
        var signer = new RemoteSparkSigner(transport, _settings.SeedFingerprint!);

        SparkSignerInfo info;
        try
        {
            info = await signer.GetInfoAsync(ct);
        }
        catch (SparkRemoteSignerException e)
        {
            throw new SparkUnavailableException($"the remote signer did not serve /spark/info: {e.Message}");
        }

        var problems = SparkSignerHandshake.Verify(info, _settings.IdentityPublicKey!, options);
        if (problems.Count > 0)
        {
            throw new SparkUnavailableException("the remote signer is not the one NodeGuard expects: " + string.Join("; ", problems));
        }

        return signer;
    }

    private async Task<ISparkSigner> WalletAsync(int walletId)
    {
        using var scope = _scopes.CreateScope();
        var wallet = await scope.ServiceProvider.GetRequiredService<IWalletRepository>().GetById(walletId);
        if (wallet is not { Kind: WalletKind.Spark, SparkEncryptedMnemonic.Length: > 0 })
        {
            throw new SparkUnavailableException($"wallet {walletId} is not a Spark wallet");
        }

        var signer = SparkSigner.FromMnemonic(_protector.Unprotect(wallet.SparkEncryptedMnemonic), wallet.SparkAccount ?? 0);

        var identity = Convert.ToHexString(await signer.GetIdentityPublicKeyAsync()).ToLowerInvariant();
        if (wallet.SparkIdentityPublicKey is { } expected && !string.Equals(identity, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new SparkUnavailableException($"its seed derives Spark identity {identity}, not the {expected} recorded at creation");
        }

        return signer;
    }
}
