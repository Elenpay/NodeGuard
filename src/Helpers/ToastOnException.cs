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