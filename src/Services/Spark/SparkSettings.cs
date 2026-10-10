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
using NodeGuard.Data.Models;
using NodeGuard.Helpers;
using NSpark;

namespace NodeGuard.Services.Spark;

/// <summary>Where NodeGuard's Spark keys live (SPARK_SIGNER).</summary>
public enum SparkSignerMode
{
    /// <summary>
    /// <c>wallet</c> (default, temporary): Spark wallets created on the Wallets page, their mnemonics encrypted in
    /// the database and their keys derived in-process. Each node swaps through the Spark wallet it is given.
    /// </summary>
    Wallet,

    /// <summary><c>remote</c>: one Spark wallet in the remote signer Lambda, under its /spark/ routes, for every node.</summary>
    Remote
}

/// <summary>
/// A Spark wallet: a Spark wallet row by its id (SPARK_SIGNER=wallet), or, with no id, the remote signer's
/// single wallet (SPARK_SIGNER=remote).
/// </summary>
public readonly record struct SparkWalletRef(int? WalletId)
{
    public static readonly SparkWalletRef RemoteSigner = new((int?)null);

    public override string ToString() => WalletId is { } id ? $"Spark wallet {id}" : "the remote signer's Spark wallet";
}

/// <summary>
/// NodeGuard's Spark configuration, read once from the environment at startup. Invalid settings fail the
/// startup while Spark is enabled, as other NodeGuard settings do.
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

    /// <summary>The remote signer's seed that holds the Spark keys (SPARK_SEED_FINGERPRINT; remote signer only).</summary>
    public string? SeedFingerprint { get; init; }

    /// <summary>The remote signer's Spark account index (SPARK_ACCOUNT, 0 by default; remote signer only).</summary>
    public int Account { get; init; }

    /// <summary>The Spark identity the remote signer must serve (SPARK_IDENTITY_PUBKEY; remote signer only).</summary>
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
    /// Most sats a Spark wallet may hold, counting a new swap (SPARK_MAX_BALANCE_SATS), unless the wallet has its
    /// own: Spark balances are only meant to pass through.
    /// </summary>
    public long MaxBalanceSats { get; init; } = DefaultMaxBalanceSats;

    /// <summary>The remote signer's Function URL (REMOTE_SIGNER_ENDPOINT), signed with SigV4.</summary>
    public Uri? SignerEndpoint { get; init; }

    /// <summary>A local Lambda runtime interface emulator to reach the signer through instead (never on mainnet).</summary>
    public Uri? SignerRieUrl { get; init; }

    /// <summary>
    /// The Spark wallet <paramref name="node"/> swaps through: its Spark wallet (SPARK_SIGNER=wallet), or the
    /// remote signer's (SPARK_SIGNER=remote). Null when the node can't use Spark: Spark is disabled, or the node
    /// has no Spark wallet.
    /// </summary>
    public SparkWalletRef? WalletFor(Node node) => !Enabled
        ? null
        : SignerMode == SparkSignerMode.Remote
            ? SparkWalletRef.RemoteSigner
            : node.SparkWalletId is { } id ? new SparkWalletRef(id) : null;

    /// <summary>The most a Spark wallet may hold: its own max balance, else SPARK_MAX_BALANCE_SATS.</summary>
    public long MaxBalanceFor(Wallet? sparkWallet) => sparkWallet?.SparkMaxBalanceSats ?? MaxBalanceSats;

    /// <summary>
    /// Reads the SPARK_* variables. <paramref name="remoteSignerEndpoint"/> is NodeGuard's REMOTE_SIGNER_ENDPOINT,
    /// shared with PSBT signing. SPARK_ENABLED defaults to true on mainnet, where Lightspark's operators and SSP
    /// are the defaults, and to false elsewhere.
    /// </summary>
    /// <exception cref="InvalidOperationException">Spark is enabled and the settings are invalid; lists every problem.</exception>
    public static SparkSettings FromEnvironment(Func<string, string?> env, Network network, string? remoteSignerEndpoint)
    {
        var isMainnet = network == NBitcoin.Network.Main;
        var enabled = env("SPARK_ENABLED") is { Length: > 0 } enabledValue ? StringHelper.IsTrue(enabledValue) : isMainnet;
        if (!enabled)
        {
            return Disabled;
        }

        var errors = new List<string>();
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

        var mode = SparkSignerMode.Wallet;
        switch (env("SPARK_SIGNER")?.Trim().ToLowerInvariant())
        {
            case null or "" or "wallet":
                break;
            case "remote":
                mode = SparkSignerMode.Remote;
                break;
            default:
                errors.Add("SPARK_SIGNER must be wallet (Spark wallets created in NodeGuard) or remote (the remote signer)");
                break;
        }

        // The remote signer serves one Spark wallet, configured here. Spark wallets carry their own keys and account
        string? fingerprint = null;
        string? identity = null;
        var account = 0;
        if (mode == SparkSignerMode.Remote)
        {
            fingerprint = env("SPARK_SEED_FINGERPRINT");
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                errors.Add("SPARK_SEED_FINGERPRINT is required with the remote signer: the master fingerprint of the seed that holds the Spark keys");
            }
            else if (!HDFingerprint.TryParse(fingerprint.Trim(), out var parsedFingerprint))
            {
                errors.Add("SPARK_SEED_FINGERPRINT must be a master fingerprint (8 hex characters)");
            }
            else
            {
                fingerprint = parsedFingerprint.ToString();
            }

            if (env("SPARK_ACCOUNT") is { Length: > 0 } accountValue &&
                (!int.TryParse(accountValue, out account) || account < 0))
            {
                errors.Add("SPARK_ACCOUNT must be the Spark account index (0 or more), the same as the signer's");
            }

            identity = env("SPARK_IDENTITY_PUBKEY");
            if (identity is { Length: > 0 } && !IsCompressedPublicKey(identity))
            {
                errors.Add("SPARK_IDENTITY_PUBKEY must be a compressed public key (hex)");
            }
            else if (string.IsNullOrWhiteSpace(identity))
            {
                identity = null;
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
            throw new InvalidOperationException("Invalid Spark settings (set SPARK_ENABLED=false to turn Spark off): " + string.Join("; ", errors));
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
