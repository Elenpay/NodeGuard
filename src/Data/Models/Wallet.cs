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

using System.ComponentModel.DataAnnotations.Schema;
using NodeGuard.Helpers;
using NBitcoin;
using NBitcoin.Scripting;
using NBXplorer.DerivationStrategy;

namespace NodeGuard.Data.Models
{
    public enum WalletAddressType
    {
        NativeSegwit,
        NestedSegwit,
        Legacy,
        Taproot
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
        /// Used to mark this wallet as imported from a BIP39 mnemonic though the seedphrase is not stored in the database if the remote signer is enabled
        /// </summary>
        public bool IsBIP39Imported { get; set; }

        /// <summary>
        /// Single sig wallet is a wallet that does not require any other signer to sign the transaction
        /// </summary>
        [NotMapped]
        public bool IsSingleSig => MofN == 1 && (IsHotWallet || IsBIP39Imported);

        /// <summary>
        /// If the wallet is unsorted multisig (i.e. keeporder in nbitcoin), it means that the keys are not sorted lexicographically
        /// </summary>
        public bool IsUnSortedMultiSig { get; set; }

        /// <summary>
        /// This field is used to store the output descriptor of the wallet if it was imported from a output descriptor
        /// </summary>
        public string? ImportedOutputDescriptor { get; set; }

        /// <summary>
        /// Watch only wallet is a wallet that does not have any private key and was imported from a xpub or output descriptor
        /// </summary>
        [NotMapped]
        public bool IsWatchOnly => ImportedOutputDescriptor != null && !IsBIP39Imported;

        /// <summary>
        /// Only when the remote signer is disabled, the seedphrase is stored in the database
        /// </summary>
        public string? BIP39Seedphrase { get; set; }

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
        public string? InternalWalletMasterFingerprint { get; set; }

        /// <summary>
        /// This is a optional field that you can used to link wallets with externally-generated IDs (e.g. a wallet belongs to a btcpayserver store)
        /// </summary>
        public string? ReferenceId { get; set; }

        [NotMapped] public bool RequiresInternalWalletSigning => Keys != null ? Keys.Count == MofN : false;

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<Key> Keys { get; set; }


        /// <summary>
        /// The internal wallet is used to co-sign with other keys of the wallet entity. It is optional for imported BIP39 wallets
        /// </summary>
        public int? InternalWalletId { get; set; }

        public InternalWallet? InternalWallet { get; set; }


        public ICollection<LiquidityRule> LiquidityRules { get; set; }

        #endregion Relationships

        /// <summary>
        /// Returns the DerivationStrategy for the multisig wallet
        /// </summary>
        /// <returns></returns>
        public DerivationStrategyBase? GetDerivationStrategy()
        {
            var currentNetwork = CurrentNetworkHelper.GetCurrentNetwork();
            if (Keys != null && Keys.Any())
            {
                //If it is not an unsorted multisig, we sort the keys lexicographically
                List<BitcoinExtPubKey> bitcoinExtPubKeys;
                if (!IsUnSortedMultiSig)
                {
                    bitcoinExtPubKeys = Keys.Select(x =>
                            x.GetBitcoinExtPubKey(currentNetwork))
                        .OrderBy(x => x.ToString()) //This is to match sortedmulti() lexicographical sort
                        .ToList();
                }
                else
                {
                    // Unsorted multisig, order is FIFO (first key is the first key added to the wallet)
                    bitcoinExtPubKeys = Keys.OrderBy(x => x.Id).Select(x =>
                            x.GetBitcoinExtPubKey(currentNetwork))
                        .ToList();
                }


                if (bitcoinExtPubKeys == null || !bitcoinExtPubKeys.Any())
                {
                    return null;
                }

                var factory = new DerivationStrategyFactory(currentNetwork);

                if (IsHotWallet || IsBIP39Imported)
                {
                    return factory.CreateDirectDerivationStrategy(bitcoinExtPubKeys.FirstOrDefault(),
                        new DerivationStrategyOptions
                        {
                            ScriptPubKeyType = ScriptPubKeyType.Segwit,
                        });
                }

                return factory.CreateMultiSigDerivationStrategy(bitcoinExtPubKeys.ToArray(),
                    MofN,
                    new DerivationStrategyOptions
                    {
                        ScriptPubKeyType = ScriptPubKeyType.Segwit,
                        KeepOrder = IsUnSortedMultiSig
                    });
            }

            return null;
        }


        public NBitcoin.Key DeriveUtxoPrivateKey(Network network, KeyPath utxoKeyPath)
        {
            if (IsBIP39Imported)
            {
                if (string.IsNullOrWhiteSpace(BIP39Seedphrase))
                    throw new InvalidOperationException("Seedphrase is empty");
                var key = Keys?.FirstOrDefault(k => k.IsBIP39ImportedKey);
                var deriveUtxoPrivateKey = new Mnemonic(BIP39Seedphrase)
                    .DeriveExtKey()
                    .GetWif(network)
                    .Derive(KeyPath.Parse(key.Path))
                    .Derive(utxoKeyPath)
                    .PrivateKey;

                return deriveUtxoPrivateKey;
            }
            else
            {
                //If the wallet is has a internal wallet, we derive from it (not for BIP39 imported wallets)
                if (InternalWalletId == null)
                {
                    throw new InvalidOperationException("You can't derive from a user's key");
                }

                var internalKey = Keys?.FirstOrDefault(k => k.InternalWalletId != null);
                return new Mnemonic(InternalWallet.MnemonicString)
                    .DeriveExtKey()
                    .GetWif(network)
                    .Derive(KeyPath.Parse(internalKey.Path))
                    .Derive(utxoKeyPath)
                    .PrivateKey;
            }
        }
    }
}