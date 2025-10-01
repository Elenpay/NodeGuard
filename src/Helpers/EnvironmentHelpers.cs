namespace NodeGuard.Helpers
{
    public static class EnvironmentHelpers
    {
        /// <summary>
        /// Gets the value of an environment variable and converts it to the specified type.
        /// If the environment variable is not set or is empty, returns the provided default value.
        /// </summary>
        /// <typeparam name="T">The type to convert the environment variable value to.</typeparam>
        /// <param name="variableName">The name of the environment variable.</param>
        /// <param name="defaultValue">The default value to return if the environment variable is not set or is empty.</param>
        /// <returns>The value of the environment variable converted to type T, or the default value.</returns>
        /// <exception cref="InvalidCastException">Thrown if the environment variable value cannot be converted to type T.</exception>
        /// <exception cref="FormatException">Thrown if the environment variable value is not in a format recognized by type T.</exception>
        /// <exception cref="OverflowException">Thrown if the environment variable value represents a number less than MinValue or greater than MaxValue of type T. (If applicable)</exception>
        public static T GetOrDefault<T>(string variableName, T defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrEmpty(value))
                return defaultValue;

            return (T)Convert.ChangeType(value, typeof(T));
        }
    }
}