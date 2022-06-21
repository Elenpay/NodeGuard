
namespace FundsManager.Data
{

    public enum ChannelOperationRequestStatus: ushort
    {
        Pending = 0,
        Approved = 1,
        Cancelled = 2,
        Rejected = 3
    }

    public enum OperationRequestType
    {
        Open = 1,
        Close = 2
    }

    public class ChannelOperationRequest : Entity
    {
        public decimal Amount { get; set; }

        public string Description { get; set; }

        public string AmountCryptoUnit { get; set; } // TODO worth an enum?

        public ChannelOperationRequestStatus status { get; set; }

        public OperationRequestType RequestType { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequestSignature> ChannelOperationRequestSignatures { get; set; }

        public int WalletId { get; set; }
        public Wallet Wallet { get; set; }

        public int SourceNodeId { get; set; }
        public Node SourceNode { get; set; }
        public int DestNodeId { get; set; }
        public Node DestNode { get; set; }
        public string UserId { get; set; }
        public ApplicationUser? User { get; set; }
        public int ChannelId { get; set; }
        public Channel Channel { get; set; }

        #endregion
    }
}
