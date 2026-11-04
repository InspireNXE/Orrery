using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.File;

namespace Orrery.App.Logging;

/// <summary>
/// The log file: one a day in the data location's log folder (<c>orrery-20261031.log</c>), a new one
/// (<c>orrery-20261031_001.log</c>) whenever one reaches <see cref="FileSizeLimit"/>, kept for <see cref="KeepFor"/> and
/// to <see cref="MaxTotalSize"/> in all. Names and addresses are hidden (<see cref="MaskingSink"/>) unless
/// <see cref="ShowNames"/> is on. Started once the data folder is known.
/// Never log passwords, keys, passphrases, clipboard text, keystrokes or session output.
/// </summary>
static class AppLog
{
    public const long FileSizeLimit = 10 * 1024 * 1024;
    public const long MaxTotalSize = 100 * 1024 * 1024;
    const string FilePattern = "orrery-*.log";
    const string OutputTemplate = "{UtcTimestamp:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {Level:u3} {SourceContext}: {Message:l}{NewLine}{" + MaskingSink.FailureProperty + ":l}";

    static readonly LoggingLevelSwitch Level = new(LogEventLevel.Information);
    static Serilog.Core.Logger? _file;
    static Serilog.Core.Logger? _logger;
    static SerilogLoggerFactory? _factory;
    static RepeatLimitingSink? _limiter;
    static TraceBridge? _traceBridge;
    static DataLocation? _location;
    static LogMask? _mask;
    static bool _showNames;
    static TimeSpan _keepFor = AppSettings.DefaultKeepLogsFor;

    /// <summary>The folder logs are written to; null until <see cref="Start"/>.</summary>
    public static string? Folder { get; private set; }

    /// <summary>Detailed logging, for troubleshooting: debug messages too, until Orrery closes.</summary>
    public static bool Detailed
    {
        get => Level.MinimumLevel <= LogEventLevel.Debug;
        set
        {
            if (value == Detailed)
            {
                return;
            }

            Level.MinimumLevel = value ? LogEventLevel.Debug : LogEventLevel.Information;
            Core.Diagnostics.Log.For("App").LogInformation("Detailed logging turned {State}", value ? "on" : "off");
        }
    }

    /// <summary>Names and addresses written as they are rather than as tags, until Orrery closes.</summary>
    public static bool ShowNames
    {
        get => _showNames;
        set
        {
            if (value == _showNames)
            {
                return;
            }

            // Said while still hidden, so a log someone shares says from where on it holds real names.
            if (value)
            {
                Core.Diagnostics.Log.For("App").LogInformation("Names and addresses are shown from here on, until Orrery closes");
            }

            _showNames = value;
            if (!value)
            {
                Core.Diagnostics.Log.For("App").LogInformation("Names and addresses are hidden again");
            }
        }
    }

    /// <summary>How long log files are kept; older ones are deleted when a file starts and when this changes.</summary>
    public static TimeSpan KeepFor
    {
        get => _keepFor;
        set
        {
            _keepFor = value;
            if (Folder is { } folder)
            {
                Prune(folder, value, MaxTotalSize, DateTime.UtcNow, current: null);
            }
        }
    }

    /// <summary>What a tag in the log stands for, or the tags a value shows as; see <see cref="LogMask.Lookup"/>.</summary>
    public static IReadOnlyList<LogMaskMatch> Lookup(string text) => _mask?.Lookup(text) ?? [];

    /// <summary>Starts writing to <paramref name="location"/>'s log folder. Logging failing never stops the app.</summary>
    public static void Start(DataLocation location, TimeSpan? keepFor = null)
    {
        if (_logger is not null)
        {
            return;
        }

        try
        {
            _keepFor = keepFor ?? _keepFor;
            Directory.CreateDirectory(location.LogDir);
            if (_mask is null || _location != location)
            {
                _mask = LogMask.For(location);
            }

            _file = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(Path.Combine(location.LogDir, "orrery-.log"),
                    outputTemplate: OutputTemplate,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimit,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: null,
                    buffered: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    shared: false,
                    hooks: new PruneWhenOpened())
                .CreateLogger();

            _limiter = new RepeatLimitingSink(new MaskingSink(_file, _mask, () => _showNames));
            _logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(Level)
                .MinimumLevel.Override("MarcusW", LogEventLevel.Warning)
                .Enrich.With<UtcTimestampEnricher>()
                .WriteTo.Sink(_limiter)
                .CreateLogger();
            _factory = new SerilogLoggerFactory(_logger);
            _location = location;
            Folder = location.LogDir;
            Core.Diagnostics.Log.Mask = _mask;
            Core.Diagnostics.Log.Factory = _factory;

            // Avalonia and anything else still writing to Trace lands in the same file.
            _traceBridge = new TraceBridge(Core.Diagnostics.Log.For("Trace"));
            Trace.Listeners.Add(_traceBridge);
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            WriteStartup(location);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceError($"Couldn't start the log in {location.LogDir}: {e.Message}");
        }
    }

