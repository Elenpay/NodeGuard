namespace FundsManager.Data
{
    public class ChannelOperationRequestSignature : Entity
    {
        public string PSBT { get; set; }

        public string SignatureContent { get; set; }

        #region Relationships

        public int ChannelOpenRequestId { get; set; }
        public ChannelOperationRequest ChannelOperationRequest { get; set; }

        #endregion
    }
}

