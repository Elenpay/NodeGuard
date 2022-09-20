namespace FundsManager.Data.Models
{
    /// <summary>
    /// PSBTs related to a WalletWithdrawalRequest
    /// </summary>
    public class WalletWithdrawalRequestPSBT : Entity
    {
        public string PSBT { get; set; }

        /// <summary>
        /// Represents the PSBT signed with the Internal Wallet key of the moment
        /// </summary>
        public bool IsInternalWalletPSBT { get; set; }

        /// <summary>
        /// Bool used to mark this sig's PSBT as the template for others to sign
        /// </summary>
        public bool IsTemplatePSBT { get; set; }

        /// <summary>
        /// Represents the final funded psbt with all the partial signatures ready to be broadcast
        /// </summary>
        public bool IsFinalisedPSBT { get; set; }

        #region Relationships

        public string? SignerId { get; set; }

        public ApplicationUser? Signer { get; set; }

        public int WalletWithdrawalRequestId { get; set; }

        public WalletWithdrawalRequest WalletWithdrawalRequest { get; set; }

        #endregion Relationships
    }
}