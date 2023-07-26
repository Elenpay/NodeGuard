using System.Text.Json.Serialization;

namespace NodeGuard.Helpers;

public class ChannelStatusLog
{
    public DateTime DateTime { get; set; }
    public LogLevel Level { get; set; }
    public string Description { get; set; }
    
    [JsonConstructor]
    public ChannelStatusLog(DateTime dateTime, LogLevel level, string description)
    {
        DateTime = dateTime;
        Level = level;
        Description = description;
    }
    
    public ChannelStatusLog(LogLevel level, string description)
    {
        DateTime = DateTime.Now;
        Level = level;
        Description = description;
    }
           
    public static ChannelStatusLog Info(string description)
    {
        return new ChannelStatusLog(LogLevel.Information, description);
    }
        
    public static ChannelStatusLog Error(string description)
    {
        return new ChannelStatusLog(LogLevel.Error, description);
    }
        
    public static ChannelStatusLog Warning(string description)
    {
        return new ChannelStatusLog(LogLevel.Warning, description);
    }
}