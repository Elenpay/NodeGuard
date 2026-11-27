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

namespace NodeGuard.Data.Models;

/// <summary>
/// A peer that gets its own share of the node's channel open budget, such as a swap service. It skips
/// the earning evidence: it qualifies on low outbound alone, or on having no channel with us at all.
/// The peer is one of the registered nodes, so its name and pubkey are read from there.
/// <para>
/// One row per (<see cref="NodeId"/>, <see cref="PeerNodeId"/>). Shares of one node sum to at most 100.
/// </para>
/// </summary>
public class ReservedChannelOpenPeer : Entity
{
    public int NodeId { get; set; }
    public Node? Node { get; set; }

    public int PeerNodeId { get; set; }
    public Node? PeerNode { get; set; }

    /// Share of the node's channel open budget reserved for this peer, 1 to 100.
    public int BudgetPct { get; set; }
}
