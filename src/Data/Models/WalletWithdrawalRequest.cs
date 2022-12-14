using System.ComponentModel.DataAnnotations.Schema;
using NBitcoin;

namespace FundsManager.Data.Models
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
        Failed = 6
    }

    /// <summary>
    /// Requests to withdraw funds from a FM-managed multisig wallet
    /// </summary>
    public class WalletWithdrawalRequest : Entity, IEquatable<WalletWithdrawalRequest>
    {
        public WalletWithdrawalRequestStatus Status { get; set; }

        /// <summary>
        /// base58 address of the output of the request
        /// </summary>
        public string DestinationAddress { get; set; }

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
        /// TX amount in BTC
        /// </summary>
        ///

        public decimal Amount { get; set; }

        /// <summary>
        /// Checks if all the threshold signatures are collected, including the internal wallet key (even if not signed yet)
        /// </summary>
        [NotMapped]
        public bool AreAllRequiredSignaturesCollected => CheckSignatures();

        [NotMapped]
        public int NumberOfSignaturesCollected =>
            WalletWithdrawalRequestPSBTs == null
                ? 0
                : WalletWithdrawalRequestPSBTs.Count(x =>
                    !x.IsTemplatePSBT && !x.IsFinalisedPSBT && !x.IsInternalWalletPSBT);

        /// <summary>
        /// This is the JobId provided by Quartz of the job executing this request.
        /// </summary>
        public string? JobId { get; set; }

        /// <summary>
        /// TxId of the request
        /// </summary>
        public string? TxId { get; set; }

        /// <summary>
        /// Check that the number of signatures (not finalised psbt nor internal wallet psbt or template psbt are gathered and increases by one to count on the internal wallet signature
        /// </summary>
        /// <returns></returns>
        private bool CheckSignatures()
        {
            var result = false;

            if (WalletWithdrawalRequestPSBTs != null && WalletWithdrawalRequestPSBTs.Any())
            {
                var userPSBTsCount = NumberOfSignaturesCollected;

                //We add the internal Wallet signature
                if (Wallet.RequiresInternalWalletSigning)
                {
                    userPSBTsCount++;
                }

                if (userPSBTsCount == Wallet.MofN)
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
        public long SatsAmount => new Money(Amount, MoneyUnit.BTC).Satoshi;

        #region Relationships

        /// <summary>
        /// User who requested the withdrawal
        /// </summary>
        public string UserRequestorId { get; set; }

        public ApplicationUser UserRequestor { get; set; }
        public int WalletId { get; set; }

        public Wallet Wallet { get; set; }

        public List<WalletWithdrawalRequestPSBT> WalletWithdrawalRequestPSBTs { get; set; }

        public List<FMUTXO> UTXOs { get; set; }

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