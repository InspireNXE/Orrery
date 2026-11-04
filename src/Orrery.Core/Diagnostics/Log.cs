using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Orrery.Core.Diagnostics;

/// <summary>
/// Where every part of Orrery gets its logger. The app points <see cref="Factory"/> at the log file once it knows
/// where the data folder is; until then, and in tests that don't set it, messages go nowhere.
/// Never log passwords, keys, passphrases, clipboard text, keystrokes or session output.
/// </summary>
public static class Log
{
    static readonly ConcurrentDictionary<string, ILogger> Loggers = new(StringComparer.Ordinal);

    public static ILoggerFactory Factory { get; set; } = NullLoggerFactory.Instance;

    /// <summary>What hides names and addresses in the log file; null until the app starts the log, and in tests that don't.</summary>
    public static LogMask? Mask { get; set; }

    /// <summary>Remembers a value the log should hide wherever it turns up, such as a saved connection's host.</summary>
    public static void Learn(LogMaskKind kind, string? value) => Mask?.Learn(kind, value);

    /// <summary>
    /// A logger for an area of the app, such as "Ssh" or "Storage". Safe to keep in a static field: it follows
    /// <see cref="Factory"/> when that changes.
    /// </summary>
    public static ILogger For(string category) => Loggers.GetOrAdd(category, c => new FollowingLogger(c));

    /// <summary>Follows whatever <see cref="Factory"/> is set to, so loggers made before startup still reach the file.</summary>
    sealed class FollowingLogger(string category) : ILogger
    {
        (ILoggerFactory Factory, ILogger Logger) _current = (NullLoggerFactory.Instance, NullLogger.Instance);

        ILogger Inner
        {
            get
            {
                var current = _current;
                var factory = Factory;
                if (!ReferenceEquals(current.Factory, factory))
                {
                    current = (factory, factory.CreateLogger(category));
                    _current = current;
                }

                return current.Logger;
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
