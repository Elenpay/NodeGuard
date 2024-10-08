using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace NodeGuard.Services;

public interface  ILocalStorageService
{
    Task<T?> LoadStorage<T>(string name, T? defaultValue = default);
    Task SetStorage<T>(string name, T value);
}

public class LocalStorageService: ILocalStorageService
{
    private ProtectedLocalStorage ProtectedLocalStorage;

    public LocalStorageService(ProtectedLocalStorage protectedLocalStorage)
    {
        ProtectedLocalStorage = protectedLocalStorage;
    }

    public async Task<T?> LoadStorage<T>(string name, T? defaultValue = default)
    {
        if (defaultValue == null) return defaultValue;
        try
        {
            var result = await ProtectedLocalStorage.GetAsync<T>(name);
            if (result.Success)
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
        await ProtectedLocalStorage.SetAsync(name, value);
    }
}
