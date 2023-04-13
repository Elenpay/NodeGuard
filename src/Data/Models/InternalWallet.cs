/*
 * NodeGuard
 * Copyright (C) 2023  Elenpay
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see http://www.gnu.org/licenses/.
 *
 */

using FundsManager.Helpers;
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
        public string? MnemonicString { get; set; }

        /// <summary>
        /// XPUB in the case the Mnemonic is not set (Remote signer mode)
        /// </summary>
        public string? XPUB
        {
            get => GetXPUB();
            set => _xpub = value;
        }
        private string? _xpub;

        public string? MasterFingerprint
        {
            get => GetMasterFingerprint();
            set => _masterFingerprint = value;
        }
        private string? _masterFingerprint;

        /// <summary>
        /// Returns the xpub/tpub as nbxplorer uses
        /// </summary>
        /// <returns></returns>
        private string? GetXPUB()
        {

            var network = CurrentNetworkHelper.GetCurrentNetwork();
            if(!string.IsNullOrWhiteSpace(MnemonicString))
            {
                var masterKey = new Mnemonic(MnemonicString).DeriveExtKey().GetWif(network);
                var keyPath = new KeyPath(DerivationPath); //https://github.com/dgarage/NBXplorer/blob/0595a87fc14aee6a6e4a0194f75aec4717819/NBXplorer/Controllers/MainController.cs#L1141
                var accountKey = masterKey.Derive(keyPath);
                var bitcoinExtPubKey = accountKey.Neuter();

                var walletDerivationScheme = bitcoinExtPubKey.ToWif();

                return walletDerivationScheme;
            }

            return _xpub;
        }

        public string GetXpubForAccount(string accountId)
        {
            var network = CurrentNetworkHelper.GetCurrentNetwork();

            if(!string.IsNullOrWhiteSpace(MnemonicString))
            {
                var masterKey = new Mnemonic(MnemonicString).DeriveExtKey().GetWif(network);
                var keyPath = new KeyPath($"{DerivationPath}/{accountId}"); //https://github.com/dgarage/NBXplorer/blob/0595a87fc14aee6a6e4a0194f75aec4717819/NBXplorer/Controllers/MainController.cs#L1141
                var accountKey = masterKey.Derive(keyPath);
                var bitcoinExtPubKey = accountKey.Neuter();
                return bitcoinExtPubKey.ToWif();
            }
            else
            {
                var extKey = new BitcoinExtPubKey(_xpub, network);
                var derivedXpub = extKey.Derive(new KeyPath(accountId));
                return derivedXpub.ToWif();
            }
        }

        public string GetKeyPathForAccount(string accountId)
        {
            return $"{DerivationPath}/{accountId}";
        }

        private string? GetMasterFingerprint()
        {
            var network = CurrentNetworkHelper.GetCurrentNetwork();
            if (!string.IsNullOrWhiteSpace(MnemonicString))
            {
                var masterFingerprint = new Mnemonic(MnemonicString).DeriveExtKey().GetWif(network).GetPublicKey()
                    .GetHDFingerPrint().ToString();
                return masterFingerprint;
            }

            return _masterFingerprint;
        }
    }
}