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
using NodeGuard.Data.Repositories.Interfaces;
using NSpark;
using NSpark.RemoteSigner;
using NSpark.Signer;

namespace NodeGuard.Services.Spark;

/// <summary>Spark cannot be used: misconfigured, refused by the signer or unreachable. The message says why.</summary>
public sealed class SparkUnavailableException(string message) : Exception(message);

/// <summary>Provides the Spark signer once it is known to be the one NodeGuard expects.</summary>
public interface ISparkSignerProvider
{
    /// <exception cref="SparkUnavailableException">The signer is not the expected one, or cannot be reached.</exception>
    Task<ISparkSigner> GetSignerAsync(SparkOptions options, CancellationToken ct);
}

/// <summary>
/// Remote mode: NSpark's RemoteSparkSigner over the signer's Function URL (or a local emulator),
/// accepted only after its /spark/info handshake. Embedded mode: the Spark keys derived from the
/// internal wallet with SPARK_SEED_FINGERPRINT (the current one in a dev environment), checked
/// against SPARK_IDENTITY_PUBKEY when set.
/// </summary>
public sealed class SparkSignerProvider : ISparkSignerProvider
{
    private readonly SparkSettings _settings;
    private readonly IServiceScopeFactory _scopes;
    private readonly HttpClient _http;
    private readonly Func<ImmutableCredentials> _awsCredentials;
    private readonly string? _awsRegion;

    public SparkSignerProvider(SparkSettings settings, IServiceScopeFactory scopes, HttpClient http,
        Func<ImmutableCredentials> awsCredentials, string? awsRegion)
    {
        _settings = settings;
        _scopes = scopes;
        _http = http;
        _awsCredentials = awsCredentials;
        _awsRegion = awsRegion;
    }

    public Task<ISparkSigner> GetSignerAsync(SparkOptions options, CancellationToken ct) =>
        _settings.SignerMode == SparkSignerMode.Remote ? RemoteAsync(options, ct) : EmbeddedAsync();

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

    private async Task<ISparkSigner> EmbeddedAsync()
    {
        using var scope = _scopes.CreateScope();
        var wallets = scope.ServiceProvider.GetRequiredService<IInternalWalletRepository>();
        var wallet = _settings.SeedFingerprint is null
            ? await wallets.GetCurrentInternalWallet()
            : (await wallets.GetAll()).FirstOrDefault(w =>
                !string.IsNullOrWhiteSpace(w.MnemonicString) && w.MasterFingerprint == _settings.SeedFingerprint);

        if (string.IsNullOrWhiteSpace(wallet?.MnemonicString))
        {
            throw new SparkUnavailableException(_settings.SeedFingerprint is null
                ? "the current internal wallet has no mnemonic to derive the Spark keys from"
                : $"no internal wallet with a mnemonic has the master fingerprint {_settings.SeedFingerprint} (SPARK_SEED_FINGERPRINT)");
        }

        // A stray double space would fail BIP-39 validation rather than derive another wallet, but be precise
        var mnemonic = string.Join(' ', wallet.MnemonicString.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var signer = SparkSigner.FromMnemonic(mnemonic, _settings.Account);

        if (_settings.IdentityPublicKey is { } expected)
        {
            var identity = Convert.ToHexString(await signer.GetIdentityPublicKeyAsync()).ToLowerInvariant();
            if (identity != expected)
            {
                throw new SparkUnavailableException(
                    $"the internal wallet's Spark identity is {identity}, SPARK_IDENTITY_PUBKEY is {expected}");
            }
        }

        return signer;
    }
}
