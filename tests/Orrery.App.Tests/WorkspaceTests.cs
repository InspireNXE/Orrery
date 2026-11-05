using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Restore;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class WindowPlacementFitterTests
{
    static readonly WindowPlacementFitter.ScreenArea Main = new(new PixelRect(0, 0, 1920, 1040), 1);
    static readonly WindowPlacementFitter.ScreenArea Right = new(new PixelRect(1920, 0, 2560, 1400), 1);
    static readonly Size Minimum = new(1000, 600);

    [Fact]
    public void A_window_on_a_screen_stays_where_it_was()
    {
        var fit = WindowPlacementFitter.Fit(new WindowPlacement(100, 80, 1440, 900, false), [Main], Minimum);

        Assert.Equal(new PixelPoint(100, 80), fit.Position);
        Assert.Equal(new Size(1440, 900), fit.Size);
    }

    [Fact]
    public void A_window_on_a_second_screen_stays_there_while_it_exists()
    {
        var saved = new WindowPlacement(2100, 100, 1440, 900, false);

        Assert.Equal(new PixelPoint(2100, 100), WindowPlacementFitter.Fit(saved, [Main, Right], Minimum).Position);
    }

    [Fact]
    public void A_window_whose_screen_is_gone_is_centered_on_the_main_one()
    {
        var fit = WindowPlacementFitter.Fit(new WindowPlacement(2100, 100, 1440, 900, true), [Main], Minimum);

        Assert.Null(fit.Position);
        Assert.Equal(new Size(1440, 900), fit.Size);
    }

    [Theory]
    [InlineData(-30000, -30000)]
    [InlineData(5000, 100)]
    [InlineData(100, 9000)]
    [InlineData(-1400, 100)] // all but 40 pixels off the left edge
    public void A_window_that_would_be_out_of_reach_is_centered(int x, int y)
    {
        Assert.Null(WindowPlacementFitter.Fit(new WindowPlacement(x, y, 1440, 900, false), [Main], Minimum).Position);
    }

    [Fact]
    public void A_window_hanging_over_an_edge_is_pulled_back_inside()
    {
        var fit = WindowPlacementFitter.Fit(new WindowPlacement(900, 400, 1440, 900, false), [Main], Minimum);

        Assert.Equal(new PixelPoint(480, 140), fit.Position);
    }

    [Fact]
    public void A_window_larger_than_a_smaller_screen_is_shrunk_to_it()
    {
        var small = new WindowPlacementFitter.ScreenArea(new PixelRect(0, 0, 1280, 720), 1);

        var fit = WindowPlacementFitter.Fit(new WindowPlacement(0, 0, 2400, 1300, false), [small], new Size(1000, 600));

        Assert.Equal(new Size(1280, 720), fit.Size);
        Assert.Equal(new PixelPoint(0, 0), fit.Position);
    }

    [Fact]
    public void Size_is_in_device_independent_units_so_a_scaled_screen_fits_fewer_of_them()
    {
        var scaled = new WindowPlacementFitter.ScreenArea(new PixelRect(0, 0, 2560, 1400), 2);

        var fit = WindowPlacementFitter.Fit(new WindowPlacement(0, 0, 3000, 900, false), [scaled], new Size(1000, 600));

        Assert.Equal(new Size(1280, 700), fit.Size);
    }

    [Fact]
    public void With_no_screens_to_judge_by_the_size_is_kept_and_the_window_is_centered()
    {
        var fit = WindowPlacementFitter.Fit(new WindowPlacement(10, 10, 1200, 800, false), [], Minimum);

        Assert.Null(fit.Position);
        Assert.Equal(new Size(1200, 800), fit.Size);
    }
}

public class WorkspaceRestorerTests
{
    static WorkspaceTab Tab(string host) => new(null, host, 22, Protocol.Ssh);

    [Fact]
    public async Task Tabs_open_in_order_with_a_pause_between_them_and_none_before_the_first()
    {
        var events = new List<string>();
        var opened = await WorkspaceRestorer.OpenAllAsync<string>([Tab("a"), Tab("b"), Tab("c")],
            tab => { events.Add("open " + tab.Host); return Task.FromResult<string?>(tab.Host); },
            TimeSpan.FromMilliseconds(400),
            (span, _) => { events.Add($"wait {span.TotalMilliseconds}"); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(["open a", "wait 400", "open b", "wait 400", "open c"], events);
        Assert.Equal<IEnumerable<string?>>(["a", "b", "c"], opened);
    }

    [Fact]
    public async Task One_that_fails_or_is_declined_does_not_stop_the_rest()
    {
        var opened = await WorkspaceRestorer.OpenAllAsync<string>([Tab("a"), Tab("broken"), Tab("declined"), Tab("d")],
            tab => tab.Host switch
            {
                "broken" => throw new InvalidOperationException("boom"),
                "declined" => Task.FromResult<string?>(null),
                _ => Task.FromResult<string?>(tab.Host),
            },
            TimeSpan.Zero, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal<IEnumerable<string?>>(["a", null, null, "d"], opened);
    }

    [Fact]
    public async Task Canceling_stops_opening_more()
    {
        using var cancel = new CancellationTokenSource();
        var host = new List<string>();
        var opened = await WorkspaceRestorer.OpenAllAsync<string>([Tab("a"), Tab("b"), Tab("c")],
            tab => { host.Add(tab.Host!); return Task.FromResult<string?>(tab.Host); },
            TimeSpan.FromSeconds(30),
            (_, token) => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            cancel.Token);

        Assert.Equal(["a"], host);
        Assert.Equal<IEnumerable<string?>>(["a", null, null], opened);
    }

    [Fact]
    public void The_default_pause_is_short_but_not_zero()
    {
        Assert.InRange(WorkspaceRestorer.Stagger, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
    }
}

public class WorkspaceWindowTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly DataLocation _location;
    readonly ConnectionEntry _saved = new() { Name = "saved box", Host = "saved.example", Settings = { Protocol = Protocol.Ssh } };
    readonly ConnectionEntry _other = new() { Name = "other box", Host = "other.example", Settings = { Protocol = Protocol.Ssh } };

    sealed class IdleSession(string host, int port = 22) : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new(host, port, "someone", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class ConnectedTerminal(string host) : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new(host, 22, "someone", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived { add { } remove { } }
        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class StateSession(string host, SessionState state) : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new(host, 22, "someone", null);
        public SessionState State => state;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// A terminal that's connecting until the test says it's connected, keeping what's typed into it and answering
    /// what's typed with <see cref="Reply"/>, as a server would.
    /// </summary>
    sealed class ConnectingTerminal(string host) : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new(host, 22, "someone", null);
        public SessionState State { get; private set; } = SessionState.Connecting;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event Action<byte[]>? DataReceived;
        public readonly System.Text.StringBuilder Sent = new();
        public Func<string, string?> Reply { get; set; } = _ => null;
        public void Output(string text) => DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));

        public void Send(ReadOnlySpan<byte> data)
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
            Sent.Append(text);
            if (Reply(text) is { } reply)
            {
                Output(reply);
            }
        }

        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Connect()
        {
            State = SessionState.Connected;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(State, null));
        }
    }

