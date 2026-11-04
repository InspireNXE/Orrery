using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class SplitViewTests : IDisposable
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

    static SessionTab? Current(MainWindow window) => VaultAutoLockTests.Private<SessionTab?>(window, "_currentTab");

    static SessionSplitPanel Area(MainWindow window) => (SessionSplitPanel)VaultAutoLockTests.Private<Panel>(window, "_sessionHost");

    static void Call(MainWindow window, string method, params object?[] args)
    {
        var info = typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = info.Invoke(window, [.. args, .. Enumerable.Repeat(Type.Missing, info.GetParameters().Length - args.Length)]);
        if (result is Task task)
        {
            task.GetAwaiter().GetResult();
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    static string[] MenuTitles(MainWindow window, SessionTab tab) =>
        ((IEnumerable<Control>)typeof(MainWindow).GetMethod("TabActions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [tab])!)
            .OfType<MenuItem>().Select(m => m.Header?.ToString() ?? string.Empty).ToArray();

    [AvaloniaFact]
    public void The_shortcut_puts_the_current_tab_beside_the_next_and_takes_it_away_again()
    {
        var window = Open("alpha", "bravo", "charlie");
        try
        {
            var (alpha, bravo, charlie) = (Tabs(window)[0], Tabs(window)[1], Tabs(window)[2]);
            Call(window, "SelectTab", alpha);

            Call(window, "ToggleSplit", alpha);

            var area = Area(window);
            Assert.True(area.IsSplit);
            Assert.Same(alpha, Current(window));
            Assert.True(alpha.Host.IsVisible);
            Assert.True(bravo.Host.IsVisible);
            Assert.False(charlie.Host.IsVisible);
            Assert.Contains("beside", bravo.Header.Classes);

            // Side by side, the current tab on the left, each about half the area.
            Assert.Equal(0, alpha.Host.Bounds.X);
            Assert.True(bravo.Host.Bounds.X > alpha.Host.Bounds.Right);
            Assert.InRange(alpha.Host.Bounds.Width, area.Bounds.Width * 0.45, area.Bounds.Width * 0.55);
            Assert.InRange(bravo.Host.Bounds.Width, area.Bounds.Width * 0.45, area.Bounds.Width * 0.55);

            Call(window, "ToggleSplit", alpha);

            Assert.False(area.IsSplit);
            Assert.Same(alpha, Current(window));
            Assert.False(bravo.Host.IsVisible);
            Assert.DoesNotContain("beside", bravo.Header.Classes);
            Assert.Equal(area.Bounds.Width, alpha.Host.Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_last_tab_goes_beside_the_one_before_it()
    {
        var window = Open("alpha", "bravo");
        try
        {
            var (alpha, bravo) = (Tabs(window)[0], Tabs(window)[1]);
            Call(window, "SelectTab", bravo);

            Call(window, "ToggleSplit", bravo);

            Assert.True(alpha.Host.IsVisible);
            Assert.True(bravo.Host.IsVisible);
            Assert.Same(alpha, VaultAutoLockTests.Private<SessionTab?>(window, "_splitRight"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Choosing_another_tab_swaps_it_into_the_side_with_the_focus_and_the_other_side_stays()
    {
        var window = Open("alpha", "bravo", "charlie");
        try
        {
            var (alpha, bravo, charlie) = (Tabs(window)[0], Tabs(window)[1], Tabs(window)[2]);
            Call(window, "SelectTab", alpha);
            Call(window, "ToggleSplit", alpha);

            Call(window, "SelectTab", charlie);

            Assert.Same(charlie, Current(window));
            Assert.True(charlie.Host.IsVisible);
            Assert.True(bravo.Host.IsVisible);
            Assert.False(alpha.Host.IsVisible);
            Assert.Same(charlie, VaultAutoLockTests.Private<SessionTab?>(window, "_splitLeft"));
            Assert.Same(bravo, VaultAutoLockTests.Private<SessionTab?>(window, "_splitRight"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Focus_moving_into_the_other_side_makes_it_the_current_tab()
    {
        var window = Open("alpha", "bravo");
        try
        {
            var (alpha, bravo) = (Tabs(window)[0], Tabs(window)[1]);
            Call(window, "SelectTab", alpha);
            Call(window, "ToggleSplit", alpha);
            Assert.True(Area(window).LeftActive);

            Call(window, "OnSessionAreaFocus", bravo.View);

            Assert.Same(bravo, Current(window));
            Assert.False(Area(window).LeftActive);
            Assert.True(Area(window).IsSplit);
            Assert.Contains("beside", alpha.Header.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Closing_a_side_gives_the_other_the_whole_area_and_closing_another_tab_keeps_the_split()
    {
        var window = Open("alpha", "bravo", "charlie");
        try
        {
            var (alpha, bravo, charlie) = (Tabs(window)[0], Tabs(window)[1], Tabs(window)[2]);
            Call(window, "SelectTab", alpha);
            Call(window, "ToggleSplit", alpha);

            Call(window, "CloseTabAsync", charlie.Session);
            Assert.True(Area(window).IsSplit);
            Assert.Same(alpha, Current(window));

            Call(window, "CloseTabAsync", alpha.Session);
            Assert.False(Area(window).IsSplit);
            Assert.Same(bravo, Current(window));
            Assert.True(bravo.Host.IsVisible);
            Assert.DoesNotContain("beside", bravo.Header.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Popping_out_a_side_ends_the_split_and_shows_the_other_but_popping_out_another_tab_keeps_it()
    {
        var window = Open("alpha", "bravo", "charlie");
        try
        {
            var (alpha, bravo) = (Tabs(window)[0], Tabs(window)[1]);
            Call(window, "SelectTab", alpha);
            Call(window, "ToggleSplit", alpha);

            Call(window, "PopOut", Tabs(window)[2]);
            Assert.True(Area(window).IsSplit);
            Call(window, "Rejoin", Tabs(window)[2]);
            Call(window, "SelectTab", alpha);

            Call(window, "PopOut", alpha);

            Assert.False(Area(window).IsSplit);
            Assert.Same(bravo, Current(window));
            Assert.NotNull(alpha.Popout);
            Call(window, "Rejoin", alpha);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_tab_menu_offers_the_split_and_then_its_end()
    {
        var window = Open("alpha", "bravo");
        try
        {
            var (alpha, bravo) = (Tabs(window)[0], Tabs(window)[1]);
            Call(window, "SelectTab", alpha);

            Assert.Contains("Side by side with the next tab", MenuTitles(window, alpha));
            Assert.Contains("Side by side with the current tab", MenuTitles(window, bravo));

            Call(window, "ToggleSplit", alpha);
            Assert.Contains("End side by side", MenuTitles(window, alpha));
            Assert.Contains("End side by side", MenuTitles(window, bravo));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_tab_dragged_to_the_right_edge_of_the_session_area_goes_beside_the_current_one()
    {
        var window = Open("alpha", "bravo", "charlie");
        try
        {
            var (alpha, charlie) = (Tabs(window)[0], Tabs(window)[2]);
            Call(window, "SelectTab", alpha);
            var area = Area(window);
            var start = charlie.Header.TranslatePoint(new Point(charlie.Header.Bounds.Width / 2, charlie.Header.Bounds.Height / 2), window)!.Value;
            var edge = area.TranslatePoint(new Point(area.Bounds.Width - 40, area.Bounds.Height / 2), window)!.Value;

            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(0, 20));
            window.MouseMove(edge);
            Dispatcher.UIThread.RunJobs();
            Assert.True(VaultAutoLockTests.Private<Border?>(window, "_splitPreview")?.IsVisible);

            window.MouseUp(edge, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(area.IsSplit);
            Assert.Null(charlie.Popout);
            Assert.Same(charlie, Current(window));
            Assert.Same(alpha, VaultAutoLockTests.Private<SessionTab?>(window, "_splitLeft"));
            Assert.Same(charlie, VaultAutoLockTests.Private<SessionTab?>(window, "_splitRight"));
            Assert.False(VaultAutoLockTests.Private<Border?>(window, "_splitPreview")?.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_divider_keeps_each_side_at_least_a_fifth_of_the_area()
    {
        var area = new SessionSplitPanel { Ratio = 0.05 };
        Assert.Equal(SessionSplitPanel.MinRatio, area.Ratio);
        area.Ratio = 0.99;
        Assert.Equal(1 - SessionSplitPanel.MinRatio, area.Ratio);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
