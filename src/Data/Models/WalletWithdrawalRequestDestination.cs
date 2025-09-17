// NodeGuard
// Copyright (C) 2025  Elenpay
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY, without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see http://www.gnu.org/licenses/.

namespace NodeGuard.Data.Models
{
    public class WalletWithdrawalRequestDestination : Entity
    {
        /// <summary>
        /// The destination address
        /// </summary>
        public required string Address { get; set; }
        /// <summary>
        /// The amount to send to this address in BTC
        /// </summary>
        public required decimal Amount { get; set; }


        #region Relationships

        /// <summary>
        /// The withdrawal request this destination belongs to
        /// </summary>
        public int WalletWithdrawalRequestId { get; set; }

        /// <summary>
        /// The withdrawal request this destination belongs to
        /// </summary>
        public WalletWithdrawalRequest WalletWithdrawalRequest { get; set; } = null!;

        #endregion
    }
}
