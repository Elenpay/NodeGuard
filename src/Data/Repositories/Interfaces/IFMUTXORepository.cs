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

public interface IFMUTXORepository
{
    Task<FMUTXO?> GetById(int id);

    Task<List<FMUTXO>> GetAll();

    Task<(bool, string?)> AddAsync(FMUTXO type);

    Task<(bool, string?)> AddRangeAsync(List<FMUTXO> type);

    (bool, string?) Remove(FMUTXO type);

    (bool, string?) RemoveRange(List<FMUTXO> types);

    (bool, string?) Update(FMUTXO type);

    /// <summary>
    /// Gets the current list of UTXOs locked on requests ChannelOperationRequest / WalletWithdrawalRequest by passing its id if wants to remove it from the resulting set
    /// </summary>
    /// <returns></returns>
    Task<List<FMUTXO>> GetLockedUTXOs(int? ignoredWalletWithdrawalRequestId = null, int? ignoredChannelOperationRequestId = null);
    Task<List<FMUTXO>> GetLockedUTXOsByWalletId(int walletId);
}