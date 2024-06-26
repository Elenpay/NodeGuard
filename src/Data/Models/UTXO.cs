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

namespace NodeGuard.Data.Models
{
    /// <summary>
    /// UTXO entity in the NodeGuard
    /// </summary>
    public class UTXO : Entity, IEquatable<UTXO>
    {
        public string TxId { get; set; }

        public uint OutputIndex { get; set; }

        public long SatsAmount { get; set; }

        #region Relationships

        public List<ChannelOperationRequest> ChannelOperationRequests { get; set; }

        public List<WalletWithdrawalRequest> WalletWithdrawalRequests { get; set; }

        #endregion Relationships

        public override string ToString()
        {
            return $"{TxId}:{OutputIndex}";
        }

        public override bool Equals(object? obj)
        {
            return base.Equals(obj);
        }

        public bool Equals(UTXO? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return TxId == other.TxId && OutputIndex == other.OutputIndex && SatsAmount == other.SatsAmount;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(TxId, OutputIndex, SatsAmount);
        }

        public static bool operator ==(UTXO? left, UTXO? right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(UTXO? left, UTXO? right)
        {
            return !Equals(left, right);
        }
    }
}