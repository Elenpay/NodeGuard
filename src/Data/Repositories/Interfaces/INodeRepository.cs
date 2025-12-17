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
using NodeGuard.Services;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface INodeRepository
{
    Task<Node?> GetById(int id);

    Task<Node?> GetByPubkey(string key);

    public Task<Node> GetOrCreateByPubKey(string pubKey, ILightningService lightningService);

    Task<List<Node>> GetAll();

    Task<List<Node>> GetAllManagedByUser(string userId);
    
    /// <summary>
    /// Get all nodes that are configured to work with the specified swap provider.
    /// For Loop: checks if the endpoint and macaroon are set in the database.
    /// For 40swap: checks if the endpoint is set in the database.
    /// </summary>
    /// <param name="provider">The swap provider to filter nodes by</param>
    /// <param name="userId">Optional user ID to filter nodes by user</param>
    /// <returns>A list of nodes configured for the specified provider</returns>
    Task<List<Node>> GetAllConfiguredByProvider(SwapProvider provider, string? userId = null);

    Task<List<Node>> GetAllManagedByNodeGuard(bool withDisabled = true);

    /// <summary>
    /// Get all nodes that have auto liquidity management enabled and configured
    /// </summary>
    /// <returns>A list of nodes with auto liquidity management enabled</returns>
    Task<List<Node>> GetAllWithAutoLiquidityEnabled();

    Task<(bool, string?)> AddAsync(Node type);

    Task<(bool, string?)> AddRangeAsync(List<Node> type);

    (bool, string?) Remove(Node type);

    (bool, string?) RemoveRange(List<Node> types);

    (bool, string?) Update(Node type);
}
