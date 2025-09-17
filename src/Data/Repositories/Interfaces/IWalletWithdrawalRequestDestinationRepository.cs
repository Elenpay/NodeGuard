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

namespace NodeGuard.Data.Repositories.Interfaces;

public interface IWalletWithdrawalRequestDestinationRepository
{
    Task<WalletWithdrawalRequestDestination?> GetById(int id);

    Task<List<WalletWithdrawalRequestDestination>> GetAll();

    Task<List<WalletWithdrawalRequestDestination>> GetByWalletWithdrawalRequestId(int walletWithdrawalRequestId);

    Task<(bool, string?)> AddAsync(WalletWithdrawalRequestDestination type);

    Task<(bool, string?)> AddRangeAsync(List<WalletWithdrawalRequestDestination> type);

    (bool, string?) Remove(WalletWithdrawalRequestDestination type);

    (bool, string?) RemoveRange(List<WalletWithdrawalRequestDestination> types);

    (bool, string?) Update(WalletWithdrawalRequestDestination type);
}
