using Serilog.Core;
using Serilog.Events;

namespace Orrery.App.Logging;

/// <summary>
/// Keeps one fault from filling the log: past <see cref="LinesPerWindow"/> lines with the same area, level and
/// message within <see cref="Window"/>, the rest are counted and written as one line when the window ends (on the
/// next line logged after it, or when the log closes).
/// </summary>
sealed class RepeatLimitingSink(ILogEventSink inner, int linesPerWindow = RepeatLimitingSink.LinesPerWindow, TimeSpan? window = null,
    TimeProvider? time = null) : ILogEventSink, IDisposable
{
    public const int LinesPerWindow = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    const int MaxTracked = 1000;

    readonly TimeSpan _window = window ?? Window;
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly object _gate = new();
    readonly Dictionary<(string? Source, LogEventLevel Level, string Template), Count> _counts = [];

    public void Emit(LogEvent logEvent)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            WriteEnded(now, all: false);

            var key = (Source(logEvent), logEvent.Level, logEvent.MessageTemplate.Text);
            if (!_counts.TryGetValue(key, out var count))
            {
                if (_counts.Count >= MaxTracked)
                {
                    inner.Emit(logEvent);
                    return;
                }

                count = new Count(now);
                _counts[key] = count;
            }

            if (count.Written < linesPerWindow)
            {
                count.Written++;
                inner.Emit(logEvent);
            }
            else
            {
                count.Skipped++;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            WriteEnded(_time.GetUtcNow(), all: true);
        }
    }

    void WriteEnded(DateTimeOffset now, bool all)
    {
        foreach (var (key, count) in _counts.Where(pair => all || pair.Value.Start + _window <= now).ToList())
        {
            _counts.Remove(key);
            if (count.Skipped > 0)
            {
                var summary = new LogEvent(now, key.Level, null,
                    new Serilog.Parsing.MessageTemplateParser().Parse("{Skipped} more like \"{Template}\" left out"),
                    [
                        new LogEventProperty("Skipped", new ScalarValue(count.Skipped)),
                        new LogEventProperty("Template", new ScalarValue(key.Template)),
                        new LogEventProperty("SourceContext", new ScalarValue(key.Source ?? "Orrery")),
                        new LogEventProperty("UtcTimestamp", new ScalarValue(now.UtcDateTime)),
                    ]);
                inner.Emit(summary);
            }
        }
    }

    static string? Source(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string source } ? source : null;

    sealed class Count(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; } = start;
        public int Written { get; set; }
        public int Skipped { get; set; }
    }
}
