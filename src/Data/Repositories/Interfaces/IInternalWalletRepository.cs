/*
 * NodeGuard
 * Copyright (C) 2023  ClovrLabs
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
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 *
 */

﻿using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IInternalWalletRepository
{
    Task<InternalWallet?> GetById(int id);

    Task<List<InternalWallet>> GetAll();

    Task<(bool, string?)> AddAsync(InternalWallet type);

    Task<(bool, string?)> AddRangeAsync(List<InternalWallet> type);

    (bool, string?) Remove(InternalWallet type);

    (bool, string?) RemoveRange(List<InternalWallet> types);

    (bool, string?) Update(InternalWallet type);

    /// <summary>
    /// Gets the current internal wallet in use by the FundsManager (the newest based on ID)
    /// </summary>
    /// <returns></returns>
    /// <returns></returns>
    Task<InternalWallet?> GetCurrentInternalWallet();

    /// <summary>
    /// Generates a new internal wallet
    /// </summary>
    /// <param name="generateReadOnlyWallet"> Indicates if the wallet is read-only and the seedphrase should not be generated here</param>
    /// <returns></returns>
    Task<InternalWallet> GenerateNewInternalWallet(bool generateReadOnlyWallet = false);
}