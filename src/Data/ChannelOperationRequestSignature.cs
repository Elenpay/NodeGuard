namespace FundsManager.Data
{
    public class ChannelOperationRequestSignature : Entity
    {
        public string PSBT { get; set; }

        public string SignatureContent { get; set; }

        #region Relationships

        public string? ChannelOpenRequestId { get; set; }
        public ChannelOperationRequest? ChannelOperationRequest { get; set; }

        #endregion
    }
}

