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

public interface ISwapOutRepository
{
   Task<SwapOut?> GetById(int id);
   Task<List<SwapOut>> GetByIds(List<int> ids);
   Task<List<SwapOut>> GetAll();
   Task<(bool, string?)> AddAsync(SwapOut swap);
   Task<(bool, string?)> AddRangeAsync(List<SwapOut> swaps);
   (bool, string?) Remove(SwapOut swap);
   (bool, string?) RemoveRange(List<SwapOut> swaps);
   (bool, string?) Update(SwapOut swap);
}