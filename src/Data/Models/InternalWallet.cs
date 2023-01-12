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

﻿using FundsManager.Helpers;
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

        public string? MasterFingerprint { get; set; }

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

        public BitcoinExtKey GetAccountKey(Network network)
        {
            var accountKey = new Mnemonic(MnemonicString).DeriveExtKey().GetWif(network).Derive(new KeyPath(DerivationPath));

            return accountKey;
        }
    }
}