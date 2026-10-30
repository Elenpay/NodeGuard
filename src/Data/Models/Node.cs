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

        public string? FortySwapEndpoint { get; set; }

        #region Automatic Node level liquidity Management

        /// <summary>
        /// Enable/disable automatic liquidity management (swap outs and channel openings) for this node
        /// </summary>
        public bool AutoLiquidityManagementEnabled { get; set; }

        /// <summary>
        /// Minimum Loop swap amount in satoshis
        /// </summary>
        public long LoopSwapMinAmountSats { get; set; }

        /// <summary>
        /// Maximum Loop swap amount in satoshis
        /// </summary>
        public long LoopSwapMaxAmountSats { get; set; }

        /// <summary>
        /// Minimum 40swap swap amount in satoshis
        /// </summary>
        public long FortySwapMinAmountSats { get; set; }

        /// <summary>
        /// Maximum 40swap swap amount in satoshis
        /// </summary>
        public long FortySwapMaxAmountSats { get; set; }

        /// <summary>
        /// Minimum Spark swap amount in satoshis; null means SPARK_SWAP_MIN_SATS
        /// </summary>
        public long? SparkSwapMinAmountSats { get; set; }

        /// <summary>
        /// Maximum Spark swap amount in satoshis; null means SPARK_SWAP_MAX_SATS
        /// </summary>
        public long? SparkSwapMaxAmountSats { get; set; }

        /// <summary>
        /// Maximum number of concurrent Loop swaps, to limit HTLC locking
        /// </summary>
        public int LoopMaxSwapsInFlight { get; set; }

        /// <summary>
        /// Maximum number of concurrent 40swap swaps, to limit HTLC locking
        /// </summary>
        public int FortySwapMaxSwapsInFlight { get; set; }

        /// <summary>
        /// Maximum number of Spark swaps being paid at once, to limit HTLC locking. Once paid, a Spark swap waits in
        /// its Spark wallet, whose max balance limits it
        /// </summary>
        public int SparkMaxSwapsInFlight { get; set; }

        /// <summary>
        /// Maximum routing fee ratio acceptable for Loop swaps, as a decimal between 0 and 1. Example: 0.005 = 0.5%
        /// </summary>
        public decimal LoopMaxSwapRoutingFeeRatio { get; set; }

        /// <summary>
        /// Maximum routing fee ratio acceptable for 40swap swaps, as a decimal between 0 and 1
        /// </summary>
        public decimal FortySwapMaxSwapRoutingFeeRatio { get; set; }

        /// <summary>
        /// Maximum routing fee ratio acceptable for Spark swaps, as a decimal between 0 and 1. It includes Spark's own
        /// 15 bps, which Spark charges on the Lightning route
        /// </summary>
        public decimal SparkMaxSwapRoutingFeeRatio { get; set; }

        /// <summary>
        /// Balance threshold in satoshis - when node balance exceeds this, trigger automatic swap out
        /// </summary>
        public long MinimumBalanceThresholdSats { get; set; }

        /// <summary>
        /// Maximum amount of BTC (in satoshis) that can be spent on swaps over the budget refresh interval
        /// </summary>
        public long SwapBudgetSats { get; set; }

        /// <summary>
        /// Time interval after which the swap budget is refreshed
        /// </summary>
        public TimeSpan SwapBudgetRefreshInterval { get; set; }

        /// <summary>
        /// The datetime when the current budget period started
        /// </summary>
        public DateTimeOffset? SwapBudgetStartDatetime { get; set; }

        /// <summary>
        /// Weight for Loop swap provider selection (default 100)
        /// Higher weight = higher probability of selection
        /// Example: LoopSwapWeight=90, FortySwapWeight=10 means 90% Loop, 10% 40swap
        /// </summary>
        public int LoopSwapWeight { get; set; } = 100;

        /// <summary>
        /// Weight for 40swap provider selection (default 0)
        /// Higher weight = higher probability of selection
        /// </summary>
        public int FortySwapWeight { get; set; } = 0;

        /// <summary>
        /// Weight for the Spark swap provider selection (default 0). Spark is skipped while the node's
        /// Spark wallet is unavailable; SparkMaxSwapsInFlight limits its swaps
        /// </summary>
        public int SparkSwapWeight { get; set; } = 0;

        /// <summary>
        /// The Spark wallet this node's Spark swap-outs pass through (SPARK_SIGNER=wallet). Several nodes may
        /// share one: each swap exits its own leaves, and the wallet's max balance counts all of them
        /// </summary>
        public int? SparkWalletId { get; set; }

        /// <summary>
        /// How long LND keeps trying routes for this node's Spark swap payments, in minutes; null means
        /// SPARK_PAYMENT_TIMEOUT_MINUTES
        /// </summary>
        public int? SparkPaymentTimeoutMinutes { get; set; }

        #endregion Automatic Swap Out Configuration

        #region Routing Engine

        /// <summary>
        /// Master gate for the dynamic fee engine on this node. Defaults OFF.
        /// </summary>
        public bool DynamicFeeManagementEnabled { get; set; } = false;

        /// <summary>
        /// Master gate for the automated rebalancer on this node. Defaults OFF.
        /// </summary>
        public bool AutoRebalanceEnabled { get; set; } = false;

        /// <summary>
        /// Allows the fee engine to set positive inbound fees on this node. Defaults OFF.
        /// </summary>
        public bool AllowPositiveInboundFees { get; set; } = false;

        /// <summary>
        /// Per-node dry-run for the routing-engine actuators: when true they log the
        /// fee/rebalance they would perform without calling LND.
        /// </summary>
        public bool RoutingEngineDryRun { get; set; } = false;

        /// <summary>
        /// Maximum sats spendable on rebalance fees over the budget refresh interval.
        /// </summary>
        public long? RebalanceBudgetSats { get; set; }

        /// <summary>
        /// Time interval after which the rebalance budget is refreshed.
        /// </summary>
        public TimeSpan? RebalanceBudgetRefreshInterval { get; set; }

        /// <summary>
        /// The datetime when the current rebalance budget period started.
        /// </summary>
        public DateTimeOffset? RebalanceBudgetStartDatetime { get; set; }

        /// <summary>
        /// Maximum number of concurrent (Pending/InFlight) rebalances for this node.
        /// </summary>
        public int? MaxRebalancesInFlight { get; set; }

        /// <summary>
        /// Maximum acceptable rebalance cost-to-earn ratio used by the profitability gate.
        /// </summary>
        public double? MaxRebalanceCostToEarnRatio { get; set; }

        /// <summary>
        /// Gate for automatic channel opening.
        /// </summary>
        public bool AutoChannelOpenEnabled { get; set; } = false;

        public AutoChannelOpenMode AutoChannelOpenMode { get; set; } = AutoChannelOpenMode.Recommendation;

        /// <summary>
        /// Wallet the opened channels are funded from. Without it no plan can be promoted.
        /// </summary>
        public int? AutoChannelOpenWalletId { get; set; }

        public Wallet? AutoChannelOpenWallet { get; set; }

        /// <summary>
        /// Every automatic open uses this size; a wallet or budget that can't cover it skips the peer.
        /// </summary>
        public long? AutoChannelOpenSizeSats { get; set; }

        public long? AutoChannelOpenBudgetSats { get; set; }

        public TimeSpan? AutoChannelOpenBudgetRefreshInterval { get; set; }

        public DateTimeOffset? AutoChannelOpenBudgetStartDatetime { get; set; }

        #endregion Routing Engine

        #region Relationships

        public ICollection<ChannelOpenRecommendation> ChannelOpenRecommendations { get; set; }

        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsSource { get; set; }
        public ICollection<ChannelOperationRequest> ChannelOperationRequestsAsDestination { get; set; }

        public ICollection<ApplicationUser> Users { get; set; }

        public List<SwapOut> SwapOuts { get; set; }

        #endregion Relationships
    }
}