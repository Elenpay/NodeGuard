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

using NSpark;
using NSpark.RemoteSigner;

namespace NodeGuard.Services.Spark;

/// <summary>
/// The checks NodeGuard makes on the remote signer's /spark/info before using it: the same contract, and a
/// policy for exactly the operators and SSP NodeGuard talks to. The identity it serves is checked afterwards,
/// against SPARK_IDENTITY_PUBKEY or the identity saved on the first handshake (<see cref="SparkSignerIdentityPin"/>).
/// </summary>
public static class SparkSignerHandshake
{
    /// <summary>Every mismatch between the signer and NodeGuard's configuration; empty when they agree.</summary>
    public static IReadOnlyList<string> Verify(SparkSignerInfo info, SparkOptions options)
    {
        var problems = new List<string>();

        if (info.ContractVersion != SparkSignerContract.Version)
        {
            problems.Add($"the signer speaks contract version {info.ContractVersion}, NodeGuard {SparkSignerContract.Version}");
        }

        if (!IsCompressedPublicKeyHex(info.IdentityPublicKey))
        {
            problems.Add($"the signer's Spark identity ({info.IdentityPublicKey}) is not a compressed public key");
        }

        var operators = options.SigningOperators.Select(o => o.IdentityPublicKeyHex.ToLowerInvariant()).ToHashSet();
        if (!operators.SetEquals(info.OperatorKeys.Select(k => k.ToLowerInvariant())))
        {
            problems.Add("the signer's SPARK_OPERATOR_KEYS are not NodeGuard's Spark operators");
        }

        if (info.Threshold != options.EffectiveSigningThreshold)
        {
            problems.Add($"the signer's SPARK_THRESHOLD is {info.Threshold}, NodeGuard's operators use {options.EffectiveSigningThreshold}");
        }

        var ssp = options.EffectiveSspIdentityPublicKeyHex?.ToLowerInvariant();
        if (ssp is null || !info.AllowedReceivers.Any(r => string.Equals(r, ssp, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"the signer's SPARK_ALLOWED_RECEIVERS do not include NodeGuard's SSP ({ssp ?? "unknown"})");
        }

        return problems;
    }

    /// <summary>
    /// 66 hex characters starting 02 or 03: what NodeGuard may save as the signer's identity. Not checked on the
    /// curve; the operators do that.
    /// </summary>
    private static bool IsCompressedPublicKeyHex(string? value) =>
        value is { Length: 66 } && (value.StartsWith("02", StringComparison.Ordinal) || value.StartsWith("03", StringComparison.Ordinal)) &&
        value.All(Uri.IsHexDigit);
}
