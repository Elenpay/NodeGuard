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

using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IChannelOperationRequestRepository : IBitcoinRequestRepository
{
    Task<ChannelOperationRequest?> GetById(int id);

    Task<List<ChannelOperationRequest>> GetAll();

    Task<List<ChannelOperationRequest>> GetUnsignedPendingRequestsByUser(string userId);

    Task<(bool, string?)> AddAsync(ChannelOperationRequest type);

    Task<(bool, string?)> AddRangeAsync(List<ChannelOperationRequest> type);

    (bool, string?) Remove(ChannelOperationRequest type);

    (bool, string?) RemoveRange(List<ChannelOperationRequest> types);

    (bool, string?) Update(ChannelOperationRequest type);

    /// <summary>
    /// Adds on the many-to-many collection the list of utxos provided
    /// </summary>
    /// <param name="type"></param>
    /// <param name="utxos"></param>
    /// <returns></returns>
    Task<(bool, string?)> AddUTXOs(IBitcoinRequest type, List<FMUTXO> utxos);

    /// <summary>
    /// Returns those requests that can have a PSBT locked until they are confirmed / rejected / cancelled
    /// </summary>
    /// <returns></returns>
    Task<List<ChannelOperationRequest>> GetPendingRequests();
}