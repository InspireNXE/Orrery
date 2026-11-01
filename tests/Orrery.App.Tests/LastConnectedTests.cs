using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.App.Tests;

public class LastConnectedTests : IDisposable
{
    static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Local));

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(-30, "just now")] // slightly ahead of the clock
    [InlineData(59, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(5 * 60, "5 minutes ago")]
    [InlineData(59 * 60, "59 minutes ago")]
    [InlineData(60 * 60, "1 hour ago")]
    [InlineData(5 * 3600, "5 hours ago")]
    [InlineData(11 * 3600, "11 hours ago")]
    public void Recent_times_are_in_minutes_and_hours(int secondsAgo, string expected)
    {
        Assert.Equal(expected, RelativeTime.Describe(Now.AddSeconds(-secondsAgo), Now));
    }

    [Theory]
    [InlineData(1, "yesterday")]
    [InlineData(2, "2 days ago")]
    [InlineData(6, "6 days ago")]
    [InlineData(7, "1 week ago")]
    [InlineData(20, "2 weeks ago")]
    [InlineData(21, "3 weeks ago")]
    [InlineData(34, "4 weeks ago")]
    public void Older_times_are_in_calendar_days_and_weeks(int daysAgo, string expected)
    {
        // Noon, so a shift of an hour for daylight saving doesn't change the date.
        Assert.Equal(expected, RelativeTime.Describe(Now.AddDays(-daysAgo), Now));
    }

    [Fact]
    public void After_five_weeks_it_is_a_date()
    {
        var when = Now.AddDays(-60);

        Assert.Equal("on " + when.LocalDateTime.ToString("d MMM yyyy"), RelativeTime.Describe(when, Now));
    }

    [Fact]
    public void A_day_is_counted_by_the_calendar_once_it_is_more_than_24_hours_ago()
    {
        var justAfterMidnight = new DateTimeOffset(new DateTime(2026, 10, 31, 0, 10, 0, DateTimeKind.Local));
        var lateLastNight = new DateTimeOffset(new DateTime(2026, 10, 30, 23, 50, 0, DateTimeKind.Local));
        var lateTonight = new DateTimeOffset(new DateTime(2026, 10, 31, 23, 55, 0, DateTimeKind.Local));

        Assert.Equal("20 minutes ago", RelativeTime.Describe(lateLastNight, justAfterMidnight));
        Assert.Equal("yesterday", RelativeTime.Describe(lateLastNight, lateTonight));
    }

    [AvaloniaFact]
    public async Task A_connection_shows_when_it_last_connected_with_the_exact_time_in_a_tooltip()
    {
        var (tree, history) = Setup();
        var entry = Add(tree, "box");
        history.RecordConnected(entry.Id, Now.AddDays(-3));
        var (panel, window) = Show(tree, history);

        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var line = Line(window, "Last connected");
        Assert.Equal("Last connected 3 days ago", line.Text);
        Assert.Equal(RelativeTime.Exact(Now.AddDays(-3)), ToolTip.GetTip(line));
        Assert.Empty(Lines(window, "Last attempt failed"));
    }

    [AvaloniaFact]
    public async Task A_connection_that_never_connected_says_so()
    {
        var (tree, history) = Setup();
        var entry = Add(tree, "box");
        var (panel, window) = Show(tree, history);

        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(Lines(window, "Never connected"));
        Assert.Empty(Lines(window, "Last attempt failed"));
    }

    [AvaloniaFact]
    public async Task A_failed_attempt_is_shown_only_when_it_is_newer_than_the_last_success()
    {
        var (tree, history) = Setup();
        var entry = Add(tree, "box");
        history.RecordConnected(entry.Id, Now.AddDays(-1));
        history.RecordFailed(entry.Id, Now.AddHours(-2));
        var (panel, window) = Show(tree, history);

        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Last connected yesterday", Line(window, "Last connected").Text);
        var failed = Line(window, "Last attempt failed");
        Assert.Equal("Last attempt failed 2 hours ago", failed.Text);
        Assert.Equal(RelativeTime.Exact(Now.AddHours(-2)), ToolTip.GetTip(failed));

        history.RecordConnected(entry.Id, Now.AddMinutes(-1));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(Lines(window, "Last attempt failed"));
        Assert.Equal("Last connected 1 minute ago", Line(window, "Last connected").Text);
    }

    [AvaloniaFact]
    public async Task A_folder_shows_the_most_recent_of_its_connections()
    {
        var (tree, history) = Setup();
        var folder = new ConnectionFolder { Name = "Servers" };
        tree.Add(folder, tree.Root);
        var older = new ConnectionEntry { Name = "older", Host = "o" };
        var newer = new ConnectionEntry { Name = "newer", Host = "n" };
        tree.Add(older, folder);
        tree.Add(newer, folder);
        history.RecordConnected(older.Id, Now.AddDays(-10));
        history.RecordConnected(newer.Id, Now.AddHours(-5));
        var (panel, window) = Show(tree, history);

        await panel.ShowAsync(folder);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Last connected 5 hours ago", Line(window, "Last connected").Text);
    }

    [AvaloniaFact]
    public async Task The_wording_keeps_up_while_the_pane_is_open()
    {
        var (tree, history) = Setup();
        var entry = Add(tree, "box");
        history.RecordConnected(entry.Id, Now);
        var clock = Now;
        var (panel, window) = Show(tree, history, () => clock);

        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Last connected just now", Line(window, "Last connected").Text);

        clock = Now.AddMinutes(7);
        panel.ShowHistory(); // what the timer does every 30 seconds

        Assert.Equal("Last connected 7 minutes ago", Line(window, "Last connected").Text);
    }

    [AvaloniaFact]
    public async Task A_new_connection_updates_the_open_pane()
    {
        var (tree, history) = Setup();
        var entry = Add(tree, "box");
        var (panel, window) = Show(tree, history);
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Lines(window, "Never connected"));

        history.RecordConnected(entry.Id, Now);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Last connected just now", Line(window, "Last connected").Text);
    }

    [AvaloniaFact]
    public async Task Renaming_a_connection_keeps_what_the_pane_shows()
    {
        var (tree, history) = Setup();
        var entry = Add(tree, "box");
        history.RecordConnected(entry.Id, Now.AddHours(-1));
        var (panel, window) = Show(tree, history);
        await panel.ShowAsync(entry);

        entry.Name = "renamed";
        tree.NotifyChanged();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Last connected 1 hour ago", Line(window, "Last connected").Text);
    }

    [AvaloniaFact]
    public async Task With_history_turned_off_the_pane_says_nothing_about_connecting_and_back_on_it_starts_afresh()
    {
        var (tree, history) = Setup();
        var entry = Add(tree, "box");
        history.RecordConnected(entry.Id, Now.AddHours(-2));
        history.RecordFailed(entry.Id, Now.AddHours(-1));
        var (panel, window) = Show(tree, history);
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Lines(window, "Last attempt failed"));

        history.Enabled = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(Lines(window, "Last connected"));
        Assert.Empty(Lines(window, "Never connected"));
        Assert.Empty(Lines(window, "Last attempt failed"));

        history.Enabled = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Lines(window, "Never connected"));
        Assert.Empty(Lines(window, "Last attempt failed"));
    }

    (ConnectionTree, ConnectionHistory) Setup() =>
        (ConnectionTree.Empty(), new ConnectionHistory(Path.Combine(_dir, ConnectionHistory.FileName)));

    static ConnectionEntry Add(ConnectionTree tree, string name)
    {
        var entry = new ConnectionEntry { Name = name, Host = name + ".example" };
        tree.Add(entry, tree.Root);
        return entry;
    }

    static (PropertiesPanel, Window) Show(ConnectionTree tree, ConnectionHistory history, Func<DateTimeOffset>? now = null)
    {
        var panel = new PropertiesPanel(tree, new NoCredentials(), history: history, now: now ?? (() => Now));
        var window = new Window { Content = panel, Width = 400, Height = 800 };
        window.Show();
        return (panel, window);
    }

    static List<TextBlock> Lines(Window window, string startsWith) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Text?.StartsWith(startsWith, StringComparison.Ordinal) == true).ToList();

    static TextBlock Line(Window window, string startsWith) => Assert.Single(Lines(window, startsWith));

    sealed class NoCredentials : ICredentialStore
    {
        public bool IsLocked => false;
        public bool Exists => true;
        public Task UnlockAsync(string masterPassword) => Task.CompletedTask;
        public void Lock() { }
        public Task<IReadOnlyList<CredentialSummary>> ListAsync() => Task.FromResult<IReadOnlyList<CredentialSummary>>([]);
        public Task<Credential?> GetAsync(Guid id) => Task.FromResult<Credential?>(null);
        public Task SaveAsync(Credential credential) => Task.CompletedTask;
        public Task SaveAllAsync(IReadOnlyList<Credential> credentials) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
