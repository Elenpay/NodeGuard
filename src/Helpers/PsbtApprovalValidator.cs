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

namespace NodeGuard.Helpers;

/// <summary>
/// Validates an approver-submitted PSBT against the template PSBT the request was raised for.
///
/// This exists because an approval is inherently untrusted input: the approver copies the template, signs it
/// offline in their own wallet or hardware device, and pastes base64 back into a free-text field. The only
/// thing that makes such an approval meaningful is proving it describes the transaction that was actually
/// approved.
///
/// It replaces validation that previously lived inside PSBTSign.razor and compared the submitted PSBT against
/// ITSELF — both sides were parsed from the same argument — which made the txid and UTXO checks tautologies
/// that could never fail. Note that PSBTSign runs server side (this is Blazor Server), so the original defect
/// was not "the check was only in the browser"; the server-side check existed and was simply wrong. Being a
/// plain class, this can additionally be enforced in the repositories, which is the better home: the
/// repository is the persistence boundary every path must cross, and it loads the template from the database
/// by request id rather than trusting a component parameter a caller might leave unset, stale, or pointed at
/// a different request.
/// </summary>
public static class PsbtApprovalValidator
{
    public record Result(bool IsValid, string? Error)
    {
        public static Result Ok { get; } = new(true, null);

        public static Result Fail(string error) => new(false, error);
    }

    /// <summary>
    /// Validates a submitted approval PSBT.
    /// </summary>
    /// <param name="templatePsbtBase64">The template NodeGuard generated for this request.</param>
    /// <param name="submittedPsbtBase64">What the approver submitted.</param>
    /// <param name="expectedSigHash">
    /// Sighash the operation requires — SigHash.All for withdrawals, SigHash.None for channel operations.
    /// </param>
    /// <param name="network">Network to parse against.</param>
    /// <param name="existingApprovals">
    /// PSBTs already stored as approvals of this request, if any. When supplied, a submission carrying the
    /// same set of signatures as an existing approval is rejected, so one signer cannot advance the threshold
    /// by submitting the same signature twice. The comparison is over signing public keys rather than bytes,
    /// so re-serializing the PSBT — or re-signing with a hardware wallet that uses randomized nonces — does
    /// not evade it.
    /// </param>
    public static Result Validate(string? templatePsbtBase64, string? submittedPsbtBase64,
        SigHash expectedSigHash, Network network, IEnumerable<string>? existingApprovals = null)
    {
        if (string.IsNullOrWhiteSpace(submittedPsbtBase64)
            || !PSBT.TryParse(submittedPsbtBase64, network, out var submitted))
        {
            return Result.Fail("Invalid PSBT, it could not be parsed.");
        }

        if (string.IsNullOrWhiteSpace(templatePsbtBase64)
            || !PSBT.TryParse(templatePsbtBase64, network, out var template))
        {
            return Result.Fail("Invalid template PSBT, it could not be parsed.");
        }

        // THE check. The submitted PSBT must describe the very transaction that was approved — same inputs,
        // same outputs, same amounts, same destinations. Because NodeGuard builds the template itself from
        // the request's destinations, matching the template's transaction hash transitively guarantees the
        // approved destinations and amounts. Everything below is defence in depth.
        if (submitted.GetGlobalTransaction().GetHash() != template.GetGlobalTransaction().GetHash())
        {
            return Result.Fail(
                "Invalid PSBT, the transaction does not match the one this request was created for.");
        }

        var templateOutpoints = template.Inputs.Select(x => x.PrevOut).ToHashSet();
        var submittedOutpoints = submitted.Inputs.Select(x => x.PrevOut).ToHashSet();
        if (!templateOutpoints.SetEquals(submittedOutpoints))
        {
            return Result.Fail("Invalid PSBT, the UTXOs do not match the ones this request was created for.");
        }

        if (!submitted.Inputs.All(x => x.PartialSigs.Any()))
        {
            return Result.Fail($"Invalid PSBT, every input must be signed with Sighash: {expectedSigHash}.");
        }

        // Note ".All" at both levels. The original check was ".Any(input => input.PartialSigs.All(...))", so
        // a single conforming input satisfied the entire PSBT.
        if (!submitted.Inputs.All(x => x.PartialSigs.All(y => y.Value.SigHash == expectedSigHash)))
        {
            return Result.Fail($"Invalid PSBT, every signature must use Sighash: {expectedSigHash}.");
        }

        if (existingApprovals is not null)
        {
            var submittedSignatures = SignatureFingerprint(submitted);

            foreach (var existing in existingApprovals)
            {
                if (!PSBT.TryParse(existing, network, out var existingPsbt)) continue;

                if (SignatureFingerprint(existingPsbt).SetEquals(submittedSignatures))
                {
                    return Result.Fail("This request has already been signed with that key.");
                }
            }
        }

        return Result.Ok;
    }

    /// <summary>
    /// Convenience overload for UI validators, which expect an error string ("" meaning valid).
    /// </summary>
    public static string ValidateForDisplay(string? templatePsbtBase64, string? submittedPsbtBase64,
        SigHash expectedSigHash, Network network)
        => Validate(templatePsbtBase64, submittedPsbtBase64, expectedSigHash, network).Error ?? string.Empty;

    /// <summary>
    /// Identifies WHICH signatures a PSBT carries, as a set of "outpoint:pubkey" pairs. Two submissions with
    /// the same fingerprint contribute the same signing authority however their bytes differ.
    /// </summary>
    private static HashSet<string> SignatureFingerprint(PSBT psbt)
        => psbt.Inputs
            .SelectMany(input => input.PartialSigs.Select(sig => $"{input.PrevOut}:{sig.Key}"))
            .ToHashSet();
}
