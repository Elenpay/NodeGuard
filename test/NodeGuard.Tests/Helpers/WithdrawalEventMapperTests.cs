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
using NodeGuard.Data.Models;
using Nodeguard;
using Wallet = NodeGuard.Data.Models.Wallet;
using S = NodeGuard.Data.Models.WalletWithdrawalRequestStatus;
using E = Nodeguard.WALLET_EVENT_TYPE;

namespace NodeGuard.Helpers;

public class WithdrawalEventMapperTests
{
    private const string TxId = "4f7c3a1b2d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8";

    public static TheoryData<S, string?, S, string?, E?> Transitions => new()
    {
        { S.FinalizingPSBT, null, S.OnChainConfirmationPending, TxId, E.WalletEventWithdrawalBroadcast },
        { S.PSBTSignaturesPending, null, S.FinalizingPSBT, null, E.WalletEventWithdrawalSigning },
        { S.OnChainConfirmationPending, TxId, S.OnChainConfirmed, TxId, E.WalletEventWithdrawalConfirmed },
        { S.OnChainConfirmationPending, TxId, S.Bumped, TxId, E.WalletEventWithdrawalBumped },
        { S.OnChainConfirmationPending, TxId, S.Cancelled, TxId, E.WalletEventWithdrawalCancelled },
        { S.Pending, null, S.Cancelled, null, E.WalletEventWithdrawalCancelled },
        { S.PSBTSignaturesPending, null, S.Rejected, null, E.WalletEventWithdrawalRejected },
        { S.FinalizingPSBT, null, S.Failed, null, E.WalletEventWithdrawalFailed },
        // A failure after the broadcast keeps the TxId: still a failure, not a second broadcast
        { S.OnChainConfirmationPending, TxId, S.Failed, TxId, E.WalletEventWithdrawalFailed },
        { S.Bumped, TxId, S.OnChainConfirmationPending, TxId, E.WalletEventWithdrawalReplacementReverted },
        { S.Cancelled, TxId, S.OnChainConfirmationPending, TxId, E.WalletEventWithdrawalReplacementReverted },
        { S.Pending, null, S.PSBTSignaturesPending, null, E.WalletEventWithdrawalStatusChanged },
        // Nothing a subscriber cares about changed
        { S.Pending, null, S.Pending, null, null },
        { S.OnChainConfirmationPending, TxId, S.OnChainConfirmationPending, TxId, null },
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void TransitionEventType_MapsEachTransition(S previous, string? previousTxId, S status, string? txId, E? expected)
    {
        WithdrawalEventMapper.TransitionEventType(previous, previousTxId, status, txId).Should().Be(expected);
    }

    [Fact]
    public void ToWithdrawalEvent_CarriesTheRequestDetails()
    {
        var request = new WalletWithdrawalRequest
        {
            Id = 42,
            Status = S.Cancelled,
            TxId = TxId,
            ReferenceId = "ref-42",
            RejectCancelDescription = "Cancelled by RBF replacement request 43",
            RequestMetadata = "{\"k\":\"v\"}",
            BumpingWalletWithdrawalRequestId = 41,
            IsRbfCancellation = true,
            CustomFeeRate = 12.5m,
            Wallet = new Wallet { IsHotWallet = true },
            WalletWithdrawalRequestDestinations =
            [
                new WalletWithdrawalRequestDestination { Address = "bcrt1qdest1", Amount = 0.001m },
                new WalletWithdrawalRequestDestination { Address = "bcrt1qdest2", Amount = 0.0025m }
            ]
        };

        var e = WithdrawalEventMapper.ToWithdrawalEvent(request, previousStatus: S.OnChainConfirmationPending,
            replacedByRequestId: 43);

        e.RequestId.Should().Be(42);
        e.Status.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalCancelled);
        e.PreviousStatus.Should().Be(WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation);
        e.TxId.Should().Be(TxId);
        e.ReferenceId.Should().Be("ref-42");
        e.RejectOrCancelReason.Should().Be("Cancelled by RBF replacement request 43");
        e.RequestMetadata.Should().Be("{\"k\":\"v\"}");
        e.BumpingRequestId.Should().Be(41);
        e.ReplacedByRequestId.Should().Be(43);
        e.IsRbfCancellation.Should().BeTrue();
        e.FeeRateSatVb.Should().Be(12.5);
        e.AmountSats.Should().Be(350_000);
        e.Destinations.Select(d => (d.Address, d.AmountSats))
            .Should().Equal(("bcrt1qdest1", 100_000L), ("bcrt1qdest2", 250_000L));
        e.HasSignaturesCollected.Should().BeFalse("hot wallets have no human signers");
        e.HasSignaturesRequired.Should().BeFalse();
    }

    [Fact]
    public void ToWithdrawalEvent_ColdWallet_CountsHumanSignatures()
    {
        var request = new WalletWithdrawalRequest
        {
            Id = 7,
            Status = S.PSBTSignaturesPending,
            Wallet = new Wallet { IsHotWallet = false, MofN = 2 },
            WalletWithdrawalRequestPSBTs =
            [
                new WalletWithdrawalRequestPSBT { IsTemplatePSBT = true, PSBT = "template" },
                new WalletWithdrawalRequestPSBT { PSBT = "signed-by-a" }
            ]
        };

        var e = WithdrawalEventMapper.ToWithdrawalEvent(request);

        e.SignaturesCollected.Should().Be(1);
        e.SignaturesRequired.Should().Be(2);
        e.HasPreviousStatus.Should().BeFalse();
        e.HasTxId.Should().BeFalse();
        e.HasReplacedByRequestId.Should().BeFalse();
    }
}
