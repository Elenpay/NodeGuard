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

using FluentAssertions;
using NBitcoin;
using NodeGuard.Helpers;

namespace NodeGuard.Tests.Helpers;

/// <summary>
/// An approver's PSBT must be bound to the transaction the withdrawal request was raised for: it is rejected
/// unless its global transaction matches the template, every input is signed with the required sighash, and
/// its signatures do not duplicate an existing approval.
///
/// Half of these assert those rejections. The other half assert the accept-path, because an over-strict rule
/// is just as damaging on a treasury tool: if legitimate approvals stop being accepted, funds become
/// unspendable. So the happy path and the multi-signer M-of-N flows are covered explicitly.
/// </summary>
public class PsbtApprovalValidatorTests
{
    private static readonly Network Network = Network.RegTest;

    // ---- rejects a substituted transaction -----------------------------------------------------------

    /// <summary>
    /// A signature over a transaction of the signer's own choosing, paying a different destination, must be
    /// rejected when submitted in place of the approved one.
    /// </summary>
    [Fact]
    public void Validate_RejectsSignedPsbtForADifferentTransaction()
    {
        var f = new Fixture();
        var substitute = f.Sign(f.BuildSpend(f.AttackerAddress, Money.Coins(0.9m)), f.KeyA);

        var result = PsbtApprovalValidator.Validate(f.TemplateBase64, substitute.ToBase64(),
            SigHash.All, Network);

        result.IsValid.Should().BeFalse(
            "a PSBT describing a different transaction must never be accepted as an approval");
        result.Error.Should().Contain("does not match");
    }

    /// <summary>
    /// Same inputs and destination but a different amount — a subtler substitution than redirecting the
    /// payment, and the one an attacker would reach for to skim.
    /// </summary>
    [Fact]
    public void Validate_RejectsTamperedAmountForTheSameDestination()
    {
        var f = new Fixture();
        var skimmed = f.Sign(f.BuildSpend(f.ApprovedAddress, Money.Coins(0.4m)), f.KeyA);

        var result = PsbtApprovalValidator.Validate(f.TemplateBase64, skimmed.ToBase64(),
            SigHash.All, Network);

        result.IsValid.Should().BeFalse("changing the amount changes the transaction");
    }

    /// <summary>
    /// The threshold half of the finding: one signer must not advance an M-of-N threshold by submitting their
    /// signature more than once. The fingerprint is over signing public keys, not bytes, so re-serializing or
    /// re-signing with a randomized nonce does not evade it.
    /// </summary>
    [Fact]
    public void Validate_RejectsASecondSubmissionCarryingTheSameSignature()
    {
        var f = new Fixture();
        var firstApproval = f.Sign(f.TemplateTransaction, f.KeyA);
        var resubmission = f.Sign(f.TemplateTransaction, f.KeyA);

        var result = PsbtApprovalValidator.Validate(f.TemplateBase64, resubmission.ToBase64(),
            SigHash.All, Network, new[] { firstApproval.ToBase64() });

        result.IsValid.Should().BeFalse("the same key must count once, however many times it is submitted");
        result.Error.Should().Contain("already been signed");
    }

    [Fact]
    public void Validate_RejectsUnsignedPsbt()
    {
        var f = new Fixture();

        var result = PsbtApprovalValidator.Validate(f.TemplateBase64, f.TemplateBase64, SigHash.All, Network);

        result.IsValid.Should().BeFalse("an approval must actually carry a signature");
    }

