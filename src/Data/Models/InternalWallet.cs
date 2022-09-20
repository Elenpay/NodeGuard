using NBitcoin;

namespace FundsManager.Data.Models
{
    /// <summary>
    /// This is another type of Wallet entity.
    /// ONLY FOR USE INTERNALLY BY THE FUNDSMANAGER as middleman in the signing process for security reasons
    /// </summary>
    public class InternalWallet : Entity
    {
        /// <summary>
        /// Derivation path of the wallet
        /// </summary>
        public string DerivationPath { get; set; }

        /// <summary>
        /// 24 Words mnemonic
        /// </summary>
        public string MnemonicString { get; set; }

        /// <summary>
        /// Returns the master private key (xprv..tprv)
        /// </summary>
        /// <param name="network"></param>
        /// <returns></returns>
        public string? GetMasterPrivateKey(Network network)
        {
            if (string.IsNullOrWhiteSpace(MnemonicString))
            {
                return null;
            }
            return new Mnemonic(MnemonicString).DeriveExtKey().GetWif(network).ToWif();
        }

        /// <summary>
        /// Returns the xpub/tpub as nbxplorer uses
        /// </summary>
        /// <param name="network"></param>
        /// <returns></returns>
		public string? GetXPUB(Network network)
        {
            if (string.IsNullOrWhiteSpace(DerivationPath) || string.IsNullOrWhiteSpace(MnemonicString))
            {
                return null;
            }

            var masterKey = new Mnemonic(MnemonicString).DeriveExtKey().GetWif(network);
            var keyPath = new KeyPath(DerivationPath); //https://github.com/dgarage/NBXplorer/blob/0595a87fc14aee6a6e4a0194f75aec4717819/NBXplorer/Controllers/MainController.cs#L1141
            var accountKey = masterKey.Derive(keyPath);
            var bitcoinExtPubKey = accountKey.Neuter();

            var walletDerivationScheme = bitcoinExtPubKey.ToWif();

            return walletDerivationScheme;
        }

        public BitcoinExtKey GetAccountKey(Network network)
        {
            var accountKey = new Mnemonic(MnemonicString).DeriveExtKey().GetWif(network).Derive(new KeyPath(DerivationPath));

            return accountKey;
        }
    }
}