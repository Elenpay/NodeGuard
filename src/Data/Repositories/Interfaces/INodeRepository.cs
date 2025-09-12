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
    Task<List<Node>> GetAllLoopConfigured(string? userId = null);

    Task<List<Node>> GetAllManagedByNodeGuard(bool withDisabled = true);

    Task<(bool, string?)> AddAsync(Node type);

    Task<(bool, string?)> AddRangeAsync(List<Node> type);

    (bool, string?) Remove(Node type);

    (bool, string?) RemoveRange(List<Node> types);

    (bool, string?) Update(Node type);
}
