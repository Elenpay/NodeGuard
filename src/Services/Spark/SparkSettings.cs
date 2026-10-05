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

using NBitcoin;
using NodeGuard.Helpers;
using NSpark;

namespace NodeGuard.Services.Spark;

/// <summary>Where NodeGuard's Spark keys live.</summary>
public enum SparkSignerMode
{
    /// <summary>Derived in-process from an internal wallet's mnemonic (no remote signer).</summary>
    Embedded,

    /// <summary>In the remote signer Lambda, under its /spark/ routes (ENABLE_REMOTE_SIGNER).</summary>
    Remote
}

/// <summary>
/// NodeGuard's Spark configuration, read once from the environment at startup. Invalid settings with
/// SPARK_ENABLED set fail the startup, as other NodeGuard settings do.
/// </summary>
public sealed class SparkSettings
{
    public const long DefaultMaxExitFeeSats = 20_000;
    public const long DefaultMaxBalanceSats = 10_000_000;

    public static readonly SparkSettings Disabled = new();

    public bool Enabled { get; init; }

    public SparkNetwork Network { get; init; }

    public bool IsMainnet => Network == SparkNetwork.Mainnet;

    /// <summary>Signing operators; NSpark's defaults (Lightspark, Breez, Flashnet) on mainnet unless SPARK_OPERATORS is set.</summary>
    public IReadOnlyList<SigningOperatorConfig> Operators { get; init; } = [];

    /// <summary>Operators' signing threshold (SPARK_THRESHOLD); null derives it from the operator count.</summary>
    public uint? Threshold { get; init; }

    public string SspUrl { get; init; } = SparkOptions.DefaultSspUrl;

    /// <summary>
    /// The SSP's identity key. Null on mainnet means Lightspark's default SSP key; off mainnet it is
    /// fetched from the SSP (open-ssp's /identity).
    /// </summary>
    public string? SspIdentityPublicKey { get; init; }

    /// <summary>Directory of operator TLS certificates to pin (self-signed regtest operators; never on mainnet).</summary>
    public string? OperatorCertsDirectory { get; init; }

    public SparkSignerMode SignerMode { get; init; }

    /// <summary>
    /// Master fingerprint of the seed that holds the Spark keys: the remote signer's
    /// SPARK_SEED_FINGERPRINT, or the internal wallet whose mnemonic is used in embedded mode. Null
    /// only in a dev environment in embedded mode, meaning the current internal wallet.
    /// </summary>
    public string? SeedFingerprint { get; init; }

    /// <summary>Spark account index (SPARK_ACCOUNT, 0 by default); must match the signer's.</summary>
    public int Account { get; init; }

    /// <summary>The Spark identity NodeGuard expects (SPARK_IDENTITY_PUBKEY); required with the remote signer.</summary>
    public string? IdentityPublicKey { get; init; }

    /// <summary>Highest fee accepted for the cooperative exit of a swap (SPARK_MAX_EXIT_FEE_SATS).</summary>
    public long MaxExitFeeSats { get; init; } = DefaultMaxExitFeeSats;

    /// <summary>
    /// How often paid Spark swaps exit on-chain, in minutes (SPARK_EXIT_INTERVAL_MINUTES); null means 10, or 1
    /// in a dev environment.
    /// </summary>
    public int? ExitIntervalMinutes { get; init; }

    /// <summary>How often paid Spark swaps exit on-chain, in minutes: <see cref="ExitIntervalMinutes"/> or its default.</summary>
    public int ExitIntervalOrDefault(bool isDevEnvironment) => ExitIntervalMinutes ?? (isDevEnvironment ? 1 : 10);

    /// <summary>
    /// Most sats the transit wallet may hold, counting a new swap (SPARK_MAX_BALANCE_SATS): Spark balances
    /// are only meant to pass through.
    /// </summary>
    public long MaxBalanceSats { get; init; } = DefaultMaxBalanceSats;

    /// <summary>The remote signer's Function URL (REMOTE_SIGNER_ENDPOINT), signed with SigV4.</summary>
    public Uri? SignerEndpoint { get; init; }

    /// <summary>A local Lambda runtime interface emulator to reach the signer through instead (never on mainnet).</summary>
    public Uri? SignerRieUrl { get; init; }

