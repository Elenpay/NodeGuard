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
    /// <summary>
    /// PSBTs related to a WalletWithdrawalRequest
    /// </summary>
    public class WalletWithdrawalRequestPSBT : Entity
    {
        public string PSBT { get; set; }

        /// <summary>
        /// Represents the PSBT signed with the Internal Wallet key of the moment
        /// </summary>
        public bool IsInternalWalletPSBT { get; set; }

        /// <summary>
        /// Bool used to mark this sig's PSBT as the template for others to sign
        /// </summary>
        public bool IsTemplatePSBT { get; set; }

        /// <summary>
        /// Represents the final funded psbt with all the partial signatures ready to be broadcast
        /// </summary>
        public bool IsFinalisedPSBT { get; set; }

        #region Relationships

        public string? SignerId { get; set; }

        public ApplicationUser? Signer { get; set; }

        public int WalletWithdrawalRequestId { get; set; }

        public WalletWithdrawalRequest WalletWithdrawalRequest { get; set; }

        #endregion Relationships
    }
}