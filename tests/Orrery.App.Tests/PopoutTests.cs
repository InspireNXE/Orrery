using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class PopoutTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    sealed class IdleSession(string host) : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new(host, 22, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    MainWindow Open(params string[] names)
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        window.Show();
        var addTab = typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var name in names)
        {
            addTab.Invoke(window, [new Border(), new IdleSession(name), new SessionOrigin(name, Protocol.Ssh, null, () => Task.CompletedTask)]);
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    static List<SessionTab> Tabs(MainWindow window) => VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs");

    static void Call(MainWindow window, string method, params object?[] args)
    {
        var info = typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = info.Invoke(window, [.. args, .. Enumerable.Repeat(Type.Missing, info.GetParameters().Length - args.Length)]);
        if (result is Task task)
        {
            task.GetAwaiter().GetResult();
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_popped_out_session_leaves_the_strip_for_its_own_window_and_comes_back_to_its_place()
    {
        var window = Open("alpha", "bravo", "charlie");
        try
        {
            var tabs = Tabs(window);
            var (alpha, bravo, charlie) = (tabs[0], tabs[1], tabs[2]);
            var strip = VaultAutoLockTests.Private<StackPanel>(window, "_tabStrip");
            var sessionHost = VaultAutoLockTests.Private<Panel>(window, "_sessionHost");

            Call(window, "PopOut", bravo);

            var popout = Assert.IsType<PopoutWindow>(bravo.Popout);
            Assert.True(popout.IsVisible);
            Assert.Equal("bravo — Orrery", popout.Title);
            Assert.Same(popout, TopLevel.GetTopLevel(bravo.View));
            Assert.True(bravo.Host.IsVisible);
            Assert.Equal([alpha.Header, charlie.Header], strip.Children);
            Assert.DoesNotContain(bravo.Host, sessionHost.Children);

            // Still an open session, in its place, but the main window shows another.
            Assert.Equal([alpha, bravo, charlie], tabs);
            Assert.Same(charlie, VaultAutoLockTests.Private<SessionTab>(window, "_currentTab"));
            Assert.Contains(popout.Actions.Children, c => Avalonia.Automation.AutomationProperties.GetName(c)?.StartsWith("Back to the tabs") == true);

            Call(window, "Rejoin", bravo);

            Assert.Null(bravo.Popout);
            Assert.False(popout.IsVisible);
            Assert.Equal([alpha.Header, bravo.Header, charlie.Header], strip.Children);
            Assert.Contains(bravo.Host, sessionHost.Children);
            Assert.Same(window, TopLevel.GetTopLevel(bravo.View));
            Assert.Same(bravo, VaultAutoLockTests.Private<SessionTab>(window, "_currentTab"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tabs_moved_while_one_is_out_leave_it_its_place_among_them()
    {
        var window = Open("alpha", "bravo", "charlie");
        try
        {
            var tabs = Tabs(window);
            var (alpha, bravo, charlie) = (tabs[0], tabs[1], tabs[2]);
            var strip = VaultAutoLockTests.Private<StackPanel>(window, "_tabStrip");

            Call(window, "PopOut", bravo);
            Call(window, "MoveTab", charlie, 0);
            Assert.Equal([charlie.Header, alpha.Header], strip.Children);

            Call(window, "Rejoin", bravo);
            Assert.Equal([charlie.Header, alpha.Header, bravo.Header], strip.Children);
            Assert.Equal([charlie, alpha, bravo], tabs);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Closing_the_window_puts_the_session_back_and_closing_the_session_closes_the_window()
    {
        var window = Open("alpha", "bravo");
        try
        {
            var tabs = Tabs(window);
            var alpha = tabs[0];

            Call(window, "PopOut", alpha);
            var popout = alpha.Popout!;
            popout.Close();
            Dispatcher.UIThread.RunJobs();

            Assert.Null(alpha.Popout);
            Assert.Contains(alpha, tabs);
            Assert.Same(window, TopLevel.GetTopLevel(alpha.View));

            Call(window, "PopOut", alpha);
            popout = alpha.Popout!;
            Call(window, "CloseTabAsync", alpha.Session);

            Assert.False(popout.IsVisible);
            Assert.DoesNotContain(alpha, tabs);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_empty_session_area_says_when_every_session_is_in_its_own_window()
    {
        var window = Open("alpha");
        try
        {
            var alpha = Tabs(window)[0];
            Call(window, "PopOut", alpha);

            Assert.True(VaultAutoLockTests.Private<Control>(window, "_emptyState").IsVisible);
            Assert.Equal("The open session is in its own window", VaultAutoLockTests.Private<TextBlock>(window, "_emptyStateTitle").Text);
            Assert.Null(VaultAutoLockTests.Private<SessionTab?>(window, "_currentTab"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_workspace_remembers_a_popped_out_session_with_its_window()
    {
        var window = Open("alpha", "bravo");
        try
        {
            var bravo = Tabs(window)[1];
            Call(window, "PopOut", bravo);

            var workspace = window.CaptureWorkspace();

            Assert.Null(workspace.Tabs[0].Popout);
            var placement = Assert.IsType<Orrery.Core.Settings.WindowPlacement>(workspace.Tabs[1].Popout);
            Assert.Equal(bravo.Popout!.ClientSize.Width, placement.Width, 1);
            Assert.False(placement.Maximized);
            Assert.Equal(0, workspace.Active);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_tab_dragged_down_off_the_strip_pops_out_and_one_dragged_along_it_doesnt()
    {
        var window = Open("alpha", "bravo");
        try
        {
            var tabs = Tabs(window);
            var (alpha, bravo) = (tabs[0], tabs[1]);
            Point Center(SessionTab tab) => tab.Header.TranslatePoint(new Point(tab.Header.Bounds.Width / 2, tab.Header.Bounds.Height / 2), window)!.Value;

            var start = Center(alpha);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(0, 20));
            Dispatcher.UIThread.RunJobs();
            Assert.False(VaultAutoLockTests.Private<bool>(window, "_tabTearing"), "a little way down is still a move along the strip");

            // Into the middle of the session area: its edges split instead.
            var area = VaultAutoLockTests.Private<Panel>(window, "_sessionHost");
            var middle = area.TranslatePoint(new Point(area.Bounds.Width / 2, area.Bounds.Height / 2), window)!.Value;
            window.MouseMove(middle);
            Dispatcher.UIThread.RunJobs();
            Assert.True(VaultAutoLockTests.Private<bool>(window, "_tabTearing"));
            Assert.NotNull(alpha.Header.Cursor);

            window.MouseUp(middle, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(alpha.Popout);
            Assert.Null(bravo.Popout);
            Assert.Null(alpha.Header.Cursor);
            Assert.Equal([alpha, bravo], tabs);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_dragged_tab_lifts_off_the_strip_leaving_its_slot_and_becomes_a_window_when_pulled_away()
    {
        var window = Open("alpha", "bravo");
        try
        {
            var alpha = Tabs(window)[0];
            var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(window)!;
            int before = layer.Children.Count;
            var start = alpha.Header.TranslatePoint(new Point(20, 16), window)!.Value;

            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(30, 0));
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<Border>(Assert.Single(layer.Children.Skip(before)));
            Assert.Contains("slot", alpha.Header.Classes);
            Assert.Equal(0, ((Control)alpha.Header.Content!).Opacity);
            Assert.False(VaultAutoLockTests.Private<TabGhost>(window, "_tabGhost").AsWindow);

            window.MouseMove(start + new Point(30, 200));
            Dispatcher.UIThread.RunJobs();
            Assert.True(VaultAutoLockTests.Private<TabGhost>(window, "_tabGhost").AsWindow);

            window.MouseMove(start + new Point(30, 0));
            window.MouseUp(start + new Point(30, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(before, layer.Children.Count);
            Assert.DoesNotContain("slot", alpha.Header.Classes);
            Assert.Equal(1, ((Control)alpha.Header.Content!).Opacity);
            Assert.Null(alpha.Popout);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Drags a popout's tab, from the name in its bar, to <paramref name="target"/> in the main window.</summary>
    static void DragBack(MainWindow window, PopoutWindow popout, Point target, Action? whileHeld = null)
    {
        popout.UpdateLayout();
        var handle = popout.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("tabhandle"));
        var start = handle.TranslatePoint(new Point(20, handle.Bounds.Height / 2), popout)!.Value;
        var over = popout.PointToClient(window.PointToScreen(target));
        popout.MouseDown(start, MouseButton.Left);
        popout.MouseMove(start + new Point(20, 0));
        popout.MouseMove(over);
        Dispatcher.UIThread.RunJobs();
        popout.MouseMove(over);
        Dispatcher.UIThread.RunJobs();
        whileHeld?.Invoke();
        popout.MouseUp(over, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void A_popped_out_tab_dragged_onto_the_strip_goes_back_where_it_is_let_go()
    {
        var window = Open("alpha", "bravo", "charlie");
        try
        {
            var tabs = Tabs(window);
            var (alpha, bravo, charlie) = (tabs[0], tabs[1], tabs[2]);
            var strip = VaultAutoLockTests.Private<StackPanel>(window, "_tabStrip");
            Call(window, "PopOut", charlie);
            var popout = charlie.Popout!;
            window.UpdateLayout();

            // Just inside bravo's left edge: past alpha's middle, short of bravo's.
            var between = bravo.Header.TranslatePoint(new Point(4, 16), window)!.Value;
            DragBack(window, popout, between, whileHeld: () =>
            {
                var slot = Assert.Single(strip.Children.OfType<Border>(), b => b.Classes.Contains("tabslot"));
                Assert.Equal([alpha.Header, slot, bravo.Header], strip.Children);
            });

            Assert.Null(charlie.Popout);
            Assert.False(popout.IsVisible);
            Assert.Equal([alpha.Header, charlie.Header, bravo.Header], strip.Children);
            Assert.Equal([alpha, charlie, bravo], tabs);
            Assert.Same(charlie, VaultAutoLockTests.Private<SessionTab>(window, "_currentTab"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_popped_out_tab_let_go_away_from_the_strip_stays_in_its_window()
    {
        var window = Open("alpha", "bravo");
        try
        {
            var bravo = Tabs(window)[1];
            var strip = VaultAutoLockTests.Private<StackPanel>(window, "_tabStrip");
            Call(window, "PopOut", bravo);
            var popout = bravo.Popout!;
            var sessionHost = VaultAutoLockTests.Private<Panel>(window, "_sessionHost");

            DragBack(window, popout, sessionHost.TranslatePoint(new Point(200, 200), window)!.Value);

            Assert.Same(popout, bravo.Popout);
            Assert.True(popout.IsVisible);
            Assert.Equal([Tabs(window)[0].Header], strip.Children);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_notice_about_a_popped_out_session_shows_in_its_window()
    {
        var window = Open("alpha");
        try
        {
            var alpha = Tabs(window)[0];
            Call(window, "PopOut", alpha);
            var popout = alpha.Popout!;

            typeof(MainWindow).GetMethod("ShowNotice", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(SessionTab), typeof(string)])!
                .Invoke(window, [alpha, "Stopped recording alpha"]);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(popout.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Stopped recording alpha");
            Assert.False(VaultAutoLockTests.Private<TextBlock>(window, "_noticeText").IsVisible);
            Assert.False(popout.Detail.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Closing_orrery_closes_the_popped_out_windows_without_bringing_them_back()
    {
        var window = Open("alpha");
        var alpha = Tabs(window)[0];
        Call(window, "PopOut", alpha);
        var popout = alpha.Popout!;

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(popout.IsVisible);
        Assert.Null(alpha.Popout);
        Assert.Empty(VaultAutoLockTests.Private<StackPanel>(window, "_tabStrip").Children);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