    /// <summary>
    /// Reads the SPARK_* variables. <paramref name="remoteSigner"/> and
    /// <paramref name="remoteSignerEndpoint"/> are NodeGuard's ENABLE_REMOTE_SIGNER and
    /// REMOTE_SIGNER_ENDPOINT, shared with PSBT signing.
    /// </summary>
    /// <exception cref="InvalidOperationException">SPARK_ENABLED is set and the settings are invalid; lists every problem.</exception>
    public static SparkSettings FromEnvironment(Func<string, string?> env, Network network, bool isDevEnvironment,
        bool remoteSigner, string? remoteSignerEndpoint)
    {
        if (!StringHelper.IsTrue(env("SPARK_ENABLED")))
        {
            return Disabled;
        }

        var errors = new List<string>();
        var isMainnet = network == NBitcoin.Network.Main;
        if (!isMainnet && network != NBitcoin.Network.RegTest)
        {
            errors.Add($"Spark runs on mainnet or regtest, not {network}");
        }

        var operators = ParseOperators(env("SPARK_OPERATORS"), errors);
        if (operators.Count == 0)
        {
            if (isMainnet) operators = [.. SparkOptions.GetDefaultOperators(SparkNetwork.Mainnet)];
            else errors.Add("SPARK_OPERATORS is required off mainnet");
        }

        uint? threshold = null;
        if (env("SPARK_THRESHOLD") is { Length: > 0 } thresholdValue)
        {
            if (uint.TryParse(thresholdValue, out var parsed) && parsed >= 1 && parsed <= operators.Count) threshold = parsed;
            else errors.Add($"SPARK_THRESHOLD must be between 1 and the number of operators ({operators.Count})");
        }

        var sspUrl = env("SPARK_SSP_URL");
        if (string.IsNullOrWhiteSpace(sspUrl))
        {
            if (!isMainnet) errors.Add("SPARK_SSP_URL is required off mainnet");
            sspUrl = SparkOptions.DefaultSspUrl;
        }

        var sspIdentity = env("SPARK_SSP_IDENTITY_PUBKEY");
        if (sspIdentity is { Length: > 0 } && !IsCompressedPublicKey(sspIdentity))
        {
            errors.Add("SPARK_SSP_IDENTITY_PUBKEY must be a compressed public key (hex)");
        }
        else if (string.IsNullOrWhiteSpace(sspIdentity) && isMainnet && sspUrl != SparkOptions.DefaultSspUrl)
        {
            errors.Add("SPARK_SSP_IDENTITY_PUBKEY is required with a custom SPARK_SSP_URL on mainnet");
        }

        var certsDirectory = env("SPARK_OPERATOR_CERTS_DIR");
        if (!string.IsNullOrWhiteSpace(certsDirectory) && isMainnet)
        {
            errors.Add("SPARK_OPERATOR_CERTS_DIR pins self-signed regtest operators and is not allowed on mainnet");
        }

        var mode = remoteSigner ? SparkSignerMode.Remote : SparkSignerMode.Embedded;

        var fingerprint = env("SPARK_SEED_FINGERPRINT");
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            fingerprint = null;
            if (mode == SparkSignerMode.Remote || !isDevEnvironment)
            {
                errors.Add("SPARK_SEED_FINGERPRINT is required: the master fingerprint of the seed that holds the Spark keys");
            }
        }
        else if (!HDFingerprint.TryParse(fingerprint.Trim(), out var parsedFingerprint))
        {
            errors.Add("SPARK_SEED_FINGERPRINT must be a master fingerprint (8 hex characters)");
        }
        else
        {
            fingerprint = parsedFingerprint.ToString();
        }

        var account = 0;
        if (env("SPARK_ACCOUNT") is { Length: > 0 } accountValue &&
            (!int.TryParse(accountValue, out account) || account < 0))
        {
            errors.Add("SPARK_ACCOUNT must be the Spark account index (0 or more), the same as the signer's");
        }

        var identity = env("SPARK_IDENTITY_PUBKEY");
        if (identity is { Length: > 0 } && !IsCompressedPublicKey(identity))
        {
            errors.Add("SPARK_IDENTITY_PUBKEY must be a compressed public key (hex)");
        }
        else if (string.IsNullOrWhiteSpace(identity))
        {
            identity = null;
            if (mode == SparkSignerMode.Remote)
            {
                errors.Add("SPARK_IDENTITY_PUBKEY is required with the remote signer: the identity it must serve (seed-ceremony verify --spark-account)");
            }
        }

