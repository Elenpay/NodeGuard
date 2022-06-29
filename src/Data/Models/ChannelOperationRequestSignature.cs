namespace FundsManager.Data.Models
{
    public class ChannelOperationRequestSignature : Entity
    {
        public string PSBT { get; set; }

        public string? SignatureContent { get; set; }

        /// <summary>
        /// Bool used to mark this sig's PSBT as the template for others to sign
        /// </summary>
        public bool IsTemplatePSBT { get; set; }

        #region Relationships

        public int ChannelOpenRequestId { get; set; }
        public ChannelOperationRequest ChannelOperationRequest { get; set; }

        public string? UserSignerId { get; set; }
        public ApplicationUser? UserSigner { get; set; }

        #endregion
    }
}

