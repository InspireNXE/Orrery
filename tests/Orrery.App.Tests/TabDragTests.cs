using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class TabDragTests : IDisposable
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

    [AvaloniaFact]
    public void A_dragged_tab_follows_the_pointer_the_others_make_room_and_it_lands_where_dropped()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            var addTab = typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var name in new[] { "alpha", "bravo", "charlie" })
            {
                addTab.Invoke(window, [new Border(), new IdleSession(name), new SessionOrigin(name, Protocol.Ssh, null, () => Task.CompletedTask)]);
            }

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var tabs = VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs");
            var (alpha, bravo, charlie) = (tabs[0], tabs[1], tabs[2]);
            Point Center(SessionTab tab) => tab.Header.TranslatePoint(new Point(tab.Header.Bounds.Width / 2, tab.Header.Bounds.Height / 2), window)!.Value;

            // Hold alpha and carry it past bravo's middle.
            var start = Center(alpha);
            double distance = Center(bravo).X - start.X + 10;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Point(20, 0));
            window.MouseMove(start + new Point(distance, 0));
            Dispatcher.UIThread.RunJobs();

            Assert.True(VaultAutoLockTests.Private<bool>(window, "_tabDragging"), "the drag should have started");
            // Alpha, lifted, follows the pointer over the window; its empty slot goes to where it will land.
            window.UpdateLayout();
            var ghost = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(window)!.Children.OfType<Border>().Last();
            double heldLeft = start.X - alpha.Header.Bounds.Width / 2;
            Assert.Equal(heldLeft + distance, ghost.TranslatePoint(default, window)!.Value.X, 1);
            Assert.True(alpha.Header.RenderTransform is TranslateTransform { Transitions.Count: > 0 }, "alpha's slot should slide to where it lands");
            Assert.True(bravo.Header.RenderTransform is TranslateTransform { } shift && shift.Transitions is { Count: > 0 },
                "bravo should be sliding left into alpha's place");
            Assert.True(charlie.Header.RenderTransform is not TranslateTransform { X: not 0 });

            window.MouseUp(start + new Point(distance, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([bravo, alpha, charlie], tabs);
            var strip = VaultAutoLockTests.Private<StackPanel>(window, "_tabStrip");
            Assert.Equal([bravo.Header, alpha.Header, charlie.Header], strip.Children);
            Assert.All(tabs, t => Assert.IsNotType<TranslateTransform>(t.Header.RenderTransform));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_last_tab_can_be_dragged_first_even_when_its_wider_with_the_normal_cursor()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            var addTab = typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var name in new[] { "A", "B with a much longer title" })
            {
                addTab.Invoke(window, [new Border(), new IdleSession(name), new SessionOrigin(name, Protocol.Ssh, null, () => Task.CompletedTask)]);
            }

            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var tabs = VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs");
            var (a, b) = (tabs[0], tabs[1]);
            Assert.True(b.Header.Bounds.Width > a.Header.Bounds.Width + 40);

            // Drag B well past the start of the strip; it stops at the start.
            var start = b.Header.TranslatePoint(new Point(b.Header.Bounds.Width / 2, b.Header.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start - new Point(20, 0));
            window.MouseMove(start - new Point(600, 0));
            Dispatcher.UIThread.RunJobs();
            Assert.Null(window.Cursor);

            window.MouseUp(start - new Point(600, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([b, a], tabs);
        }
        finally
        {
            window.Close();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
