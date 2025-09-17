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

using Blazored.Toast.Services;

namespace NodeGuard.Helpers;

public static class ToastOnException
{
    private const string DefaultErrorMessage = "An error occurred";

    // async Try-catch wrapper for void methods
    public static async Task<T> ExecuteAsync<T>(Func<Task<T>> func, ILogger logger, IToastService toastService, string errorMessage = DefaultErrorMessage, T defaultValue = default!)
    {
        try
        {
            return await func.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, errorMessage);
            toastService.ShowError(errorMessage);
        }

        return defaultValue;
    }

    // sync Try-catch wrapper for methods that return a value
    public static T Execute<T>(Func<T> action, ILogger logger, IToastService toastService, string errorMessage = DefaultErrorMessage, T defaultValue = default!)
    {
        try
        {
            return action.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, errorMessage);
            toastService.ShowError(errorMessage);
        }

        return defaultValue;
    }
}
