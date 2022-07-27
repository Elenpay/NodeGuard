using FundsManager.Helpers;
using NBitcoin;
using NBXplorer.DerivationStrategy;

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

        /// <summary>
        /// M-of-N Multisig threshold
        /// </summary>
        public int MofN { get; set; }

        public string? Description { get; set; }

        public bool IsArchived { get; set; }

        public bool IsCompromised { get; set; }

        /// <summary>
        /// A finalised wallet means that all the configuration has been set and it is ready to be used in the application
        /// </summary>
        public bool IsFinalised { get; set; }

        public WalletAddressType WalletAddressType { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<Key> Keys { get; set; }

        /// <summary>
        /// The internal wallet is used to co-sign with other keys of the wallet entity
        /// </summary>
        public int InternalWalletId { get; set; }

        public InternalWallet InternalWallet { get; set; }

        #endregion Relationships

        /// <summary>
        /// Returns the DerivationStrategy for the multisig wallet
        /// </summary>
        /// <returns></returns>
        public DerivationStrategyBase? GetDerivationStrategy()
        {
            DerivationStrategyBase result = null;
            var currentNetwork = CurrentNetworkHelper.GetCurrentNetwork();

            if (Keys != null && Keys.Any())
            {
                var bitcoinExtPubKeys = Keys.Select(x =>
                    {
                        return new BitcoinExtPubKey(x.XPUB,
                            currentNetwork);
                    })
                    .ToList();

                if (bitcoinExtPubKeys == null || !bitcoinExtPubKeys.Any())
                {
                    return null;
                }

                var factory = new DerivationStrategyFactory(currentNetwork);

                result = factory.CreateMultiSigDerivationStrategy(bitcoinExtPubKeys.ToArray(),
                       MofN,
                        new DerivationStrategyOptions
                        {
                            ScriptPubKeyType = ScriptPubKeyType.Segwit
                        });
            }

            return result;
        }
    }
}