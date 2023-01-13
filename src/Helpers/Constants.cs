using System.Globalization;

public class Constants
{
    public static readonly long MINIMUM_CHANNEL_CAPACITY_SATS;
    
    static Constants()
    {
        var environmentVariable = Environment.GetEnvironmentVariable("MINIMUM_CHANNEL_CAPACITY_SATS") ?? throw new InvalidOperationException("MINIMUM_CHANNEL_CAPACITY_SATS not set");
        MINIMUM_CHANNEL_CAPACITY_SATS = long.Parse(environmentVariable, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }
}