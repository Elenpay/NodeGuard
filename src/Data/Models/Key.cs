/*
 * NodeGuard
 * Copyright (C) 2023  ClovrLabs
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
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 *
 */

﻿namespace FundsManager.Data.Models
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
        /// Derivation Path (e.g.
        /// </summary>
        public string? Path { get; set; }

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
    }
}