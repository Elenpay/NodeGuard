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

using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories;

public interface ILiquidityRuleRepository
{
    Task<LiquidityRule?> GetById(int id);
    Task<List<LiquidityRule>> GetAll();
    Task<(bool, string?)> AddAsync(LiquidityRule type);
    Task<(bool, string?)> AddRangeAsync(List<LiquidityRule> type);
    (bool, string?) Remove(LiquidityRule type);
    (bool, string?) RemoveRange(List<LiquidityRule> types);
    (bool, string?) Update(LiquidityRule type);

    /// <summary>
    /// Gets all the liquidity rules for a given node
    /// </summary>
    /// <param name="nodePubKey"></param>
    /// <returns></returns>
    Task<ICollection<LiquidityRule>> GetByNodePubKey(string nodePubKey);
}
