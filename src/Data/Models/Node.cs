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

namespace NodeGuard.Data.Models
{
    public class Node : Entity
    {
        public string PubKey { get; set; }

        public string Name { get; set; }

        public string? Description { get; set; }

        /// <summary>
        /// Macaroon with channel admin permissions
        /// </summary>
        public string? ChannelAdminMacaroon { get; set; }

        /// <summary>
        ///host:port grpc endpoint
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// enable/disable autosweep
        /// </summary>
        public bool AutosweepEnabled { get; set; } = true;

        /// <summary>
        /// The wallet used on upfront_shutdown_script if the peer supports this
        /// </summary>
        public int? ReturningFundsWalletId { get; set; }

        public Wallet? ReturningFundsWallet { get; set; }

        /// <summary>
        /// enable/disable node
        /// </summary>
        public bool IsNodeDisabled { get; set; }

        /// <summary>
        /// Returns true if the node is managed by us. We defer this from the existence of an Endpoint
        /// </summary>
        [NotMapped]
        public bool IsManaged => Endpoint != null;

        public string? LoopEndpoint { get; set; }

        public string? LoopMacaroon { get; set; }

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsDestination { get; set; }

        public ICollection<ApplicationUser> Users { get; set; }

        public List<SwapOut> SwapOuts { get; set; }

        #endregion Relationships
    }
}