    [Fact]
    public void Validate_RejectsWrongSigHash()
    {
        var f = new Fixture();
        var signedWithAll = f.Sign(f.TemplateTransaction, f.KeyA);

        // Channel operations require SIGHASH_NONE; a SIGHASH_ALL signature must not satisfy them.
        var result = PsbtApprovalValidator.Validate(f.TemplateBase64, signedWithAll.ToBase64(),
            SigHash.None, Network);

        result.IsValid.Should().BeFalse("the sighash must be the one the operation requires");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-psbt")]
    public void Validate_RejectsUnparseableSubmission(string submitted)
    {
        var f = new Fixture();

        var result = PsbtApprovalValidator.Validate(f.TemplateBase64, submitted, SigHash.All, Network);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("could not be parsed");
    }

    [Fact]
    public void Validate_RejectsWhenThereIsNoTemplateToCompareAgainst()
    {
        var f = new Fixture();
        var approval = f.Sign(f.TemplateTransaction, f.KeyA);

        var result = PsbtApprovalValidator.Validate(null, approval.ToBase64(), SigHash.All, Network);

        result.IsValid.Should().BeFalse(
            "with no template there is nothing to bind the approval to, so it must not be trusted");
    }

    // ---- the fix must not break legitimate signing ---------------------------------------------------

    /// <summary>
    /// The single most important test in this file. An honestly-signed approval of the approved transaction
    /// must be accepted — otherwise the fix has converted a confidentiality problem into frozen funds.
    /// </summary>
    [Fact]
    public void Validate_AcceptsAnHonestApproval()
    {
        var f = new Fixture();
        var approval = f.Sign(f.TemplateTransaction, f.KeyA);

        var result = PsbtApprovalValidator.Validate(f.TemplateBase64, approval.ToBase64(),
            SigHash.All, Network);

        result.IsValid.Should().BeTrue($"a correctly signed approval must be accepted, got: {result.Error}");
        result.Error.Should().BeNull();
    }

    /// <summary>
    /// M-of-N must still work: a second approval from a DIFFERENT key is a distinct signature and has to be
    /// accepted even though one approval is already on file.
    /// </summary>
    [Fact]
    public void Validate_AcceptsASecondApprovalFromADifferentKey()
    {
        var f = new Fixture();
        var firstApproval = f.Sign(f.TemplateTransaction, f.KeyA);
        var secondApproval = f.Sign(f.TemplateTransaction, f.KeyB);

        var result = PsbtApprovalValidator.Validate(f.TemplateBase64, secondApproval.ToBase64(),
            SigHash.All, Network, new[] { firstApproval.ToBase64() });

        result.IsValid.Should().BeTrue(
            $"a different keyholder's signature must be accepted, got: {result.Error}");
    }

    /// <summary>
    /// A user holding two keys in the same wallet submits both signatures in one PSBT — a legitimate setup that
    /// a naive "one approval per user" rule would have broken.
    /// </summary>
    [Fact]
    public void Validate_AcceptsAnApprovalCarryingTwoKeysSignatures()
    {
        var f = new Fixture();
        var approval = f.Sign(f.TemplateTransaction, f.KeyA, f.KeyB);

        var result = PsbtApprovalValidator.Validate(f.TemplateBase64, approval.ToBase64(),
            SigHash.All, Network);

        result.IsValid.Should().BeTrue($"multiple signatures in one PSBT are valid, got: {result.Error}");
    }

    [Fact]
    public void ValidateForDisplay_ReturnsEmptyStringForAnHonestApproval()
    {
        var f = new Fixture();
        var approval = f.Sign(f.TemplateTransaction, f.KeyA);

        var errors = PsbtApprovalValidator.ValidateForDisplay(f.TemplateBase64, approval.ToBase64(),
            SigHash.All, Network);

        errors.Should().BeEmpty("the UI overload signals validity with an empty string");
    }

    [Fact]
    public void ValidateForDisplay_ReturnsAnErrorForASubstitution()
    {
        var f = new Fixture();
        var substitute = f.Sign(f.BuildSpend(f.AttackerAddress, Money.Coins(0.9m)), f.KeyA);

        var errors = PsbtApprovalValidator.ValidateForDisplay(f.TemplateBase64, substitute.ToBase64(),
            SigHash.All, Network);

        errors.Should().NotBeNullOrWhiteSpace();
    }

    // ---- fixture -------------------------------------------------------------------------------------

    /// <summary>
    /// A 2-of-2 P2WSH multisig UTXO — the shape NodeGuard's cold wallets actually use — plus an approved
    /// destination and an attacker-controlled one. Using a real multisig matters: with a single-key P2WPKH
    /// output only one key can produce a signature, so the multi-signer cases could not be expressed.
    /// </summary>
    private sealed class Fixture
    {
        internal Key KeyA { get; } = new();
        internal Key KeyB { get; } = new();
        internal BitcoinAddress ApprovedAddress { get; }
        internal BitcoinAddress AttackerAddress { get; }
        internal Transaction TemplateTransaction { get; }
        internal string TemplateBase64 { get; }

        private readonly ScriptCoin _coin;

        internal Fixture()
        {
            var redeem = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, KeyA.PubKey, KeyB.PubKey);

            var funding = Network.CreateTransaction();
            funding.Outputs.Add(new TxOut(Money.Coins(1m), redeem.WitHash.ScriptPubKey));
            _coin = new Coin(funding, 0U).ToScriptCoin(redeem);

            ApprovedAddress = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);
            AttackerAddress = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network);

            TemplateTransaction = BuildSpend(ApprovedAddress, Money.Coins(0.5m));
            TemplateBase64 = ToPsbt(TemplateTransaction).ToBase64();
        }

        internal Transaction BuildSpend(BitcoinAddress destination, Money amount)
        {
            var tx = Network.CreateTransaction();
            tx.Inputs.Add(new TxIn(_coin.Outpoint));
            tx.Outputs.Add(new TxOut(amount, destination.ScriptPubKey));
            return tx;
        }

        internal PSBT ToPsbt(Transaction tx) => PSBT.FromTransaction(tx, Network).AddCoins(_coin);

        internal PSBT Sign(Transaction tx, params Key[] keys)
        {
            var psbt = ToPsbt(tx);
            psbt.SignWithKeys(keys);
            return psbt;
        }
    }
}
