using System;

namespace MasterRelayVPN.Models;

public enum LogLevel { Debug, Info, Warning, Error }

public record LogEntry(DateTime Time, LogLevel Level, string Source, string Message)
{
    public string LevelShort => Level switch
    {
        LogLevel.Debug   => "DBG",
        LogLevel.Info    => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error   => "ERR",
        _ => "?"
    };
}
