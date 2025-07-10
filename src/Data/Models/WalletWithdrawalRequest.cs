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

using System.ComponentModel.DataAnnotations.Schema;
using NBitcoin;
using NodeGuard.Services;

namespace NodeGuard.Data.Models
{
    public enum WalletWithdrawalRequestStatus
    {
        /// <summary>
        /// Pending status means that it is waiting for approval by treasury guys
        /// </summary>
        Pending = 0,

        /// <summary>
        /// Cancelled by the user who requests it
        /// </summary>
        Cancelled = 1,

        /// <summary>
        /// Rejected by the other approvers of the operation
        /// </summary>
        Rejected = 2,

        /// <summary>
        /// Approved by at least one approver and waiting for PSBT signatures filling
        /// </summary>
        PSBTSignaturesPending = 3,

        /// <summary>
        /// The operation tx is signed and broadcast waiting for on-chain confirmation
        /// </summary>
        OnChainConfirmationPending = 4,

        /// <summary>
        /// The TX is fully broadcast this means that the operation has been confirmed
        /// </summary>
        OnChainConfirmed = 5,

        /// <summary>
        /// Marked when a error happens when broadcasting the TX
        /// </summary>
        Failed = 6,

        /// <summary>
        /// The PSBT is being signed by NodeGuard after all human required signatures have been collected
        /// </summary>
        FinalizingPSBT = 7
    }

    /// <summary>
    /// Requests to withdraw funds from a NG-managed wallet
    /// </summary>
    public class WalletWithdrawalRequest : Entity, IEquatable<WalletWithdrawalRequest>, IBitcoinRequest
    {
        public WalletWithdrawalRequestStatus Status { get; set; }

        /// <summary>
        /// Description by the requestor
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Bool used to mark if the output of the request should the maximum as possible to the destination address.
        /// </summary>
        public bool WithdrawAllFunds { get; set; }

        public string? RejectCancelDescription { get; set; }

        /// <summary>
        /// Checks if all the threshold signatures are collected, including the internal wallet key (even if not signed yet)
        /// </summary>
        [NotMapped]
        public bool AreAllRequiredHumanSignaturesCollected => CheckSignatures();

        [NotMapped]
        public int NumberOfSignaturesCollected =>
    WalletWithdrawalRequestPSBTs == null
        ? 0
        : WalletWithdrawalRequestPSBTs.Count(x =>
            !x.IsTemplatePSBT && !x.IsFinalisedPSBT && !x.IsInternalWalletPSBT);

        /// <summary>
        /// This indicates if the user requested a changeless operation by selecting UTXOs
        /// </summary>
        public bool Changeless { get; set; }

        /// <summary>
        /// TxId of the request
        /// </summary>
        public string? TxId { get; set; }

        /// <summary>
        /// For additional info required by the requestor
        /// </summary>
        public string? RequestMetadata { get; set; }

        /// <summary>
        /// Recommended fee type selected by the user to be applied at the moment of the operation, this cannot be changed once the template PSBT is created nor signed
        /// </summary>
        public MempoolRecommendedFeesType MempoolRecommendedFeesType { get; set; }

        /// <summary>
        /// Fee rate in sat/vbyte to be applied if the user selects a custom fee rate
        /// </summary>
        public decimal? CustomFeeRate { get; set; }

        /// <summary>
        /// Check that the number of signatures (not finalised psbt nor internal wallet psbt or template psbt are gathered and increases by one to count on the internal wallet signature
        /// </summary>
        /// <returns></returns>
        private bool CheckSignatures()
        {
            var result = false;

            if (Wallet.IsHotWallet)
            {
                return true;
            }

            if (WalletWithdrawalRequestPSBTs != null && WalletWithdrawalRequestPSBTs.Any())
            {
                var numberOfSignaturesCollected = NumberOfSignaturesCollected;

                //We add the internal Wallet signature
                if (Wallet.RequiresInternalWalletSigning)
                {
                    numberOfSignaturesCollected++;
                }

                if (numberOfSignaturesCollected == Wallet.MofN)
                {
                    result = true;
                }
            }

            return result;
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((WalletWithdrawalRequest)obj);
        }

        public bool Equals(WalletWithdrawalRequest? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Id == other.Id;
        }

        [NotMapped]
        public decimal TotalAmount =>
            WalletWithdrawalRequestDestinations?.Sum(x => x.Amount) ?? 0m;

        [NotMapped]
        public long SatsAmount => new Money(WalletWithdrawalRequestDestinations?.Sum(x => x.Amount) ?? 0m, MoneyUnit.BTC).Satoshi;

        #region Relationships

        /// <summary>
        /// User who requested the withdrawal
        /// </summary>
        public string? UserRequestorId { get; set; }

        public ApplicationUser UserRequestor { get; set; }
        public int WalletId { get; set; }

        public Wallet Wallet { get; set; }

        public List<WalletWithdrawalRequestPSBT> WalletWithdrawalRequestPSBTs { get; set; }

        public List<FMUTXO> UTXOs { get; set; }

        /// <summary>
        /// This is a optional field that you can used to link withdrawals with externally-generated IDs (e.g. a withdrawal/settlement that belongs to an elenpay store)
        /// </summary>
        public string? ReferenceId { get; set; }

        /// <summary>
        /// List of destinations for the withdrawal request, if any, the amounts and dest on this entity is mutually exclusive with the WalletWithdrawalRequestDestinations
        /// </summary>
        public List<WalletWithdrawalRequestDestination>? WalletWithdrawalRequestDestinations { get; set; }

        #endregion Relationships

        public override int GetHashCode()
        {
            return Id;
        }

        public static bool operator ==(WalletWithdrawalRequest? left, WalletWithdrawalRequest? right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(WalletWithdrawalRequest? left, WalletWithdrawalRequest? right)
        {
            return !Equals(left, right);
        }
    }
}