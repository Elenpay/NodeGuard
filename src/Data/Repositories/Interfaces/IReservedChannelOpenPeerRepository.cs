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

using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IReservedChannelOpenPeerRepository
{
    /// <summary>The node's reserved peers, each with its <see cref="ReservedChannelOpenPeer.PeerNode"/>.</summary>
    Task<List<ReservedChannelOpenPeer>> GetByNode(int nodeId);

    /// <summary>
    /// Replaces the node's reserved peers with <paramref name="peers"/> atomically, so a half-saved list can
    /// never push the shares past 100%.
    /// </summary>
    Task<(bool, string?)> ReplaceForNode(int nodeId, IReadOnlyList<ReservedChannelOpenPeer> peers);
}
