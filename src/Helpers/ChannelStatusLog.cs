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
