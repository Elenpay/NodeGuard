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

using System.ComponentModel.DataAnnotations.Schema;

namespace NodeGuard.Data.Models;

/// <summary>
/// A rule for setting liquidity automation on NodeGuard
/// </summary>
public class LiquidityRule : Entity
{

    public decimal? MinimumLocalBalance { get; set; }

    public decimal? MinimumRemoteBalance { get; set; }

    /// <summary>
    /// Target between 0 and 1 that we would like for the channel to be balanced after a rebalancing operation is complete
    /// </summary>
    public decimal? RebalanceTarget { get; set; }

    /// <summary>
    /// Let's you know if the rule has a wallet or an address as a target for the rebalancing operation
    /// </summary>
    public bool IsReverseSwapWalletRule { get; set; }

    /// <summary>
    /// In case that is a rule that sends the funds to an address instead of a wallet this is the address
    /// </summary>
    public string? ReverseSwapAddress { get; set; }

    #region Relationships

    public int ChannelId { get; set; }
    public required Channel Channel { get; set; }

    public int SwapWalletId { get; set; }
    public required Wallet SwapWallet { get; set; }

    public int? ReverseSwapWalletId { get; set; }
    public Wallet? ReverseSwapWallet { get; set; }

    public int NodeId { get; set; }
    public required Node Node { get; set; }

    /// <summary>
    /// The pubkey of the node that is the remote counterparty of the channel
    /// </summary>
    [NotMapped]
    public string? RemoteNodePubkey => Channel?.SourceNode?.PubKey != Node.PubKey ? Channel?.SourceNode?.PubKey : Channel?.DestinationNode?.PubKey;

    #endregion

}
