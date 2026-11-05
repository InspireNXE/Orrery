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
        // Logs in the data folder may be synced to other computers, so the name says which computer wrote it.
        var tag = DataFolderPresence.ComputerTag(Environment.MachineName);
        Assert.Equal($"orrery-{tag}-{DateTime.Now:yyyyMMdd}.log", Path.GetFileName(file));
        var text = File.ReadAllText(file);
        Assert.Contains("INF App: Orrery ", text);
        Assert.Contains("INF App: Portable mode, data in ", text);
        Assert.Matches("WRN Test: Something odd about host-[0-9a-f]{6}", text);
        Assert.DoesNotContain("server1", text);
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
    public void Names_and_addresses_are_tags_unless_shown_and_showing_them_is_written_down()
    {
        AppLog.Start(Location);
        try
        {
            var log = Log.For("Test");
            log.LogWarning("{Host}: login as {User} refused: {Reason}", "quokka01.mask-test.example", "wombatdeploy", "quokka01.mask-test.example said no to wombatdeploy");
            log.LogError(new IOException("Connection to 10.123.45.67:22 timed out"), "Couldn't reach \"{Name}\"", "Narwhal payroll");
            AppLog.ShowNames = true;
            log.LogWarning("{Host}: login as {User} refused", "quokka02.mask-test.example", "wombatdeploy");
        }
        finally
        {
            AppLog.ShowNames = false;
            AppLog.Stop();
        }

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(Location.LogDir)));
        var hidden = text[..text.IndexOf("Names and addresses are shown from here on", StringComparison.Ordinal)];
        Assert.Matches(@"WRN Test: (host-[0-9a-f]{6}): login as (user-[0-9a-f]{6}) refused: \1 said no to \2", hidden);
        Assert.Matches(@"ERR Test: Couldn't reach ""name-[0-9a-f]{6}""\r?\nSystem.IO.IOException: Connection to ip-[0-9a-f]{6}:22 timed out", hidden);
        foreach (var name in new[] { "quokka", "wombat", "10.123.45.67", "Narwhal" })
        {
            Assert.DoesNotContain(name, hidden);
        }

        Assert.Contains("WRN Test: quokka02.mask-test.example: login as wombatdeploy refused", text);
        Assert.Contains("Names and addresses are hidden again", text);
    }

    [Fact]
    public void Old_logs_go_by_age_then_by_size_and_the_newest_always_stays()
    {
        var folder = Path.Combine(_root, "prune");
        Directory.CreateDirectory(folder);
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        string Make(string name, double daysOld, int bytes)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, now.AddDays(-daysOld));
            return path;
        }

        var today = Make("orrery-20261104.log", 0, 400);
        var full = Make("orrery-20261103_001.log", 0.9, 400); // The day's second file, written after the first.
        var yesterday = Make("orrery-20261103.log", 1, 400);
        var lastWeek = Make("orrery-20261028.log", 7, 10);
        var tooOld = Make("orrery-20261001.log", 34, 10);
        var other = Make("ironrdp.log", 40, 10);

        AppLog.Prune(folder, TimeSpan.FromDays(14), maxTotal: 1000, now, current: null);

        Assert.True(File.Exists(today));
        Assert.True(File.Exists(full));
        Assert.False(File.Exists(yesterday)); // Past 1000 bytes in all.
        Assert.False(File.Exists(lastWeek));
        Assert.False(File.Exists(tooOld));
        Assert.True(File.Exists(other)); // Not Orrery's.

        AppLog.Prune(folder, TimeSpan.FromDays(1), maxTotal: 10, now, current: full);
        Assert.True(File.Exists(today));
        Assert.True(File.Exists(full));
    }

    [Fact]
    public void Logs_outside_the_data_folder_keep_the_plain_name()
    {
        var installed = new DataLocation(StorageMode.Installed, Path.Combine(_root, "data"), Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        Assert.Equal("orrery-.log", AppLog.FileNameFor(installed));
        Assert.Matches(@"^orrery-[0-9a-f]{6}-\.log$", AppLog.FileNameFor(Location));
    }

    [Theory]
    [InlineData("orrery-a1b2c3-20261105.log", true)]
    [InlineData("orrery-a1b2c3-20261105_001.log", true)]
    [InlineData("orrery-20261105.log", true)] // From before names were per computer.
    [InlineData("orrery-20261105_002.log", true)]
    [InlineData("orrery-123456-20261105.log", false)] // Another computer's tag, starting with digits.
    [InlineData("orrery-ffffff-20261105.log", false)]
    public void Only_this_computer_s_logs_and_older_unsplit_ones_count_as_its_own(string name, bool own) =>
        Assert.Equal(own, AppLog.IsOwn(name, "orrery-a1b2c3-"));

    [Fact]
    public void Pruning_leaves_other_computers_logs_in_a_synced_folder()
    {
        var folder = Path.Combine(_root, "shared");
        Directory.CreateDirectory(folder);
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        string Make(string name, double daysOld)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, now.AddDays(-daysOld));
            return path;
        }

        var mine = Make("orrery-a1b2c3-20261105.log", 0);
        var myOld = Make("orrery-a1b2c3-20261001.log", 35);
        var unsplitOld = Make("orrery-20261001.log", 35);
        var theirsOld = Make("orrery-123456-20261001.log", 35);

        AppLog.Prune(folder, TimeSpan.FromDays(14), maxTotal: 1000, now, current: null, ownPrefix: "orrery-a1b2c3-");

        Assert.True(File.Exists(mine));
        Assert.False(File.Exists(myOld));
        Assert.False(File.Exists(unsplitOld));
        Assert.True(File.Exists(theirsOld)); // That computer's Orrery tidies its own.
    }

    [Fact]
    public void Deleting_the_logs_takes_today_s_too_and_the_log_carries_on()
    {
        AppLog.Start(Location);
        try
        {
            Log.For("Test").LogWarning("Before");
            File.WriteAllText(Path.Combine(Location.LogDir, "orrery-20261001.log"), "old");
            var theirs = Path.Combine(Location.LogDir, "orrery-123456-20261001.log");
            File.WriteAllText(theirs, "another computer's");

            Assert.Equal(2, AppLog.DeleteLogs(Location));
            Log.For("Test").LogWarning("After");
        }
        finally
        {
            AppLog.Stop();
        }

        Assert.True(File.Exists(Path.Combine(Location.LogDir, "orrery-123456-20261001.log")));
        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(Location.LogDir), f => !f.Contains("123456")));
        Assert.DoesNotContain("Before", text);
        Assert.Contains("Deleted 2 earlier log file(s)", text);
        Assert.Contains("WRN Test: After", text);
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
        var showNames = Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), c => c.Content as string == "Show names and addresses in logs");
        Assert.False(showNames.IsChecked);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Content as string == "Delete logs");
        var keepFor = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => Avalonia.Automation.AutomationProperties.GetName(c) == "Keep logs for");
        Assert.Equal("14 days", (keepFor.SelectedItem as ComboBoxItem)?.Content);

        keepFor.SelectedItem = keepFor.Items.OfType<ComboBoxItem>().Single(i => i.Content as string == "7 days");
        Assert.Equal(7, settings.Load().LogDays);
        Assert.Equal(TimeSpan.FromDays(7), AppLog.KeepFor);
        window.Close();
        AppLog.KeepFor = Orrery.Core.Settings.AppSettings.DefaultKeepLogsFor;
    }

    [Fact]
    public void A_tag_is_looked_up_from_what_the_log_has_seen()
    {
        AppLog.Start(Location);
        try
        {
            Log.For("Test").LogInformation("Reached {Host}", "nas.home.example");
            var tag = Assert.Single(AppLog.Lookup("nas.home.example"), m => m.Kind == LogMaskKind.Host).Tag;

            Assert.Equal($"{tag} is the host nas.home.example", SettingsView.LookupText(tag));
            Assert.StartsWith("Not seen since Orrery started", SettingsView.LookupText("host-000000"));
            Assert.Contains($"{tag} (as a host)", SettingsView.LookupText("nas.home.example"));
            Assert.DoesNotContain("as a host", SettingsView.LookupText("localhost"));
        }
        finally
        {
            AppLog.Stop();
        }
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
            Assert.Matches($@"INF Session: \[{id}\] Opening ""name-[0-9a-f]{{6}}"" on host-[0-9a-f]{{6}}:22", text);
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
