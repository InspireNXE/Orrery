using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Orrery.App.Logging;

/// <summary>
/// Sends what libraries write to <see cref="Trace"/> (Avalonia's own warnings, mostly) to the log. Plain
/// Write/WriteLine calls, which carry no level, are debug messages.
/// </summary>
sealed class TraceBridge(ILogger logger) : TraceListener
{
    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
        logger.Log(LevelFor(eventType), "{Message}", message);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
        logger.Log(LevelFor(eventType), "{Message}", args is { Length: > 0 } && format is not null ? string.Format(format, args) : format);

    public override void Write(string? message) => logger.LogDebug("{Message}", message);

    public override void WriteLine(string? message) => logger.LogDebug("{Message}", message);

    static LogLevel LevelFor(TraceEventType type) => type switch
    {
        TraceEventType.Critical => LogLevel.Critical,
        TraceEventType.Error => LogLevel.Error,
        TraceEventType.Warning => LogLevel.Warning,
        TraceEventType.Information => LogLevel.Information,
        _ => LogLevel.Debug,
    };
}
