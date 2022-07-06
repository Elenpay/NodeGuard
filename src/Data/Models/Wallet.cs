namespace FundsManager.Data.Models
{
    public enum WalletAddressType
    {
        NativeSegwit, NestedSegwit, Legacy, Taproot
    }

    /// <summary>
    /// Multisig wallet
    /// </summary>
    public class Wallet : Entity
    {
        public string Name { get; set; }

        public int MofN { get; set; }

        public string? Description { get; set; }

        public bool IsArchived { get; set; }

        public bool IsCompromised { get; set; }

        public WalletAddressType WalletAddressType { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<Key> Keys { get; set; }

        /// <summary>
        /// The internal wallet is used to co-sign with other keys of the wallet entity
        /// </summary>
        public int InternalWalletId { get; set; }
        public InternalWallet InternalWallet { get; set; }

        #endregion
    }
}
