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

 namespace FundsManager.Data.Models
{
    /// <summary>
    /// A ChannelOperationRequestPSBT is a PSBT with the input signed by the approver
    /// </summary>
    public class ChannelOperationRequestPSBT : Entity
    {
        public string PSBT { get; set; }

        /// <summary>
        /// Bool used to mark this sig's PSBT as the template for others to sign
        /// </summary>
        public bool IsTemplatePSBT { get; set; }

        /// <summary>
        /// Represents the PSBT signed with the Internal Wallet key of the moment
        /// </summary>
        public bool IsInternalWalletPSBT { get; set; }

        /// <summary>
        /// Represents the final funded psbt with all the partial signatures sent to LND
        /// </summary>
        public bool IsFinalisedPSBT { get; set; }

        #region Relationships

        public int ChannelOperationRequestId { get; set; }
        public ChannelOperationRequest ChannelOperationRequest { get; set; }

        public string? UserSignerId { get; set; }
        public ApplicationUser? UserSigner { get; set; }

        #endregion Relationships
    }
}