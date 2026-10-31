using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Storage;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace Orrery.App.Logging;

/// <summary>
/// The log file: one per day in the data location's log folder (<c>orrery-20261031.log</c>), up to
/// <see cref="FileSizeLimit"/> each, kept for <see cref="KeepFor"/>. Started once the data folder is known.
/// Never log passwords, keys, passphrases, clipboard text, keystrokes or session output.
/// </summary>
static class AppLog
{
    public const long FileSizeLimit = 10 * 1024 * 1024;
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);
    const string OutputTemplate = "{UtcTimestamp:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} {Level:u3} {SourceContext}: {Message:l}{NewLine}{Exception}";

    static readonly LoggingLevelSwitch Level = new(LogEventLevel.Information);
    static Serilog.Core.Logger? _file;
    static Serilog.Core.Logger? _logger;
    static SerilogLoggerFactory? _factory;
    static RepeatLimitingSink? _limiter;
    static TraceBridge? _traceBridge;

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

    /// <summary>Starts writing to <paramref name="location"/>'s log folder. Logging failing never stops the app.</summary>
    public static void Start(DataLocation location)
    {
        if (_logger is not null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(location.LogDir);
            _file = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(Path.Combine(location.LogDir, "orrery-.log"),
                    outputTemplate: OutputTemplate,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: FileSizeLimit,
                    rollOnFileSizeLimit: false,
                    retainedFileCountLimit: null,
                    retainedFileTimeLimit: KeepFor,
                    buffered: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1),
                    shared: false)
                .CreateLogger();

            _limiter = new RepeatLimitingSink(_file);
            _logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(Level)
                .Enrich.With<UtcTimestampEnricher>()
                .WriteTo.Sink(_limiter)
                .CreateLogger();
            _factory = new SerilogLoggerFactory(_logger);
            Folder = location.LogDir;
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
        app.LogInformation("{Mode} mode, data in {DataDir}", location.Mode, location.DataDir);
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
        _limiter?.Dispose();
        _logger.Dispose();
        _file?.Dispose();
        _factory?.Dispose();
        (_file, _logger, _factory, _limiter, _traceBridge) = (null, null, null, null, null);
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
}
