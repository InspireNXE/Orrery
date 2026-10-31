using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Orrery.App.Logging;
using Orrery.App.Sessions;
using Orrery.App.Settings;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Orrery.App.Tests;

// The log is one per process, so these run one at a time.
[Collection(nameof(LoggingTests))]
[CollectionDefinition(nameof(LoggingTests), DisableParallelization = true)]
public class LoggingTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    DataLocation Location => new(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));

    [Fact]
    public void Writes_startup_messages_and_trace_output_to_a_dated_file()
    {
        AppLog.Start(Location);
        try
        {
            Log.For("Test").LogWarning("Something odd about {Host}", "server1");
            Trace.TraceError("A library complained");
        }
        finally
        {
            AppLog.Stop();
        }

        var file = Assert.Single(Directory.GetFiles(Location.LogDir));
        Assert.Equal($"orrery-{DateTime.Now:yyyyMMdd}.log", Path.GetFileName(file));
        var text = File.ReadAllText(file);
        Assert.Contains("INF App: Orrery ", text);
        Assert.Contains("INF App: Portable mode, data in " + _root, text);
        Assert.Contains("WRN Test: Something odd about server1", text);
        Assert.Contains("ERR Trace: A library complained", text);
        Assert.Contains("INF App: Orrery closing", text);
    }

    [Fact]
    public void Debug_messages_are_written_only_while_detailed_logging_is_on()
    {
        AppLog.Start(Location);
        try
        {
            Log.For("Test").LogDebug("Quiet one");
            AppLog.Detailed = true;
            Log.For("Test").LogDebug("Loud one");
        }
        finally
        {
            AppLog.Detailed = false;
            AppLog.Stop();
        }

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(Location.LogDir)));
        Assert.DoesNotContain("Quiet one", text);
        Assert.Contains("DBG Test: Loud one", text);
    }

    [Fact]
    public void A_message_repeating_is_cut_short_and_counted_when_the_window_ends()
    {
        var written = new List<LogEvent>();
        var time = new ManualTime();
        var sink = new RepeatLimitingSink(new ListSink(written), linesPerWindow: 2, window: TimeSpan.FromMinutes(1), time);

        for (var i = 0; i < 5; i++)
        {
            sink.Emit(Event(time, "Couldn't reach {Host}", "server1"));
        }

        sink.Emit(Event(time, "Something else"));
        Assert.Equal(3, written.Count);

        time.Now += TimeSpan.FromMinutes(2);
        sink.Emit(Event(time, "Couldn't reach {Host}", "server1"));

        Assert.Equal(5, written.Count);
        Assert.Equal("{Skipped} more like \"{Template}\" left out", written[3].MessageTemplate.Text);
        Assert.Equal(3, ((ScalarValue)written[3].Properties["Skipped"]).Value);
        Assert.Equal("Couldn't reach {Host}", ((ScalarValue)written[3].Properties["Template"]).Value);
        Assert.Equal("Couldn't reach \"server1\"", written[4].RenderMessage());
    }

    [Fact]
    public void Leftover_repeats_are_counted_when_the_log_closes()
    {
        var written = new List<LogEvent>();
        var time = new ManualTime();
        var sink = new RepeatLimitingSink(new ListSink(written), linesPerWindow: 1, window: TimeSpan.FromMinutes(1), time);
        sink.Emit(Event(time, "Again"));
        sink.Emit(Event(time, "Again"));

        sink.Dispose();

        Assert.Equal(2, written.Count);
        Assert.StartsWith("1 more like", written[1].RenderMessage());
    }

    [Theory]
    [InlineData(42, "42 s")]
    [InlineData(725, "12 min 5 s")]
    [InlineData(12_000, "3 h 20 min")]
    public void Durations_read_plainly(int seconds, string expected) =>
        Assert.Equal(expected, SessionLogger.Duration(TimeSpan.FromSeconds(seconds)));

    [AvaloniaFact]
    public void Settings_shows_the_log_folder_and_a_detailed_logging_switch()
    {
        var settings = AppSettingsStore.In(Location);
        using var vault = new VaultFileStore(Location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var window = new Window { Content = new SettingsView(Location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask) };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Location.LogDir);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Open log folder");
        var detailed = Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), c => (c.Content as string)?.StartsWith("Detailed logging") == true);
        Assert.False(detailed.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public void A_session_logs_opening_connecting_and_how_it_ended_under_its_own_id()
    {
        AppLog.Start(Location);
        try
        {
            var dropped = new FakeSession();
            var tab = new SessionTab(dropped, new Border(), new SessionOrigin("web", Protocol.Ssh, null, () => Task.CompletedTask));
            var id = SessionLogger.Attach(tab).Id;
            dropped.Set(SessionState.Connected);
            Dispatcher.UIThread.RunJobs();
            dropped.Set(SessionState.Failed, "The network went away");
            Dispatcher.UIThread.RunJobs();

            var stillOpen = new FakeSession();
            var openId = SessionLogger.Attach(new SessionTab(stillOpen, new Border(), new SessionOrigin("db", Protocol.Rdp, null, () => Task.CompletedTask))).Id;
            stillOpen.Set(SessionState.Connected);
            Dispatcher.UIThread.RunJobs();
            SessionLogger.CloseAll();

            AppLog.Stop();
            var text = File.ReadAllText(Assert.Single(Directory.GetFiles(Location.LogDir)));
            Assert.Matches(@"^ssh-[0-9a-f]{4}$", id);
            Assert.Contains($"INF Session: [{id}] Opening \"web\" on box:22", text);
            Assert.Contains($"INF Session: [{id}] Connected in ", text);
            Assert.Contains($"WRN Session: [{id}] Dropped after 0 s: The network went away", text);
            Assert.Contains($"INF Session: [{openId}] Closed with Orrery after 0 s", text);
        }
        finally
        {
            AppLog.Stop();
        }
    }

    sealed class FakeSession : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State { get; private set; } = SessionState.Connecting;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Set(SessionState state, string? message = null)
        {
            State = state;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
        }
    }

    static LogEvent Event(ManualTime time, string template, params object[] values)
    {
        var parsed = new MessageTemplateParser().Parse(template);
        var names = parsed.Tokens.OfType<PropertyToken>().Select(t => t.PropertyName).ToList();
        return new LogEvent(time.GetUtcNow(), LogEventLevel.Warning, null, parsed,
            names.Zip(values, (name, value) => new LogEventProperty(name, new ScalarValue(value))));
    }

    sealed class ListSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => events.Add(logEvent);
    }

    sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
