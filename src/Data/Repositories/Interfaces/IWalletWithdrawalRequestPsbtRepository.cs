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

public interface IWalletWithdrawalRequestPsbtRepository
{
    Task<WalletWithdrawalRequestPSBT?> GetById(int id);
    Task<List<WalletWithdrawalRequestPSBT>> GetAll();
    Task<(bool, string?)> AddAsync(WalletWithdrawalRequestPSBT type);
    Task<(bool, string?)> AddRangeAsync(List<WalletWithdrawalRequestPSBT> type);
    (bool, string?) Remove(WalletWithdrawalRequestPSBT type);
    (bool, string?) RemoveRange(List<WalletWithdrawalRequestPSBT> types);
    (bool, string?) Update(WalletWithdrawalRequestPSBT type);
}