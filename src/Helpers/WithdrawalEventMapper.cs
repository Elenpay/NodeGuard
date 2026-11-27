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
using Nodeguard;

namespace NodeGuard.Helpers;

/// <summary>
/// Maps withdrawal requests and their status transitions to the gRPC wallet event shapes
/// </summary>
public static class WithdrawalEventMapper
{
    public static WITHDRAWAL_REQUEST_STATUS ToProtoStatus(WalletWithdrawalRequestStatus status)
    {
        return status switch
        {
            WalletWithdrawalRequestStatus.Cancelled => WITHDRAWAL_REQUEST_STATUS.WithdrawalCancelled,
            WalletWithdrawalRequestStatus.Failed => WITHDRAWAL_REQUEST_STATUS.WithdrawalFailed,
            WalletWithdrawalRequestStatus.FinalizingPSBT => WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingApproval,
            WalletWithdrawalRequestStatus.OnChainConfirmationPending => WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingConfirmation,
            WalletWithdrawalRequestStatus.OnChainConfirmed => WITHDRAWAL_REQUEST_STATUS.WithdrawalSettled,
            WalletWithdrawalRequestStatus.Pending => WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingApproval,
            WalletWithdrawalRequestStatus.PSBTSignaturesPending => WITHDRAWAL_REQUEST_STATUS.WithdrawalPendingApproval,
            WalletWithdrawalRequestStatus.Rejected => WITHDRAWAL_REQUEST_STATUS.WithdrawalRejected,
            WalletWithdrawalRequestStatus.Bumped => WITHDRAWAL_REQUEST_STATUS.WithdrawalBumped,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status")
        };
    }

    /// <summary>
    /// The event a persisted change of a withdrawal request stands for, or null when nothing a subscriber cares
    /// about changed (e.g. an update that only touched the description)
    /// </summary>
    public static WALLET_EVENT_TYPE? TransitionEventType(
        WalletWithdrawalRequestStatus previousStatus, string? previousTxId,
        WalletWithdrawalRequestStatus status, string? txId)
    {
        var statusChanged = previousStatus != status;
        var txIdChanged = !string.Equals(previousTxId, txId, StringComparison.Ordinal);

        if (status == WalletWithdrawalRequestStatus.OnChainConfirmationPending)
        {
            if (previousStatus is WalletWithdrawalRequestStatus.Bumped or WalletWithdrawalRequestStatus.Cancelled)
                return WALLET_EVENT_TYPE.WalletEventWithdrawalReplacementReverted;

            if (txIdChanged && !string.IsNullOrEmpty(txId))
                return WALLET_EVENT_TYPE.WalletEventWithdrawalBroadcast;
        }

        if (!statusChanged)
            return null;

        return status switch
        {
            WalletWithdrawalRequestStatus.FinalizingPSBT => WALLET_EVENT_TYPE.WalletEventWithdrawalSigning,
            WalletWithdrawalRequestStatus.OnChainConfirmed => WALLET_EVENT_TYPE.WalletEventWithdrawalConfirmed,
            WalletWithdrawalRequestStatus.Bumped => WALLET_EVENT_TYPE.WalletEventWithdrawalBumped,
            WalletWithdrawalRequestStatus.Cancelled => WALLET_EVENT_TYPE.WalletEventWithdrawalCancelled,
            WalletWithdrawalRequestStatus.Rejected => WALLET_EVENT_TYPE.WalletEventWithdrawalRejected,
            WalletWithdrawalRequestStatus.Failed => WALLET_EVENT_TYPE.WalletEventWithdrawalFailed,
            _ => WALLET_EVENT_TYPE.WalletEventWithdrawalStatusChanged
        };
    }

    /// <summary>
    /// Builds the event payload. Signature counts need <see cref="WalletWithdrawalRequest.Wallet"/> with its keys and
    /// the request's PSBTs loaded; amounts need its destinations.
    /// </summary>
    public static WithdrawalEvent ToWithdrawalEvent(WalletWithdrawalRequest request,
        WalletWithdrawalRequestStatus? previousStatus = null, int? replacedByRequestId = null)
    {
        var withdrawalEvent = new WithdrawalEvent
        {
            RequestId = request.Id,
            Status = ToProtoStatus(request.Status),
            IsRbfCancellation = request.IsRbfCancellation,
            AmountSats = request.SatsAmount
        };

        if (previousStatus != null) withdrawalEvent.PreviousStatus = ToProtoStatus(previousStatus.Value);
        if (!string.IsNullOrEmpty(request.TxId)) withdrawalEvent.TxId = request.TxId;
        if (!string.IsNullOrEmpty(request.ReferenceId)) withdrawalEvent.ReferenceId = request.ReferenceId;
        if (!string.IsNullOrEmpty(request.RejectCancelDescription)) withdrawalEvent.RejectOrCancelReason = request.RejectCancelDescription;
        if (!string.IsNullOrEmpty(request.RequestMetadata)) withdrawalEvent.RequestMetadata = request.RequestMetadata;
        if (request.BumpingWalletWithdrawalRequestId != null) withdrawalEvent.BumpingRequestId = request.BumpingWalletWithdrawalRequestId.Value;
        if (replacedByRequestId != null) withdrawalEvent.ReplacedByRequestId = replacedByRequestId.Value;
        if (request.CustomFeeRate != null) withdrawalEvent.FeeRateSatVb = (double)request.CustomFeeRate.Value;

        foreach (var destination in request.WalletWithdrawalRequestDestinations ?? [])
        {
            withdrawalEvent.Destinations.Add(new WithdrawalDestination
            {
                Address = destination.Address,
                AmountSats = new Money(destination.Amount, MoneyUnit.BTC).Satoshi
            });
        }

        var wallet = request.Wallet;
        if (wallet is { IsHotWallet: false })
        {
            withdrawalEvent.SignaturesCollected = request.NumberOfSignaturesCollected;
            withdrawalEvent.SignaturesRequired = wallet.MofN - (wallet.RequiresInternalWalletSigning ? 1 : 0);
        }

        return withdrawalEvent;
    }
}
