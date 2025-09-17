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

public interface IUTXOTagRepository
{
    Task<List<UTXOTag>> GetByOutpoint(string outpoint);

    Task<UTXOTag?> GetByKeyAndOutpoint(string key, string outpoint);

    Task<List<UTXOTag>> GetByKeyValue(string key, string value);

    Task<(bool, string?)> AddAsync(UTXOTag type);

    Task<(bool, string?)> AddRangeAsync(List<UTXOTag> type);

    (bool, string?) Remove(UTXOTag type);

    (bool, string?) RemoveRange(List<UTXOTag> types);

    (bool, string?) Update(UTXOTag type);

    Task<(bool, string?)> UpsertRangeAsync(List<UTXOTag> type);
}