    static void WriteStartup(DataLocation location)
    {
        var app = Core.Diagnostics.Log.For("App");
        var version = typeof(AppLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppLog).Assembly.GetName().Version?.ToString() ?? "unknown";
        app.LogInformation("Orrery {Version} starting on {Os} ({Architecture}), .NET {Runtime}", version.Split('+')[0],
            RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture, Environment.Version);
        app.LogInformation("{Mode} mode, data in {DataFolder}", location.Mode, _showNames ? location.DataDir : HomeShortened(location.DataDir));
        app.LogInformation("Names and addresses are {Shown}; logs are kept for {Days} days", _showNames ? "shown" : "hidden", (int)_keepFor.TotalDays);
    }

    // Orrery's own folder is worth seeing; the account name in it isn't.
    static string HomeShortened(string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && folder.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + folder[home.Length..] : folder;
    }

    /// <summary>Writes everything still buffered; called on exit.</summary>
    public static void Stop()
    {
        if (_logger is null)
        {
            return;
        }

        Core.Diagnostics.Log.For("App").LogInformation("Orrery closing");
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        if (_traceBridge is not null)
        {
            Trace.Listeners.Remove(_traceBridge);
        }

        Core.Diagnostics.Log.Factory = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        Core.Diagnostics.Log.Mask = null;
        _limiter?.Dispose();
        _logger.Dispose();
        _file?.Dispose();
        _factory?.Dispose();
        (_file, _logger, _factory, _limiter, _traceBridge, Folder) = (null, null, null, null, null, null);
    }

    /// <summary>
    /// Deletes every log file in <paramref name="location"/>'s log folder, the one being written too (the log starts again
    /// in a new file). Returns how many were deleted; one that can't be is left.
    /// </summary>
    public static int DeleteLogs(DataLocation location)
    {
        bool running = _logger is not null && _location == location;
        if (running)
        {
            Stop();
        }

        int deleted = 0;
        try
        {
            foreach (var file in Directory.Exists(location.LogDir) ? Directory.GetFiles(location.LogDir, FilePattern) : [])
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        finally
        {
            if (running)
            {
                Start(location);
                Core.Diagnostics.Log.For("App").LogInformation("Deleted {Count} earlier log file(s)", deleted);
            }
        }

        return deleted;
    }

    /// <summary>
    /// Deletes log files last written more than <paramref name="keepFor"/> ago, then the oldest while all of them come to
    /// more than <paramref name="maxTotal"/>. The newest file, and <paramref name="current"/>, are always kept.
    /// </summary>
    internal static void Prune(string folder, TimeSpan keepFor, long maxTotal, DateTime nowUtc, string? current)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles(FilePattern).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            var open = current is null ? null : Path.GetFullPath(current);
            long total = 0;
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i];
                total += file.Length;
                bool keep = i == 0 || string.Equals(file.FullName, open, StringComparison.OrdinalIgnoreCase);
                if (!keep && (file.LastWriteTimeUtc < nowUtc - keepFor || total > maxTotal))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The process ends after this, so the buffer is written out here or not at all.
    static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Core.Diagnostics.Log.For("App").LogCritical(e.ExceptionObject as Exception, "Orrery is closing after an unhandled exception");
        Stop();
    }

    sealed class UtcTimestampEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("UtcTimestamp", logEvent.Timestamp.UtcDateTime));
    }

    // Each new file (a new day, or the last one full) is when old ones go.
    sealed class PruneWhenOpened : FileLifecycleHooks
    {
        public override Stream OnFileOpened(string path, Stream underlyingStream, Encoding encoding)
        {
            if (Path.GetDirectoryName(path) is { } folder)
            {
                Prune(folder, _keepFor, MaxTotalSize, DateTime.UtcNow, path);
            }

            return underlyingStream;
        }
    }
}
