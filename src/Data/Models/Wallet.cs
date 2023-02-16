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

﻿using System.ComponentModel.DataAnnotations.Schema;
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
        
        /// <summary>
        /// Used to mark this wallet as Hot that does not require human signing (NodeGuard or its remote signer will sign the transaction)
        /// </summary>
        public bool IsHotWallet { get; set; }
        
        /// <summary>
        /// This field is used to store the derivation path which allow to uniquely identify the wallet amongs others (Hot or Multisig)
        /// </summary>
        public string? InternalWalletSubDerivationPath { get; set; }
        
        /// <summary>
        /// This field is a copy of the column by the same name in the InternalWallet model.
        /// It is used as a way to make the relationship (InternalWalletSubDerivationPath,MasterFingerprint) unique.
        /// If a private key is compromised and we have to change the internal wallet,
        /// this field will allow us to start derivation paths from 0 again since the MasterFingerprint will be different
        /// </summary>
        public string? MasterFingerprint { get; set; }
        
        /// <summary>
        /// This is a optional field that you can used to link wallets with externally-generated IDs (e.g. a wallet belongs to a btcpayserver store)
        /// </summary>
        public string? ReferenceId { get; set; }

        [NotMapped] public bool RequiresInternalWalletSigning => Keys != null ? Keys.Count == MofN : false;

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<Key> Keys { get; set; }
        

        /// <summary>
        /// The internal wallet is used to co-sign with other keys of the wallet entity
        /// </summary>
        public int InternalWalletId { get; set; }

        public InternalWallet InternalWallet { get; set; }
        
        
        public ICollection<LiquidityRule> LiquidityRules { get; set; }
        
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
                    }).OrderBy(x => x.ExtPubKey.PubKey) //This is to match sortedmulti() lexicographical sort
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