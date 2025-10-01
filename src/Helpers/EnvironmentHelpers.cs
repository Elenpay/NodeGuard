namespace NodeGuard.Helpers
{
    public static class EnvironmentHelpers
    {
        public static T GetOrDefault<T>(string variableName, T defaultValue)
        {
            var value = System.Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrEmpty(value))
                return defaultValue;

            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}