    public WorkspaceWindowTests()
    {
        _location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(_location.DataDir);

        // A non-empty tree, so start-up doesn't go looking for an mRemoteNG file to offer.
        var tree = ConnectionTree.Empty();
        tree.Add(_saved, tree.Root);
        tree.Add(_other, tree.Root);
        ConnectionStore.In(_location).Save(tree);
    }

    string WorkspaceFile => ComputerFiles.PathFor(_location, WorkspaceStore.FileName);

    MainWindow NewWindow()
    {
        VaultAutoLockTests.AddMainWindowResources();
        return new MainWindow(_location);
    }

    static void AddTab(MainWindow window, IConnectionSession session, SessionOrigin origin) =>
        typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Border(), session, origin]);

    static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Assert.True(condition(), "timed out waiting");
    }

    [AvaloniaFact]
    public void A_failed_session_marks_its_row_failed_and_a_connected_tab_of_the_same_connection_wins()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            var origin = new SessionOrigin(_saved.Name, Protocol.Ssh, _saved.Id, () => Task.CompletedTask);
            var row = VaultAutoLockTests.Private<TreeView>(window, "_treeView").ItemsSource!.OfType<NodeViewModel>().Single(r => r.Node.Id == _saved.Id);

            AddTab(window, new StateSession(_saved.Host, SessionState.Failed), origin);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SessionState.Failed, row.Status);

            AddTab(window, new StateSession(_saved.Host, SessionState.Connected), origin);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(SessionState.Connected, row.Status);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Closing_remembers_the_tabs_in_order_the_active_one_and_no_login()
    {
        var window = NewWindow();
        window.Show();
        AddTab(window, new IdleSession(_saved.Host), new SessionOrigin(_saved.Name, Protocol.Ssh, _saved.Id, () => Task.CompletedTask));
        AddTab(window, new IdleSession("typed.example", 2222), new SessionOrigin("typed.example", Protocol.Vnc, null, () => Task.CompletedTask));
        AddTab(window, new IdleSession(_saved.Host), new SessionOrigin($"{_saved.Name} · files", Protocol.Ssh, _saved.Id, () => Task.CompletedTask) { Files = true });
        VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs")[1].Header.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        window.Close();

        var workspace = new WorkspaceStore(WorkspaceFile).Load();
        Assert.Equal(
            [new WorkspaceTab(_saved.Id, null, 0, Protocol.Ssh), new WorkspaceTab(null, "typed.example", 2222, Protocol.Vnc), new WorkspaceTab(_saved.Id, null, 0, Protocol.Ssh, true)],
            workspace.Tabs);
        Assert.Equal(1, workspace.Active);
        Assert.DoesNotContain("someone", File.ReadAllText(WorkspaceFile));
    }

    [AvaloniaFact]
    public void A_local_terminal_is_remembered_by_its_shell_not_as_a_connection_to_this_computer()
    {
        var window = NewWindow();
        window.Show();
        AddTab(window, new IdleSession(Environment.MachineName), new SessionOrigin("Ubuntu", Protocol.Ssh, null, () => Task.CompletedTask)
        {
            Local = true,
            LocalShell = "wsl:Ubuntu",
        });
        Dispatcher.UIThread.RunJobs();

        window.Close();

        var workspace = new WorkspaceStore(WorkspaceFile).Load();
        Assert.Equal([new WorkspaceTab(null, null, 0, Protocol.Ssh, LocalShell: "wsl:Ubuntu")], workspace.Tabs);
    }

    [AvaloniaFact]
    public void The_wheel_scrolls_tabs_that_do_not_fit()
    {
        var window = NewWindow();
        window.Width = 1000;
        window.Show();
        for (int i = 0; i < 20; i++)
        {
            AddTab(window, new IdleSession($"host-{i}"), new SessionOrigin($"a-long-host-name-{i}", Protocol.Ssh, null, () => Task.CompletedTask));
        }

        Dispatcher.UIThread.RunJobs();
        var scroller = window.FindControl<ScrollViewer>("TabScroller")!;
        scroller.Offset = default;
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
        var over = scroller.TranslatePoint(new Point(scroller.Bounds.Width / 2, scroller.Bounds.Height / 2), window)!.Value;

        // Down goes right, up comes back, and it stops at the start.
        window.MouseWheel(over, new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MainWindow.TabWheelStep, scroller.Offset.X);
        window.MouseWheel(over, new Vector(0, 2));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, scroller.Offset.X);
        window.Close();
    }

    [Theory]
    [InlineData(new[] { 100.0, 100.0 }, 300.0, double.PositiveInfinity)]
    [InlineData(new[] { 100.0, 100.0, 100.0 }, 240.0, 80.0)]
    [InlineData(new[] { 40.0, 200.0, 200.0 }, 240.0, 100.0)]
    public void Tabs_that_do_not_fit_share_what_is_left_after_the_narrow_ones(double[] widths, double room, double share) =>
        Assert.Equal(share, TabStripPanel.Share(widths, room));

    [AvaloniaFact]
    public void Tabs_narrow_to_fit_then_show_only_their_state_and_the_strip_scrolls_last()
    {
        var window = NewWindow();
        window.Width = 1200;
        window.Show();
        var scroller = window.FindControl<ScrollViewer>("TabScroller")!;
        var strip = window.FindControl<TabStripPanel>("TabStrip")!;
        var headers = new List<SessionTabHeader>();
        void Add(int count)
        {
            for (int i = 0; i < count; i++)
            {
                AddTab(window, new IdleSession($"h{headers.Count}"), new SessionOrigin($"a-rather-long-host-name-{headers.Count}", Protocol.Ssh, null, () => Task.CompletedTask));
                headers.Add(strip.Children.OfType<SessionTabHeader>().Last());
            }

            Dispatcher.UIThread.RunJobs();
        }

        Add(2);
        Assert.All(headers, h => Assert.False(h.Compact));
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1);

        // More than fit at full width: narrowed, still with their names, and nothing to scroll.
        Add(4);
        Assert.True(headers[0].Bounds.Width < headers[0].FullWidth);
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1);

        // Many: just their state, at their narrowest, and then the strip scrolls.
        Add(30);
        Assert.All(headers, h => Assert.True(h.Compact));
        Assert.All(headers, h => Assert.False(h.Tight));
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width);

        // Closing most of them gives the names back.
        foreach (var header in headers.Skip(2).ToList())
        {
            strip.Children.Remove(header);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.All(headers.Take(2), h => Assert.False(h.Compact));
        Assert.All(headers.Take(2), h => Assert.False(h.Tight));
        window.Close();
    }

    [AvaloniaFact]
    public void The_tab_list_is_offered_once_there_are_two_tabs()
    {
        var window = NewWindow();
        window.Show();
        var list = window.FindControl<Button>("TabListButton")!;
        AddTab(window, new IdleSession("one"), new SessionOrigin("one", Protocol.Ssh, null, () => Task.CompletedTask));
        Dispatcher.UIThread.RunJobs();
        Assert.False(list.IsVisible);

        AddTab(window, new IdleSession("two"), new SessionOrigin("two", Protocol.Ssh, null, () => Task.CompletedTask));
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void An_administrator_shell_is_not_reopened_at_the_next_start()
    {
        var window = NewWindow();
        window.Show();
        AddTab(window, new IdleSession(Environment.MachineName), new SessionOrigin("PowerShell 7 (Administrator)", Protocol.Ssh, null, () => Task.CompletedTask)
        {
            Local = true,
            LocalShell = "pwsh",
            Elevated = true,
        });
        AddTab(window, new IdleSession(Environment.MachineName), new SessionOrigin("PowerShell 7", Protocol.Ssh, null, () => Task.CompletedTask)
        {
            Local = true,
            LocalShell = "pwsh",
        });
        Dispatcher.UIThread.RunJobs();

        window.Close();

        Assert.Equal([new WorkspaceTab(null, null, 0, Protocol.Ssh, LocalShell: "pwsh")], new WorkspaceStore(WorkspaceFile).Load().Tabs);
    }

    [AvaloniaFact]
    public void Closing_remembers_the_window_and_whether_it_was_maximized()
    {
        var window = NewWindow();
        window.Show();
        window.Position = new PixelPoint(120, 90);
        window.Width = 1111;
        window.Height = 777;
        Dispatcher.UIThread.RunJobs();

        window.Close();

        var saved = new WorkspaceStore(WorkspaceFile).Load().Window;
        Assert.NotNull(saved);
        Assert.False(saved.Maximized);
        Assert.Equal(1111, saved.Width, 1);
        Assert.Equal(777, saved.Height, 1);
        Assert.Equal((120, 90), (saved.X, saved.Y));
    }

    [AvaloniaFact]
    public void A_maximized_window_is_remembered_with_its_normal_size_and_comes_back_maximized()
    {
        new WorkspaceStore(WorkspaceFile).Save(new Workspace { Window = new WindowPlacement(60, 50, 1200, 800, Maximized: true) });
        var window = NewWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WindowState.Maximized, window.WindowState);
        window.Close();

        var saved = new WorkspaceStore(WorkspaceFile).Load().Window;
        Assert.True(saved!.Maximized);
        Assert.Equal((60, 50, 1200d, 800d), (saved.X, saved.Y, saved.Width, saved.Height));
    }

    [AvaloniaFact]
    public void The_window_opens_where_it_was()
    {
        new WorkspaceStore(WorkspaceFile).Save(new Workspace { Window = new WindowPlacement(60, 50, 1200, 800, Maximized: false) });
        var window = NewWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new PixelPoint(60, 50), window.Position);
            Assert.Equal(1200, window.Width);
            Assert.Equal(800, window.Height);
            Assert.Equal(WindowState.Normal, window.WindowState);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_window_remembered_off_every_screen_opens_on_one()
    {
        new WorkspaceStore(WorkspaceFile).Save(new Workspace { Window = new WindowPlacement(-40000, -40000, 1200, 800, Maximized: false) });
        var window = NewWindow();
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var screen = Assert.IsAssignableFrom<Avalonia.Platform.Screen>(window.Screens.ScreenFromWindow(window));
            Assert.True(screen.WorkingArea.Contains(window.Position), $"window at {window.Position} should be on a screen");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ask_shows_a_bar_with_the_count_and_Not_now_dismisses_it_without_opening_anything()
    {
        new WorkspaceStore(WorkspaceFile).Save(new Workspace
        {
            Tabs = [new WorkspaceTab(_saved.Id, null, 0, Protocol.Ssh), new WorkspaceTab(Guid.NewGuid(), null, 0, Protocol.Ssh), new WorkspaceTab(null, "typed.example", 22, Protocol.Ssh)],
        });
        var window = NewWindow();
        try
        {
            window.Show();
            await window.OfferWorkspaceAsync();
            Dispatcher.UIThread.RunJobs();

            var bar = window.FindControl<Border>("RestoreBar")!;
            Assert.True(bar.IsVisible);
            Assert.Equal("2 sessions were open last time", window.FindControl<TextBlock>("RestoreText")!.Text);
            Assert.Equal("Reopen", window.FindControl<Button>("RestoreReopenButton")!.Content);
            Assert.Equal("Not now", window.FindControl<Button>("RestoreDismissButton")!.Content);

            window.FindControl<Button>("RestoreDismissButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.False(bar.IsVisible);
            Assert.Empty(VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Closing_without_answering_the_bar_keeps_the_offer_for_next_time()
    {
        var tabs = new List<WorkspaceTab> { new(_saved.Id, null, 0, Protocol.Ssh), new(null, "typed.example", 22, Protocol.Ssh) };
        new WorkspaceStore(WorkspaceFile).Save(new Workspace { Tabs = tabs, Active = 1 });
        var window = NewWindow();
        window.Show();
        await window.OfferWorkspaceAsync();

        window.Close();

        var workspace = new WorkspaceStore(WorkspaceFile).Load();
        Assert.Equal(tabs, workspace.Tabs);
        Assert.Equal(1, workspace.Active);
    }


    [AvaloniaFact]
    public async Task A_saved_connection_sends_its_when_connected_commands_once_connected_folders_first()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var saved = (ConnectionEntry)tree.Find(_saved.Id)!;
            tree.Root.Commands = [new SavedCommand { Name = "Root first", Text = "echo root", WhenConnected = true }];
            saved.Commands =
            [
                new SavedCommand { Name = "By hand", Text = "uptime" },
                new SavedCommand { Name = "Into app", Text = "cd /srv/app", WhenConnected = true },
                new SavedCommand { Name = "Typed only", Text = "tmux attach", WhenConnected = true, PressEnter = false },
            ];

            var session = new ConnectingTerminal(_saved.Host);
            var origin = new SessionOrigin(_saved.Name, Protocol.Ssh, _saved.Id, () => Task.CompletedTask);
            typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Sessions.TerminalView(session), session, origin]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(string.Empty, session.Sent.ToString());

            session.Connect();
            session.Output("someone@saved:~$ ");
            await Until(() => session.Sent.ToString().EndsWith("tmux attach"));
            Assert.Equal("echo root\rcd /srv/app\rtmux attach", session.Sent.ToString());

            // Once per tab: a later change of state doesn't send them again.
            session.Connect();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("echo root\rcd /srv/app\rtmux attach", session.Sent.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Opens <see cref="_saved"/> in a tab with <paramref name="commands"/> sent when connected, its login a vault
    /// credential whose password is "hunter2", and connects it at a shell prompt.
    /// </summary>
    Task<ConnectingTerminal> ConnectAsync(MainWindow window, Func<string, string?> reply, params SavedCommand[] commands) =>
        ConnectAsync(window, reply, "someone@saved:~$ ", commands);

    async Task<ConnectingTerminal> ConnectAsync(MainWindow window, Func<string, string?> reply, string firstScreen, params SavedCommand[] commands)
    {
        var vault = Assert.IsType<VaultFileStore>(VaultAutoLockTests.Private<ICredentialStore>(window, "_credentials"));
        await vault.UnlockAsync("correct horse battery staple");
        var login = Guid.NewGuid();
        await vault.SaveAsync(new Credential(login, "someone's login", CredentialKind.Password, "someone", null, "hunter2"));

        var saved = (ConnectionEntry)VaultAutoLockTests.Private<ConnectionTree>(window, "_tree").Find(_saved.Id)!;
        saved.Settings.CredentialId = login;
        saved.Commands = [.. commands];

        var session = new ConnectingTerminal(_saved.Host) { Reply = reply };
        var origin = new SessionOrigin(_saved.Name, Protocol.Ssh, _saved.Id, () => Task.CompletedTask);
        typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Sessions.TerminalView(session), session, origin]);
        session.Connect();
        session.Output(firstScreen);
        return session;
    }

    /// <summary>Runs the UI for a while, for checking nothing more is typed.</summary>
    static async Task IdleAsync(int milliseconds = 1500)
    {
        for (int i = 0; i < milliseconds / 25; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
    }

    [AvaloniaFact]
    public async Task The_login_password_isnt_typed_at_a_prompt_for_someone_elses()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            var session = await ConnectAsync(window, typed => typed == "sudo -i\r" ? "\r\n[sudo] password for root: " : null, BecomeRoot, IntoRoot);

            await Until(() => session.Sent.Length > 0);
            await IdleAsync();
            Assert.Equal("sudo -i\r", session.Sent.ToString());
            Assert.Contains("the prompt asks for root's password, not someone's", VaultAutoLockTests.Private<SessionTab>(window, "_currentTab").Warning.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_password_that_shows_on_screen_is_reported_and_stops_the_rest()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            // A program that prints "Password:" and echoes the line: not a real password prompt.
            var session = await ConnectAsync(window, typed => typed switch
            {
                "sudo -i\r" => "\r\nPassword: ",
                "hunter2\r" => "hunter2\r\nsomeone@saved:~$ ",
                _ => null,
            }, BecomeRoot, IntoRoot);

            await Until(() => session.Sent.ToString().Contains("hunter2"));
            await IdleAsync();
            Assert.Equal("sudo -i\rhunter2\r", session.Sent.ToString());

            // Said over the session until dismissed, as an error, not only in the status line for a few seconds.
            var warning = VaultAutoLockTests.Private<SessionTab>(window, "_currentTab").Warning;
            Assert.StartsWith("Security: the password from \"someone's login\" showed on screen", warning.Text);
            Assert.Equal(Orrery.App.Controls.NoticeKind.Error, warning.Kind);
            await IdleAsync(500);
            Assert.NotNull(warning.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_command_that_types_a_password_waits_for_a_shell_prompt_on_connect()
    {
        var wait = MainWindow.ShellWait;
        MainWindow.ShellWait = TimeSpan.FromSeconds(1);
        var window = NewWindow();
        try
        {
            window.Show();
            var session = await ConnectAsync(window, _ => null, "\r\nlogin: ", BecomeRoot);

            await IdleAsync(2500);
            Assert.Equal(string.Empty, session.Sent.ToString());
        }
        finally
        {
            MainWindow.ShellWait = wait;
            window.Close();
        }
    }

    static SavedCommand BecomeRoot => new() { Name = "Become root", Text = "sudo -i", WhenConnected = true, Password = CommandPassword.Login };
    static SavedCommand IntoRoot => new() { Name = "Home", Text = "cd /root", WhenConnected = true };

    [AvaloniaFact]
    public async Task Become_root_types_the_login_password_at_sudos_prompt_then_carries_on()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            var session = await ConnectAsync(window, typed => typed switch
            {
                "sudo -i\r" => "\r\n[sudo] password for someone: ",
                "hunter2\r" => "\r\nroot@saved:~# ",
                _ => null,
            }, BecomeRoot, IntoRoot);

            await Until(() => session.Sent.ToString().EndsWith("cd /root\r"));
            Assert.Equal("sudo -i\rhunter2\rcd /root\r", session.Sent.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task No_password_is_typed_when_sudo_doesnt_ask_for_one()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            var session = await ConnectAsync(window, typed => typed == "sudo -i\r" ? "\r\nroot@saved:~# " : null, BecomeRoot, IntoRoot);

            await Until(() => session.Sent.ToString().EndsWith("cd /root\r"));
            Assert.Equal("sudo -i\rcd /root\r", session.Sent.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task No_prompt_in_time_types_nothing_and_stops_the_rest()
    {
        var wait = MainWindow.PasswordWait;
        MainWindow.PasswordWait = TimeSpan.FromSeconds(1);
        var window = NewWindow();
        try
        {
            window.Show();
            var session = await ConnectAsync(window, typed => typed == "sudo -i\r" ? "\r\nWe trust you have received the usual lecture" : null, BecomeRoot, IntoRoot);

            await Until(() => session.Sent.Length > 0);
            for (int i = 0; i < 80; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }

            Assert.Equal("sudo -i\r", session.Sent.ToString());
        }
        finally
        {
            MainWindow.PasswordWait = wait;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_quick_connect_gets_no_password_even_sent_by_hand()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var vault = Assert.IsType<VaultFileStore>(VaultAutoLockTests.Private<ICredentialStore>(window, "_credentials"));
            await vault.UnlockAsync("correct horse battery staple");
            var root = Guid.NewGuid();
            await vault.SaveAsync(new Credential(root, "root", CredentialKind.Password, "root", null, "hunter2"));

            // The defaults' commands reach typed-in addresses, by hand: those never get a password.
            var su = new SavedCommand { Name = "su", Text = "su -", Password = CommandPassword.Credential, PasswordCredentialId = root };
            tree.Root.Commands = [su];
            var session = new ConnectingTerminal("typed.example") { Reply = typed => typed == "su -\r" ? "\r\nPassword: " : null };
            typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                [new Sessions.TerminalView(session), session, new SessionOrigin("typed.example", Protocol.Ssh, null, () => Task.CompletedTask)]);
            session.Connect();
            Dispatcher.UIThread.RunJobs();

            var tab = VaultAutoLockTests.Private<SessionTab>(window, "_currentTab");
            Assert.True(await window.SendSavedCommandAsync(tab, new ResolvedCommand(su, tree.Root, false), pressEnter: true, focus: false));
            Assert.Equal("su -\r", session.Sent.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_chosen_credentials_password_isnt_typed_from_the_defaults()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            var su = new SavedCommand { Name = "su", Text = "su -", WhenConnected = true, Password = CommandPassword.Credential, PasswordCredentialId = Guid.NewGuid() };
            var session = await ConnectAsync(window, typed => typed == "su -\r" ? "\r\nPassword: " : null);
            VaultAutoLockTests.Private<ConnectionTree>(window, "_tree").Root.Commands = [su];

            var tab = VaultAutoLockTests.Private<SessionTab>(window, "_currentTab");
            Assert.True(await window.SendSavedCommandAsync(tab, new ResolvedCommand(su, VaultAutoLockTests.Private<ConnectionTree>(window, "_tree").Root, false), pressEnter: true, focus: false));
            Assert.Equal("su -\r", session.Sent.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_refused_password_isnt_typed_again()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            var session = await ConnectAsync(window, typed => typed switch
            {
                "sudo -i\r" => "\r\n[sudo] password for someone: ",
                "hunter2\r" => "\r\nSorry, try again.\r\n[sudo] password for someone: ",
                _ => null,
            }, BecomeRoot, IntoRoot);

            await Until(() => session.Sent.ToString().Contains("hunter2"));
            for (int i = 0; i < 60; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }

            Assert.Equal("sudo -i\rhunter2\r", session.Sent.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_quick_connect_terminal_sends_nothing_by_itself()
    {
        var window = NewWindow();
        try
        {
            window.Show();
            VaultAutoLockTests.Private<ConnectionTree>(window, "_tree").Root.Commands = [new SavedCommand { Name = "Root", Text = "sudo -i", WhenConnected = true }];
            var session = new ConnectingTerminal("typed.example");
            var origin = new SessionOrigin("typed.example", Protocol.Ssh, null, () => Task.CompletedTask);
            typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Sessions.TerminalView(session), session, origin]);

            session.Connect();
            for (int i = 0; i < 10; i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }

            Assert.Equal(string.Empty, session.Sent.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_offer_hides_while_a_session_is_full_screen_and_comes_back_after()
    {
        new WorkspaceStore(WorkspaceFile).Save(new Workspace { Tabs = [new WorkspaceTab(_saved.Id, null, 0, Protocol.Ssh)] });
        var window = NewWindow();
        try
        {
            window.Show();
            await window.OfferWorkspaceAsync();
            AddTab(window, new IdleSession("desk.example", 3389), new SessionOrigin("desk", Protocol.Rdp, null, () => Task.CompletedTask));
            Dispatcher.UIThread.RunJobs();
            var bar = window.FindControl<Border>("RestoreBar")!;
            Assert.True(bar.IsVisible);

            typeof(MainWindow).GetMethod("EnterFullScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null, null]);
            Assert.False(bar.IsVisible);

            typeof(MainWindow).GetMethod("ExitFullScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
            Assert.True(bar.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_full_screen_bar_goes_after_sliding_off_it_along_the_top_edge_and_moving_down()
    {
        var window = NewWindow();
        try
        {
            window.Width = 1200;
            window.Height = 800;
            window.Show();
            AddTab(window, new IdleSession("desk.example", 3389), new SessionOrigin("desk", Protocol.Rdp, null, () => Task.CompletedTask));
            Dispatcher.UIThread.RunJobs();
            typeof(MainWindow).GetMethod("EnterFullScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null, null]);
            Dispatcher.UIThread.RunJobs();
            var bar = window.FindControl<Border>("FullScreenBar")!;

            void Move(double x, double y)
            {
                window.MouseMove(new Avalonia.Point(x, y));
                Dispatcher.UIThread.RunJobs();
            }

            Move(20, 300);
            Move(20, 2);
            Assert.True(bar.IsVisible);

            // The strip that showed it doesn't take the click: the session under it does (a remote window's title
            // bar or close button sits there).
            var hotZone = window.FindControl<Border>("FullScreenHotZone")!;
            var hit = Avalonia.Input.InputExtensions.InputHitTest(window, new Avalonia.Point(20, 2)) as Avalonia.Visual;
            Assert.NotNull(hit);
            Assert.NotSame(hotZone, hit);
            Assert.False(hit is Window, "nothing under the top edge took the pointer");

            // Onto the bar, off it sideways along the top edge, then down: never over the bar on the way out.
            var middle = bar.TranslatePoint(new Avalonia.Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), window)!.Value;
            Move(middle.X, middle.Y);
            Move(20, 2);
            Assert.True(bar.IsVisible);
            Move(20, 300);
            Assert.False(bar.IsVisible);

            // Straight down off the bar still hides it.
            Move(20, 2);
            Move(middle.X, middle.Y);
            Move(middle.X, 300);
            Assert.False(bar.IsVisible);

            // Showing with the pointer already down in the session (an exit that never came): the next move hides it.
            bar.IsVisible = true;
            Move(24, 310);
            Assert.False(bar.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_full_screen_bar_says_when_the_session_is_recording()
    {
        var settings = AppSettingsStore.In(_location);
        settings.Save(settings.Load() with { RecordingNoteShown = true });
        var window = NewWindow();
        try
        {
            window.Width = 1200;
            window.Height = 800;
            window.Show();
            var vault = Assert.IsType<VaultFileStore>(VaultAutoLockTests.Private<ICredentialStore>(window, "_credentials"));
            await vault.UnlockAsync("correct horse battery staple");
            var session = new ConnectedTerminal(_saved.Host);
            typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,
                [new Sessions.TerminalView(session), session, new SessionOrigin(_saved.Name, Protocol.Ssh, _saved.Id, () => Task.CompletedTask)]);
            Dispatcher.UIThread.RunJobs();
            typeof(MainWindow).GetMethod("EnterFullScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null, null]);
            Dispatcher.UIThread.RunJobs();
            var bar = window.FindControl<Border>("FullScreenBar")!;
            var mark = window.FindControl<StackPanel>("FullScreenRecording")!;

            window.MouseMove(new Avalonia.Point(20, 300));
            window.MouseMove(new Avalonia.Point(20, 2));
            Dispatcher.UIThread.RunJobs();
            Assert.True(bar.IsVisible);
            Assert.False(mark.IsVisible);

            // Starting and stopping while the bar is showing changes it there and then.
            var tab = VaultAutoLockTests.Private<SessionTab>(window, "_currentTab");
            await (Task)typeof(MainWindow).GetMethod("StartRecordingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [tab])!;
            Assert.True(tab.IsRecording);
            Assert.True(mark.IsVisible);
            Assert.EndsWith(", recording", Avalonia.Automation.AutomationProperties.GetName(bar));

            var recorder = tab.Recorder!;
            recorder.Stop();
            await recorder.Completion;
            Dispatcher.UIThread.RunJobs();
            Assert.False(mark.IsVisible);
            Assert.DoesNotContain("recording", Avalonia.Automation.AutomationProperties.GetName(bar));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(RestoreSessionsMode.Never, true)]
    [InlineData(RestoreSessionsMode.Ask, false)] // only a connection deleted since: nothing left to offer
    public async Task No_bar_when_the_setting_is_Never_or_nothing_is_left_to_reopen(RestoreSessionsMode mode, bool connectionExists)
    {
        AppSettingsStore.In(_location).Save(new AppSettings { RestoreSessions = mode });
        new WorkspaceStore(WorkspaceFile).Save(new Workspace { Tabs = [new WorkspaceTab(connectionExists ? _saved.Id : Guid.NewGuid(), null, 0, Protocol.Ssh)] });
        var window = NewWindow();
        try
        {
            window.Show();
            await window.OfferWorkspaceAsync();

            Assert.False(window.FindControl<Border>("RestoreBar")!.IsVisible);
            Assert.Empty(VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Always_reopens_in_order_selects_the_active_tab_and_skips_deleted_connections()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    (await listener.AcceptTcpClientAsync()).Dispose();
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException)
            {
            }
        });

        AppSettingsStore.In(_location).Save(new AppSettings { RestoreSessions = RestoreSessionsMode.Always });
        new WorkspaceStore(WorkspaceFile).Save(new Workspace
        {
            Tabs =
            [
                new WorkspaceTab(null, "127.0.0.1", port, Protocol.Telnet),
                new WorkspaceTab(Guid.NewGuid(), null, 0, Protocol.Telnet), // deleted since
                new WorkspaceTab(null, "localhost", port, Protocol.Telnet),
            ],
            Active = 0,
        });
        var window = NewWindow();
        try
        {
            window.Show();
            var tabs = VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs");
            await Until(() => tabs.Count == 2);

            Assert.Equal(["127.0.0.1", "localhost"], tabs.Select(t => t.Session.Target.Host));
            await Until(() => !VaultAutoLockTests.Private<bool>(window, "_restoring"));
            Assert.Same(tabs[0], VaultAutoLockTests.Private<SessionTab?>(window, "_currentTab"));
        }
        finally
        {
            window.Close();
            listener.Stop();
        }
    }

    [AvaloniaFact]
    public void The_start_up_setting_is_a_choice_of_three_that_saves()
    {
        var settings = AppSettingsStore.In(_location);
        using var vault = new VaultFileStore(_location.FileIn("vault.frv"), autoLockAfter: TimeSpan.FromMinutes(30));
        var view = new Orrery.App.Settings.SettingsView(_location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var choices = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "RestoreSessions").ToList();
        Assert.Equal(["Ask me", "Always reopen them", "Never"], choices.Select(c => c.Content));
        Assert.True(choices[0].IsChecked);

        choices[2].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(RestoreSessionsMode.Never, settings.Load().RestoreMode);

        choices[1].IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(RestoreSessionsMode.Always, settings.Load().RestoreMode);
        window.Close();
    }

    [AvaloniaFact]
    public void With_Never_closing_remembers_the_window_but_not_the_tabs()
    {
        AppSettingsStore.In(_location).Save(new AppSettings { RestoreSessions = RestoreSessionsMode.Never });
        var window = NewWindow();
        window.Show();
        window.Width = 1111;
        AddTab(window, new IdleSession(_saved.Host), new SessionOrigin(_saved.Name, Protocol.Ssh, _saved.Id, () => Task.CompletedTask));
        AddTab(window, new IdleSession("typed.example", 2222), new SessionOrigin("typed.example", Protocol.Vnc, null, () => Task.CompletedTask));
        Dispatcher.UIThread.RunJobs();

        window.Close();

        var workspace = new WorkspaceStore(WorkspaceFile).Load();
        Assert.Empty(workspace.Tabs);
        Assert.Equal(-1, workspace.Active);
        Assert.Equal(1111, workspace.Window!.Width, 1);
        Assert.DoesNotContain("typed.example", File.ReadAllText(WorkspaceFile));
    }

    [AvaloniaFact]
    public async Task With_Never_a_list_kept_from_before_is_deleted_at_start_up()
    {
        AppSettingsStore.In(_location).Save(new AppSettings { RestoreSessions = RestoreSessionsMode.Never });
        new WorkspaceStore(WorkspaceFile).Save(new Workspace
        {
            Window = new WindowPlacement(60, 50, 1200, 800, false),
            Tabs = [new WorkspaceTab(_saved.Id, null, 0, Protocol.Ssh), new WorkspaceTab(null, "typed.example", 22, Protocol.Ssh)],
        });
        var window = NewWindow();
        try
        {
            window.Show();
            await window.OfferWorkspaceAsync();

            var workspace = new WorkspaceStore(WorkspaceFile).Load();
            Assert.Empty(workspace.Tabs);
            Assert.Equal(new WindowPlacement(60, 50, 1200, 800, false), workspace.Window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Choosing_Never_deletes_the_saved_list_now_and_withdraws_the_offer()
    {
        new WorkspaceStore(WorkspaceFile).Save(new Workspace { Tabs = [new WorkspaceTab(_saved.Id, null, 0, Protocol.Ssh)], Active = 0 });
        var window = NewWindow();
        try
        {
            window.Show();
            await window.OfferWorkspaceAsync();
            var bar = window.FindControl<Border>("RestoreBar")!;
            Assert.True(bar.IsVisible);

            AppSettingsStore.In(_location).Save(new AppSettings { RestoreSessions = RestoreSessionsMode.Never });
            window.ForgetOpenTabs();

            Assert.False(bar.IsVisible);
            Assert.Empty(new WorkspaceStore(WorkspaceFile).Load().Tabs);
        }
        finally
        {
            window.Close();
        }

        // Closing doesn't bring back the offer that was withdrawn.
        Assert.Empty(new WorkspaceStore(WorkspaceFile).Load().Tabs);
    }

    [AvaloniaFact]
    public void With_recent_connections_off_what_was_kept_is_deleted_at_start_up_and_nothing_new_is_written()
    {
        var recentFile = ComputerFiles.PathFor(_location, RecentConnections.FileName);
        var historyFile = ComputerFiles.PathFor(_location, ConnectionHistory.FileName);
        new RecentConnections(recentFile).Add(new RecentConnection(_saved.Id, _saved.Name, Protocol.Ssh, _saved.Host!, 22, null, DateTimeOffset.Now));
        new ConnectionHistory(historyFile).RecordConnected(_saved.Id, DateTimeOffset.Now);
        AppSettingsStore.In(_location).Save(new AppSettings { RememberRecentConnections = false });

        var window = NewWindow();
        try
        {
            window.Show();
            Assert.False(File.Exists(recentFile));
            Assert.False(File.Exists(historyFile));

            AddTab(window, new IdleSession("typed.example", 2222), new SessionOrigin("typed.example", Protocol.Vnc, null, () => Task.CompletedTask));
            AddTab(window, new IdleSession(_saved.Host!), new SessionOrigin(_saved.Name, Protocol.Ssh, _saved.Id, () => Task.CompletedTask));
            Dispatcher.UIThread.RunJobs();

            Assert.False(File.Exists(recentFile));
            Assert.False(File.Exists(historyFile));
            var panel = VaultAutoLockTests.Private<Orrery.App.Connections.RecentPanel>(window, "_recentPanel");
            var texts = panel.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains(texts, t => t?.StartsWith("Recent connections aren't remembered") == true);
            Assert.DoesNotContain(texts, t => t?.Contains("typed.example") == true);
        }
        finally
        {
            window.Close();
        }

        Assert.False(File.Exists(recentFile));
        Assert.False(File.Exists(historyFile));
    }

    [AvaloniaFact]
    public void Remembering_recent_connections_is_on_by_default_and_turning_it_off_is_saved_and_announced()
    {
        var settings = AppSettingsStore.In(_location);
        using var vault = new VaultFileStore(_location.FileIn("vault.frv"), autoLockAfter: TimeSpan.FromMinutes(30));
        var view = new Orrery.App.Settings.SettingsView(_location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        bool? changed = null;
        RestoreSessionsMode? mode = null;
        view.RememberRecentConnectionsChanged += (_, on) => changed = on;
        view.RestoreSessionsChanged += (_, chosen) => mode = chosen;
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var check = window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Remember recent connections"));
        Assert.True(check.IsChecked);
        Assert.Contains("Turning this off clears the list", Avalonia.Automation.AutomationProperties.GetHelpText(check));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Turning this off clears the list") == true);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("window's size and position") == true);

        check.IsChecked = false;
        Assert.False(changed);
        Assert.False(settings.Load().RemembersRecentConnections);

        check.IsChecked = true;
        Assert.True(changed);
        Assert.True(settings.Load().RemembersRecentConnections);

        window.GetVisualDescendants().OfType<RadioButton>().Single(r => Equals(r.Content, "Never")).IsChecked = true;
        Assert.Equal(RestoreSessionsMode.Never, mode);
        window.Close();
    }

    [AvaloniaFact]
    public void Opening_an_open_connection_goes_to_it_by_default_and_choosing_a_new_session_is_saved_and_announced()
    {
        var settings = AppSettingsStore.In(_location);
        using var vault = new VaultFileStore(_location.FileIn("vault.frv"), autoLockAfter: TimeSpan.FromMinutes(30));
        var view = new Orrery.App.Settings.SettingsView(_location, settings, vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        bool? changed = null;
        view.NewSessionOnOpenChanged += (_, on) => changed = on;
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var choice = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => Avalonia.Automation.AutomationProperties.GetName(c) == "Opening a connection that's already open");
        Assert.Equal(0, choice.SelectedIndex);
        Assert.False(settings.Load().OpensNewSession);

        choice.SelectedIndex = 1;
        Assert.True(changed);
        Assert.True(settings.Load().OpensNewSession);

        choice.SelectedIndex = 0;
        Assert.False(changed);
        Assert.False(settings.Load().OpensNewSession);
        window.Close();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
