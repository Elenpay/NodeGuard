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

﻿using FundsManager.Data.Models;

namespace FundsManager.Data.Repositories.Interfaces;

public interface IWalletRepository
{
    Task<Wallet?> GetById(int id);

    Task<List<Wallet>> GetAll();

    /// <summary>
    /// Obtains all wallets that are Finalised and not in a compromised or archived state
    /// </summary>
    /// <returns> List of available wallets</returns>
    Task<List<Wallet>> GetAvailableWallets();

    Task<(bool, string?)> AddAsync(Wallet type);

    Task<(bool, string?)> AddRangeAsync(List<Wallet> type);

    (bool, string?) Remove(Wallet type);

    (bool, string?) RemoveRange(List<Wallet> types);

    (bool, string?) Update(Wallet type, bool includeKeysUpdate = false);

    /// <summary>
    /// Enables the tracking of this wallet and locks the edition of its parameters other than name and description
    /// </summary>
    /// <param name="selectedWalletToFinalise"></param>
    /// <returns></returns>
    Task<(bool, string?)> FinaliseWallet(Wallet selectedWalletToFinalise);
}