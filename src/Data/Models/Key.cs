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

using NBitcoin;

namespace FundsManager.Data.Models
{
    public class Key : Entity, IEquatable<Key>
    {
        public string Name { get; set; }

        public string XPUB { get; set; }

        public string? Description { get; set; }

        public bool IsArchived { get; set; }

        public bool IsCompromised { get; set; }

        public string? MasterFingerprint { get; set; }

        /// <summary>
        /// Derivation Path (e.g. m/84'/0'/0')
        /// </summary>
        public string? Path { get; set; }
        
        /// <summary>
        /// Flag to indicate that this key was imported from a BIP39 mnemonic
        /// </summary>
        public bool IsBIP39ImportedKey { get; set; }

        #region Relationships

        public string? UserId { get; set; }
        public ApplicationUser? User { get; set; }

        public ICollection<Wallet> Wallets { get; set; }

        /// <summary>
        /// The internal wallet where this key belongs (if it were a internal wallet key)
        /// </summary>
        public int? InternalWalletId { get; set; }
        public InternalWallet? InternalWallet { get; set; }

        #endregion Relationships

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != typeof(Key)) return false;
            return Equals((Key)obj);
        }

        public bool Equals(Key? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return XPUB == other.XPUB;
        }

        public override int GetHashCode()
        {
            return XPUB.GetHashCode();
        }

        public static bool operator ==(Key? left, Key? right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(Key? left, Key? right)
        {
            return !Equals(left, right);
        }
        
        private sealed class CheckEquality: IEqualityComparer<Key>
        {
            public bool Equals(Key? b1, Key? b2)
            {
                if (b1 == null || b2 == null) throw new ArgumentException("Compared keys can't be null");
                return b1.Id == b2.Id;
            }

            public int GetHashCode(Key obj)
            {
                throw new NotImplementedException();
            }
        }
        
        private static readonly IEqualityComparer<Key> Comparer = new CheckEquality();

        public static bool Contains(ICollection<Key> source, Key? key) 
        {
            return source.Contains(key, Comparer!);
        }
        
        public BitcoinExtPubKey GetBitcoinExtPubKey(Network network)
        {
            return new BitcoinExtPubKey(XPUB, network);
        }

        public RootedKeyPath GetRootedKeyPath()
        {
            var masterFingerprint = HDFingerprint.Parse(MasterFingerprint);
            return new RootedKeyPath(masterFingerprint, new KeyPath(Path));
        }

        public KeyPath DeriveUtxoKeyPath(KeyPath utxoKeyPath)
        {
            return KeyPath.Parse(Path).Derive(utxoKeyPath);
        }

        public PubKey DeriveUtxoPubKey(Network network, KeyPath utxoKeyPath)
        {
            return GetBitcoinExtPubKey(network).Derive(utxoKeyPath).GetPublicKey();
        }

        public RootedKeyPath GetAddressRootedKeyPath(KeyPath utxoKeyPath)
        {
            return new RootedKeyPath(HDFingerprint.Parse(MasterFingerprint), utxoKeyPath);
        }
    }
}