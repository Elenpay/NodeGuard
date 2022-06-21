namespace FundsManager.Data
{
    public enum WalletAddressType
    {
        NativeSegwit, NestedSegwit, Legacy, Taproot
    }

    public class Wallet : Entity
    {
        public string Name { get; set; }

        public string MofN { get; set; }

        public string Description { get; set; }

        public bool IsArchived { get; set; }

        public bool IsCompromised { get; set; }

        public WalletAddressType WalletAddressType { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<Key> Keys { get; set; }

        #endregion
    }
}
