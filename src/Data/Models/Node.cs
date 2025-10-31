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
using NBitcoin;

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
        /// The destination wallet for funds from this node.
        /// Used for upfront_shutdown_script if the peer supports this and as the destination wallet for automatic swap outs.
        /// </summary>
        public int? FundsDestinationWalletId { get; set; }

        public Wallet? FundsDestinationWallet { get; set; }

        /// <summary>
        /// enable/disable node
        /// </summary>
        public bool IsNodeDisabled { get; set; }

        /// <summary>
        /// Returns true if the node is managed by us. We defer this from the existence of an Endpoint
        /// </summary>
        [NotMapped]
        public bool IsManaged => Endpoint != null;

        public string? LoopdEndpoint { get; set; }

        public string? LoopdMacaroon { get; set; }

        public string? LoopdCert { get; set; }

        #region Automatic Node level liquidity Management

        /// <summary>
        /// Enable/disable automatic liquidity management (swap outs and channel openings) for this node
        /// </summary>
        public bool AutoLiquidityManagementEnabled { get; set; }

        /// <summary>
        /// Minimum swap amount in satoshis
        /// </summary>
        public long? SwapMinAmountSats { get; set; }

        /// <summary>
        /// Maximum swap amount in satoshis
        /// </summary>
        public long? SwapMaxAmountSats { get; set; }

        /// <summary>
        /// Maximum number of concurrent swaps to limit HTLC locking
        /// </summary>
        public int? MaxSwapsInFlight { get; set; }

        /// <summary>
        /// Maximum fee ratio (including routing + service fees) acceptable for swaps as a decimal between 0 and 1
        /// Example: 0.005 = 0.5%
        /// </summary>
        public decimal? MaxSwapFeeRatio { get; set; }

        /// <summary>
        /// Balance threshold in satoshis - when node balance exceeds this, trigger automatic swap out
        /// </summary>
        public long? MinimumBalanceThresholdSats { get; set; }

        /// <summary>
        /// Maximum amount of BTC (in satoshis) that can be spent on swaps over the budget refresh interval
        /// </summary>
        public long? SwapBudgetSats { get; set; }

        /// <summary>
        /// Time interval after which the swap budget is refreshed
        /// </summary>
        public TimeSpan? SwapBudgetRefreshInterval { get; set; }

        /// <summary>
        /// The datetime when the current budget period started
        /// </summary>
        public DateTimeOffset? SwapBudgetStartDatetime { get; set; }

        #endregion Automatic Swap Out Configuration

        #region Relationships

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsDestination { get; set; }

        public ICollection<ApplicationUser> Users { get; set; }

        public List<SwapOut> SwapOuts { get; set; }

        #endregion Relationships
    }
}