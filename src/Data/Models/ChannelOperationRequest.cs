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
        /// The operation tx is signed and broadcast waiting for onchain confirmation
        /// </summary>
        OnChainConfirmationPending = 6,

        /// <summary>
        /// The TX is fully broadcast this means that the channel has been open/closed
        /// </summary>
        OnChainConfirmed = 7
        //TODO Failed status
    }

    public enum OperationRequestType
    {
        Open = 1,
        Close = 2
    }

    public class ChannelOperationRequest : Entity, IEquatable<ChannelOperationRequest>
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

        /// <summary>
        /// Transaction Id once received from the LightningService 
        /// </summary>
        public string? TxId { get; set; }
        
        /// <summary>
        /// Text filled by the user upon request cancellation/rejection 
        /// </summary>
        public string? ClosingReason { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequestPSBT> ChannelOperationRequestPsbts { get; set; }

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

        public List<FMUTXO> Utxos { get; set; }

        #endregion Relationships

        /// <summary>
        /// Checks if all the threshold signatures are collected, including the internal wallet key (even if not signed yet)
        /// </summary>
        [NotMapped]
        public bool AreAllRequiredSignaturesCollected => CheckSignatures();

        [NotMapped]
        public int NumberOfSignaturesCollected => ChannelOperationRequestPsbts == null ? 0 : ChannelOperationRequestPsbts.Count(x =>
                                                                                                                                                                                                                                                                                                                                                                                                            !x.IsFinalisedPSBT && !x.IsTemplatePSBT && !x.IsInternalWalletPSBT);

        /// <summary>
        /// This is the JobId provided by Hangfire of the job executing this request.
        /// </summary>
        public string? JobId { get; set; }

        /// <summary>
        /// Check that the number of signatures (not finalised psbt nor internal wallet psbt or template psbt are gathered and increases by one to count on the internal wallet signature
        /// </summary>
        /// <returns></returns>
        private bool CheckSignatures()
        {
            var result = false;

            if (ChannelOperationRequestPsbts != null && ChannelOperationRequestPsbts.Any())
            {
                var userPSBTsCount = NumberOfSignaturesCollected;

                //We add the internal Wallet signature
                userPSBTsCount++;

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
            return Equals((ChannelOperationRequest)obj);
        }

        public bool Equals(ChannelOperationRequest? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Id == other.Id;
        }

        public override int GetHashCode()
        {
            return Id;
        }

        public static bool operator ==(ChannelOperationRequest? left, ChannelOperationRequest? right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(ChannelOperationRequest? left, ChannelOperationRequest? right)
        {
            return !Equals(left, right);
        }
    }
}