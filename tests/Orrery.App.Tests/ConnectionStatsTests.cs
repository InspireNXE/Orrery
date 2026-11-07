using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>The Connection card on the "This session" tab, and the tab's mark for a slow link.</summary>
public class ConnectionStatsTests
{
    sealed class Desktop : IFrameBufferSession, ILinkStatsSession, IClipboardSession
    {
        public ConnectionTarget Target { get; } = new("desk.example", 3389, "user", null);
        public SessionState State { get; private set; } = SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<ClipboardTransfer>? ClipboardTransferChanged;
        public event EventHandler<string>? RemoteClipboardChanged { add { } remove { } }
        public void SendClipboard(string text) { }
        public void Transfer(ClipboardTransfer transfer) => ClipboardTransferChanged?.Invoke(this, transfer);

        public void Become(SessionState state)
        {
            State = state;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, null));
            Dispatcher.UIThread.RunJobs();
        }

        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
        public LinkStats Link { get; } = new();
        public int Width => 1920;
        public int Height => 1080;
        public void Update() => FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, 1, 1), resized: false));
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) { }
        public void SendKey(ushort scancode, bool pressed) { }
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Terminal : ITerminalSession, ILinkStatsSession
    {
        public ConnectionTarget Target { get; } = new("web01", 22, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived { add { } remove { } }
        public LinkStats Link { get; } = new();
        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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

    static async Task<(Window Window, SessionTab Tab)> Open(IConnectionSession session, Control view, Protocol protocol)
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = session.Target.Host, Settings = { Protocol = protocol } };
        tree.Add(entry, tree.Root);
        var tab = new SessionTab(session, view, new SessionOrigin(entry.Name, protocol, entry.Id, () => Task.CompletedTask));
        var panel = new PropertiesPanel(tree, new NoCredentials());
        var window = new Window { Content = panel, Width = 420, Height = 1200 };
        window.Show();
        await panel.ShowAsync(entry);
        panel.ShowSession(tab);
        Dispatcher.UIThread.RunJobs();
        return (window, tab);
    }

    static void ShowSessionTab(Window window)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "This session"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    static bool Shown(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    [AvaloniaFact]
    public async Task A_remote_desktops_card_shows_its_updates_round_trip_and_timings()
    {
        var desktop = new Desktop();
        var (window, tab) = await Open(desktop, new RemoteFrameView(desktop), Protocol.Rdp);
        desktop.Link.ConnectTime = TimeSpan.FromMilliseconds(320);
        desktop.Link.LastResize = TimeSpan.FromMilliseconds(45);
        desktop.Link.Received(512 * 1024);
        for (int i = 0; i < 28; i++)
        {
            desktop.Update();
        }

        tab.Link!.Sample();
        ShowSessionTab(window);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Shown(window, "Connection"));
        Assert.True(Shown(window, "Screen updates"));
        Assert.True(Shown(window, "Connecting took"));
        Assert.True(Shown(window, "320 ms"));
        Assert.True(Shown(window, "Last resize"));
        Assert.True(Shown(window, "45 ms"));
        Assert.True(Shown(window, "1920 × 1080"));
        Assert.True(Shown(window, "512 KB in, 0 bytes out"));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.EndsWith("/s") == true);
        // Scaling and the other view settings are still there above it.
        Assert.Contains(window.GetVisualDescendants().OfType<Border>(), c => c.IsEffectivelyVisible && c.Name == "Scaling");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_terminal_gets_the_session_tab_with_only_the_connection_card()
    {
        var terminal = new Terminal();
        var (window, tab) = await Open(terminal, new Border(), Protocol.Ssh);
        terminal.Link.Received(2048);
        tab.Link!.Sample();
        ShowSessionTab(window);

        Assert.True(Shown(window, "Connection"));
        Assert.True(Shown(window, "Data in"));
        Assert.False(Shown(window, "Screen updates"));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(), c => c.IsEffectivelyVisible && c.Name == "Scaling");
        Assert.False(Shown(window, "On next connect"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_long_round_trip_marks_the_tab_slow_and_the_card_says_why()
    {
        var desktop = new Desktop();
        var (window, tab) = await Open(desktop, new RemoteFrameView(desktop), Protocol.Rdp);
        tab.Link!.Add(new LinkSample(1000, 100, 10, 0.1, TimeSpan.FromMilliseconds(220)));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LinkHealth.Slow, tab.Link.Health);
        Assert.Equal("Connected, slow", tab.StateText);
        var glyph = tab.Header.GetLogicalDescendants().OfType<StateGlyph>().Single();
        Assert.True(glyph.Slow);
        Assert.Contains(tab.Link.Hints(), h => h.StartsWith("The round trip is 220 ms. That's the network"));

        tab.Link.Add(new LinkSample(1000, 100, 10, 0.1, TimeSpan.FromMilliseconds(20)));
        Assert.Equal(LinkHealth.Good, tab.Link.Health);
        Assert.False(glyph.Slow);
        Assert.Empty(tab.Link.Hints());
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_card_leads_with_a_verdict_and_shows_where_connecting_spent_its_time()
    {
        var desktop = new Desktop();
        var (window, tab) = await Open(desktop, new RemoteFrameView(desktop), Protocol.Rdp);
        desktop.Link.ConnectSteps =
        [
            new ConnectStep("Network", TimeSpan.FromMilliseconds(200)),
            new ConnectStep("Security", TimeSpan.FromMilliseconds(800)),
            new ConnectStep("Sign-in", TimeSpan.FromMilliseconds(10_400)),
            new ConnectStep("Session setup", TimeSpan.FromMilliseconds(600)),
            new ConnectStep("First screen", TimeSpan.FromMilliseconds(1000)),
        ];
        // A second the steps don't cover, as after a redirection.
        desktop.Link.ConnectTime = TimeSpan.FromMilliseconds(14_000);
        tab.Link!.Add(new LinkSample(5000, 4000, 31, 0, TimeSpan.FromMilliseconds(5)));
        tab.Link.Sample();
        ShowSessionTab(window);

        Assert.True(Shown(window, "Good"));
        Assert.True(Shown(window, "Connecting took 14 s"));
        Assert.True(Shown(window, "Sign-in 10 s"));
        Assert.True(Shown(window, "First screen 1.0 s"));
        Assert.True(Shown(window, "Other 1.0 s"));
        // With its steps, the connect time isn't a row of its own as well.
        Assert.False(Shown(window, "Connecting took"));
        Assert.True(Shown(window, "Recent"));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.EndsWith(" Connected") == true);
        Assert.True(Shown(window, "Drops"));
        Assert.True(Shown(window, "none"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_drop_is_counted_and_carried_to_the_tabs_next_connection()
    {
        var first = new Desktop();
        var (window, tab) = await Open(first, new RemoteFrameView(first), Protocol.Rdp);
        first.Link.ConnectTime = TimeSpan.FromMilliseconds(900);
        tab.Link!.Sample();
        first.Become(SessionState.Failed);
        Assert.Equal(1, tab.Link.Drops);
        Assert.Equal(new[] { LinkEventKind.Connected, LinkEventKind.Lost }, tab.Link.Events.Select(e => e.Kind));

        var second = new Desktop();
        var next = new LinkMonitor(second);
        next.CarryFrom(tab.Link);
        second.Link.ConnectTime = TimeSpan.FromMilliseconds(700);
        next.Sample();

        Assert.Equal(1, next.Drops);
        Assert.StartsWith("1, last ", next.DropsText());
        Assert.Equal("Reconnected", next.Events[^1].Text);
        Assert.Equal("700 ms", next.Events[^1].Value);
        Assert.Equal(3, next.Events.Count);

        // A session the server ended isn't a drop.
        second.Become(SessionState.Disconnected);
        Assert.Equal(1, next.Drops);
        Assert.Equal(LinkEventKind.Ended, next.Events[^1].Kind);
        window.Close();
    }

    [AvaloniaFact]
    public void Resizes_and_downloaded_files_are_noted()
    {
        var desktop = new Desktop();
        var monitor = new LinkMonitor(desktop);
        desktop.Link.LastResize = TimeSpan.FromMilliseconds(620);
        monitor.Sample();
        var resized = monitor.Events.Single(e => e.Kind == LinkEventKind.Resized);
        Assert.Equal("Resized to 1920 × 1080", resized.Text);
        Assert.Equal("620 ms", resized.Value);

        // A download that ends short (cancelled, or the remote gave up) isn't noted as copied.
        monitor.FollowTransfer(new ClipboardTransfer(2, 0, 4 << 20, Ended: false));
        monitor.FollowTransfer(new ClipboardTransfer(2, 1 << 20, 4 << 20, Ended: true));
        Assert.DoesNotContain(monitor.Events, e => e.Kind == LinkEventKind.Copied);

        monitor.FollowTransfer(new ClipboardTransfer(2, 0, 4 << 20, Ended: false));
        monitor.FollowTransfer(new ClipboardTransfer(2, 4 << 20, 4 << 20, Ended: true));
        var copied = monitor.Events.Single(e => e.Kind == LinkEventKind.Copied);
        Assert.Equal("Copied 2 files from the remote", copied.Text);
        Assert.StartsWith("4.0 MB", copied.Value);

        // The session's own event reaches the monitor too.
        desktop.Transfer(new ClipboardTransfer(1, 0, 10, Ended: false));
        desktop.Transfer(new ClipboardTransfer(1, 10, 10, Ended: true));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(monitor.Events, e => e.Text == "Copied 1 file from the remote");
    }

    [AvaloniaFact]
    public void Copy_details_has_the_build_the_groups_and_what_happened()
    {
        var desktop = new Desktop();
        var monitor = new LinkMonitor(desktop);
        desktop.Link.ConnectSteps = [new ConnectStep("Network", TimeSpan.FromMilliseconds(3)), new ConnectStep("Sign-in", TimeSpan.FromMilliseconds(400))];
        desktop.Link.ConnectTime = TimeSpan.FromMilliseconds(403);
        monitor.Sample();
        var card = new ConnectionCard(monitor, "RDP");

        var details = card.Details();
        Assert.Contains("RDP to desk.example:3389", details);
        Assert.Contains($"Orrery {ConnectionCard.Version()}", details);
        Assert.Contains("Good: ", details);
        Assert.Contains("Now:", details);
        Assert.Contains("  Desktop: 1920 × 1080", details);
        Assert.Contains("This session:", details);
        Assert.Contains("Connecting took 403 ms: Network 3 ms, Sign-in 400 ms", details);
        Assert.Contains("Connected (403 ms)", details);
        // The version is the build's, not the four-part assembly version.
        Assert.DoesNotMatch(@"^\d+\.\d+\.\d+\.\d+$", ConnectionCard.Version());
    }

    [Fact]
    public void Decoding_counts_as_slow_only_after_a_few_busy_seconds_running()
    {
        var busy = new LinkSample(0, 0, 30, 0.95, null);
        var idle = busy with { BusyShare = 0.2 };
        Assert.Equal(LinkHealth.Unknown, LinkMonitor.Judge([]));
        Assert.Equal(LinkHealth.Good, LinkMonitor.Judge([busy]));
        Assert.Equal(LinkHealth.Good, LinkMonitor.Judge([busy, busy, idle]));
        Assert.Equal(LinkHealth.Slow, LinkMonitor.Judge([idle, busy, busy, busy]));
    }

    [Fact]
    public void Rates_read_as_network_speeds_and_sizes_and_times_as_people_say_them()
    {
        Assert.Equal("4.2 Mbit/s", LinkMonitor.Rate(525_000));
        Assert.Equal("120 kbit/s", LinkMonitor.Rate(15_000));
        Assert.Equal("0 bit/s", LinkMonitor.Rate(0));
        Assert.Equal("2.1 GB", LinkMonitor.Size((long)(2.1 * (1L << 30))));
        Assert.Equal("64.0 MB", LinkMonitor.Size(64L << 20));
        Assert.Equal("18 ms", LinkMonitor.Milliseconds(TimeSpan.FromMilliseconds(18.2)));
        Assert.Equal("1 ms", LinkMonitor.Milliseconds(TimeSpan.FromMicroseconds(80)));
        Assert.Equal("1.9 s", LinkMonitor.Milliseconds(TimeSpan.FromMilliseconds(1900)));
        Assert.Equal("1 h 12 min", LinkMonitor.Duration(TimeSpan.FromMinutes(72)));
    }
}
