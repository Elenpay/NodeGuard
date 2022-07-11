using System.ComponentModel.DataAnnotations.Schema;
using NBitcoin;

namespace FundsManager.Data.Models
{
    public enum ChannelOperationRequestStatus
    {
        /// <summary>
        /// Pending status is when the PSBT is fully signed by signers but waiting to be signed by the funds manager and broadcasted
        /// </summary>
        Approved = 1,

        Cancelled = 2,
        Rejected = 3,

        /// <summary>
        /// Pending status means that it is waiting for approval by treasury guys
        /// </summary>
        Pending = 4,

        /// <summary>
        /// Approved and waiting for PSBT signatures filling, only for OperationRequestType = Open
        /// </summary>
        PSBTSignaturesPending = 5,

        /// <summary>
        /// The operation tx is signed and waiting for onchain confirmation
        /// </summary>
        OnChainConfirmationPending = 6,

        /// <summary>
        /// The TX is fully broadcast this means that the channel has been open/closed
        /// </summary>
        OnChainConfirmed = 7
    }

    public enum OperationRequestType
    {
        Open = 1,
        Close = 2
    }

    public class ChannelOperationRequest : Entity
    {
        /// <summary>
        /// Amount in satoshis
        /// </summary>
        public long SatsAmount { get; set; }

        /// <summary>
        /// Calculated property to convert to btc
        /// </summary>
        [NotMapped]
        public decimal Amount => new Money(SatsAmount, MoneyUnit.Satoshi).ToDecimal(MoneyUnit.BTC);

        public string? Description { get; set; }

        public MoneyUnit AmountCryptoUnit { get; set; }

        public ChannelOperationRequestStatus Status { get; set; }

        public OperationRequestType RequestType { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequestSignature> ChannelOperationRequestSignatures { get; set; }

        public int WalletId { get; set; }
        public Wallet Wallet { get; set; }

        public int SourceNodeId { get; set; }
        public Node SourceNode { get; set; }
        public int? DestNodeId { get; set; }
        public Node? DestNode { get; set; }
        public string UserId { get; set; }
        public ApplicationUser User { get; set; }
        public int? ChannelId { get; set; }
        public Channel? Channel { get; set; }

        public bool IsChannelPrivate { get; set; }

        #endregion Relationships
    }
}