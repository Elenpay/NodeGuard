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

using System.Reflection;
using FluentAssertions;
using NBitcoin;

namespace NodeGuard.Shared;

/// <summary>
/// The PSBTSign approval component must validate a pasted PSBT against the request's real template, so a
/// signed PSBT for a different transaction is rejected before it can be stored as an approval. ValidatePSBT
/// delegates to <see cref="NodeGuard.Helpers.PsbtApprovalValidator"/> using the component's TemplatePsbtString
/// parameter; these tests pin that the component wires the template through and surfaces a rejection.
///
/// ValidatePSBT is a private method on a Razor component and bUnit is not referenced, so the component cannot
/// be rendered in a test. The method is pure with respect to its argument — no injected services, no JS
/// interop, no render tree — so invoking it directly by reflection exercises exactly the code the Approve
/// button runs on the server (this is Blazor Server: the component's @code executes in the circuit).
/// </summary>
public class PSBTSignTests
{
    /// <summary>
    /// A PSBT for a different transaction, paying a different destination and validly signed with the
    /// signer's own key, must be rejected. ValidatePSBT returns an empty string only for a valid approval,
    /// and that empty result is what lets the Approve button proceed — so a non-empty error is required here.
    /// </summary>
    [Fact]
    public void ValidatePSBT_RejectsSignedPsbtForADifferentTransaction()
    {
        var fixture = BuildSubstitutionFixture();

        var errors = InvokeValidatePsbt(fixture.TemplateBase64, fixture.ForeignSignedBase64);

        errors.Should().NotBeNullOrWhiteSpace(
            "the pasted PSBT must be compared against the template, so a signed PSBT describing a " +
            "different transaction is refused");
    }

    /// <summary>An unsigned PSBT is rejected (every input must carry a signature).</summary>
    [Fact]
    public void ValidatePSBT_RejectsUnsignedPsbt()
    {
        var fixture = BuildSubstitutionFixture();

        var errors = InvokeValidatePsbt(fixture.TemplateBase64, fixture.ForeignUnsignedBase64);

        errors.Should().NotBeNullOrWhiteSpace("an unsigned PSBT must be rejected");
    }

    /// <summary>Unparseable input is rejected — confirms the reflection harness reaches the real method.</summary>
    [Fact]
    public void ValidatePSBT_RejectsGarbage()
    {
        var fixture = BuildSubstitutionFixture();

        var errors = InvokeValidatePsbt(fixture.TemplateBase64, "not-a-psbt");

        errors.Should().NotBeNullOrWhiteSpace("unparseable input must be rejected");
    }

    // ---- fixture -------------------------------------------------------------------------------------

    private sealed record SubstitutionFixture(
        string TemplateBase64,
        string ForeignSignedBase64,
        string ForeignUnsignedBase64);

    /// <summary>
    /// Builds the template the approver is asked to sign, plus their own transaction spending the same
    /// wallet UTXO to a different address — one signed, one unsigned.
    /// </summary>
    private static SubstitutionFixture BuildSubstitutionFixture()
    {
        var network = Network.RegTest;

        var signingKey = new Key();
        var walletScript = signingKey.PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit);

        var funding = network.CreateTransaction();
        funding.Outputs.Add(new TxOut(Money.Coins(1m), walletScript));
        var coin = new Coin(funding, 0U);

        var approvedDestination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, network);
        var foreignDestination = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, network);

        Transaction Spend(BitcoinAddress destination, Money amount)
        {
            var tx = network.CreateTransaction();
            tx.Inputs.Add(new TxIn(coin.Outpoint));
            tx.Outputs.Add(new TxOut(amount, destination.ScriptPubKey));
            return tx;
        }

        var templatePsbt = PSBT.FromTransaction(Spend(approvedDestination, Money.Coins(0.5m)), network)
            .AddCoins(coin);

        // Same input, a different destination and amount — a different transaction id.
        var foreignTx = Spend(foreignDestination, Money.Coins(0.9m));
        var foreignUnsigned = PSBT.FromTransaction(foreignTx, network).AddCoins(coin);

        var foreignSigned = PSBT.FromTransaction(foreignTx, network).AddCoins(coin);
        foreignSigned.SignWithKeys(signingKey);

        return new SubstitutionFixture(
            templatePsbt.ToBase64(),
            foreignSigned.ToBase64(),
            foreignUnsigned.ToBase64());
    }

    /// <summary>
    /// Invokes PSBTSign's private ValidatePSBT with the real template supplied through the component's
    /// TemplatePsbtString parameter. Returns the validator's error string ("" meaning valid); a thrown
    /// exception is surfaced as an error string, since that is also a rejection.
    /// </summary>
    private static string InvokeValidatePsbt(string templateBase64, string pastedBase64)
    {
        var component = new PSBTSign
        {
            TemplatePsbtString = templateBase64,
            SigHashMode = SigHash.All,
        };

        var method = typeof(PSBTSign).GetMethod("ValidatePSBT",
                         BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("PSBTSign.ValidatePSBT not found.");

        try
        {
            return (string)method.Invoke(component, new object?[] { pastedBase64 })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return $"threw {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        }
    }
}
