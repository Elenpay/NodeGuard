namespace FundsManager.Data.Models
{
    /// <summary>
    /// A ChannelOperationRequestPSBT is a PSBT with the input signed by the approver
    /// </summary>
    public class ChannelOperationRequestPSBT : Entity
    {
        public string PSBT { get; set; }

        /// <summary>
        /// Bool used to mark this sig's PSBT as the template for others to sign
        /// </summary>
        public bool IsTemplatePSBT { get; set; }

        /// <summary>
        /// Represents the PSBT signed with the Internal Wallet key of the moment
        /// </summary>
        public bool IsInternalWalletPSBT { get; set; }

        /// <summary>
        /// Represents the final funded psbt with all the partial signatures sent to LND
        /// </summary>
        public bool IsFinalisedPSBT { get; set; }

        #region Relationships

        public int ChannelOperationRequestId { get; set; }
        public ChannelOperationRequest ChannelOperationRequest { get; set; }

        public string? UserSignerId { get; set; }
        public ApplicationUser? UserSigner { get; set; }

        #endregion Relationships
    }
}