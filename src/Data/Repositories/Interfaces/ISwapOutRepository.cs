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

using NBitcoin;
using NodeGuard.Data.Models;

namespace NodeGuard.Data.Repositories.Interfaces;

public interface ISwapOutRepository
{
   Task<SwapOut?> GetById(int id);
   Task<List<SwapOut>> GetByIds(List<int> ids);
   Task<(List<SwapOut> swaps, int totalCount)> GetPaginatedAsync(
      int pageNumber,
      int pageSize,
      SwapOutStatus? status = null,
      SwapProvider? provider = null,
      int? nodeId = null,
      int? walletId = null,
      string? userId = null,
      bool? isManual = null,
      DateTimeOffset? fromDate = null,
      DateTimeOffset? toDate = null);
   Task<List<SwapOut>> GetAllPending();
   Task<(bool, string?)> AddAsync(SwapOut swap);
   Task<(bool, string?)> AddRangeAsync(List<SwapOut> swaps);
   (bool, string?) Update(SwapOut swap);
   
   /// <summary>
   /// Get all swaps that are currently in flight (not completed, failed, or timed out) for a specific node, swaps older than 24 hours are ignored to avoid swaps deadlocks if many are stuck
   /// </summary>
   Task<List<SwapOut>> GetInFlightSwapsByNode(int nodeId);
   
   /// <summary>
   /// Calculate the total amount spent on swaps since a specific datetime for budget tracking
   /// </summary>
   Task<Money> GetConsumedFeesSince(int nodeId, DateTimeOffset since);
}