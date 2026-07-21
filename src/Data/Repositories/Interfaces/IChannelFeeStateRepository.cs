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

public interface IChannelFeeStateRepository
{
    Task<ChannelFeeState?> GetByChannelId(int channelId);

    /// <summary>
    /// All fee-state rows for channels owned by the given managed node.
    /// Used by the fee engine to batch per-node state.
    /// </summary>
    Task<List<ChannelFeeState>> GetByManagedNodePubKey(string managedNodePubKey);

    Task UpsertByChannelId(ChannelFeeState state);

    /// <summary>
    /// Deletes the fee-state row for a single channel, if present.
    /// </summary>
    /// <returns>The number of rows deleted (0 or 1).</returns>
    Task<int> DeleteByChannelId(int channelId);

    /// <summary>
    /// Deletes the fee-state rows for every channel owned by the given managed node.
    /// </summary>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteByManagedNodePubKey(string managedNodePubKey);
}
