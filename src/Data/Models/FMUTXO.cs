namespace FundsManager.Data.Models
{
    /// <summary>
    /// UTXO entity in the FundsManager
    /// </summary>
    public class FMUTXO : Entity, IEquatable<FMUTXO>
    {
        public string TxId { get; set; }

        public uint OutputIndex { get; set; }

        public long SatsAmount { get; set; }

        #region Relationships

        public List<ChannelOperationRequest> ChannelOperationRequests { get; set; }

        #endregion Relationships

        public override string ToString()
        {
            return $"{TxId}:{OutputIndex}";
        }

        public override bool Equals(object? obj)
        {
            return base.Equals(obj);
        }

        public bool Equals(FMUTXO? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return TxId == other.TxId && OutputIndex == other.OutputIndex && SatsAmount == other.SatsAmount;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(TxId, OutputIndex, SatsAmount);
        }

        public static bool operator ==(FMUTXO? left, FMUTXO? right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(FMUTXO? left, FMUTXO? right)
        {
            return !Equals(left, right);
        }
    }
}