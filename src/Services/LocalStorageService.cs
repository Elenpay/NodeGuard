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

using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace NodeGuard.Services;

public interface ILocalStorageService
{
    Task<T> LoadStorage<T>(string name, T defaultValue);
    Task SetStorage<T>(string name, T value);
}

public class LocalStorageService : ILocalStorageService
{
    private ProtectedLocalStorage ProtectedLocalStorage;

    public LocalStorageService(ProtectedLocalStorage protectedLocalStorage)
    {
        ProtectedLocalStorage = protectedLocalStorage;
    }

    public async Task<T> LoadStorage<T>(string name, T defaultValue)
    {
        if (defaultValue == null) return defaultValue;
        try
        {
            var result = await ProtectedLocalStorage.GetAsync<T>(name);
            if (result.Success && result.Value != null)
            {
                return result.Value;
            }

            throw new Exception("Value not found");
        }
        catch
        {
            await ProtectedLocalStorage.SetAsync(name, defaultValue);
        }

        return defaultValue;
    }

    public async Task SetStorage<T>(string name, T value)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        await ProtectedLocalStorage.SetAsync(name, value);
    }
}