        var maxExitFee = DefaultMaxExitFeeSats;
        if (env("SPARK_MAX_EXIT_FEE_SATS") is { Length: > 0 } maxExitFeeValue &&
            (!long.TryParse(maxExitFeeValue, out maxExitFee) || maxExitFee <= 0))
        {
            errors.Add("SPARK_MAX_EXIT_FEE_SATS must be a positive number of sats");
        }

        int? exitInterval = null;
        if (env("SPARK_EXIT_INTERVAL_MINUTES") is { Length: > 0 } exitIntervalValue)
        {
            if (int.TryParse(exitIntervalValue, out var parsed) && parsed > 0) exitInterval = parsed;
            else errors.Add("SPARK_EXIT_INTERVAL_MINUTES must be a positive number of minutes");
        }

        var maxBalance = DefaultMaxBalanceSats;
        if (env("SPARK_MAX_BALANCE_SATS") is { Length: > 0 } maxBalanceValue &&
            (!long.TryParse(maxBalanceValue, out maxBalance) || maxBalance <= 0))
        {
            errors.Add("SPARK_MAX_BALANCE_SATS must be a positive number of sats");
        }

        Uri? endpoint = null;
        Uri? rieUrl = null;
        if (mode == SparkSignerMode.Remote)
        {
            if (!Uri.TryCreate(remoteSignerEndpoint, UriKind.Absolute, out endpoint) || endpoint.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(endpoint.Query))
            {
                errors.Add("REMOTE_SIGNER_ENDPOINT must be the signer's Function URL with no path (https://<id>.lambda-url.<region>.on.aws/) for Spark's /spark/ routes");
            }

            if (env("SPARK_SIGNER_RIE_URL") is { Length: > 0 } rie)
            {
                if (isMainnet) errors.Add("SPARK_SIGNER_RIE_URL (a local Lambda emulator) is not allowed on mainnet");
                else if (!Uri.TryCreate(rie, UriKind.Absolute, out rieUrl)) errors.Add("SPARK_SIGNER_RIE_URL must be an absolute URL");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid Spark settings (SPARK_ENABLED is set): " + string.Join("; ", errors));
        }

        return new SparkSettings
        {
            Enabled = true,
            Network = isMainnet ? SparkNetwork.Mainnet : SparkNetwork.Regtest,
            Operators = operators,
            Threshold = threshold,
            SspUrl = sspUrl,
            SspIdentityPublicKey = string.IsNullOrWhiteSpace(sspIdentity) ? null : sspIdentity.Trim().ToLowerInvariant(),
            OperatorCertsDirectory = string.IsNullOrWhiteSpace(certsDirectory) ? null : certsDirectory,
            SignerMode = mode,
            SeedFingerprint = fingerprint,
            Account = account,
            IdentityPublicKey = identity?.Trim().ToLowerInvariant(),
            MaxExitFeeSats = maxExitFee,
            ExitIntervalMinutes = exitInterval,
            MaxBalanceSats = maxBalance,
            SignerEndpoint = endpoint,
            SignerRieUrl = rieUrl
        };
    }

    /// <summary>NSpark's options for these settings, with the SSP identity once known.</summary>
    public SparkOptions ToSparkOptions(string? sspIdentityPublicKey) => new()
    {
        Network = Network,
        SigningOperators = Operators.ToArray(),
        SigningThreshold = Threshold,
        SspUrl = SspUrl,
        SspIdentityPublicKeyHex = sspIdentityPublicKey ?? SspIdentityPublicKey
    };

    /// <summary>"address|identifier|identity public key" entries separated by ';'.</summary>
    private static List<SigningOperatorConfig> ParseOperators(string? value, List<string> errors)
    {
        var operators = new List<SigningOperatorConfig>();
        foreach (var entry in (value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !Uri.TryCreate(parts[0], UriKind.Absolute, out _) || parts[1].Length != 64 ||
                !parts[1].All(Uri.IsHexDigit) || !IsCompressedPublicKey(parts[2]))
            {
                errors.Add("SPARK_OPERATORS entries must be 'address|64-hex identifier|compressed public key hex', separated by ';'");
                return [];
            }

            operators.Add(new SigningOperatorConfig(parts[0], parts[1].ToLowerInvariant(), parts[2].ToLowerInvariant()));
        }

        return operators;
    }

    private static bool IsCompressedPublicKey(string hex)
    {
        hex = hex.Trim();
        return hex.Length == 66 && hex.All(Uri.IsHexDigit) && PubKey.TryCreatePubKey(Convert.FromHexString(hex), out var key) &&
               key.IsCompressed;
    }
}
