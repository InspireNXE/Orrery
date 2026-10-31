using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class SessionTabTests
{
    sealed class FakeSession : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static (SessionTab Tab, Window Window, Point Centre, List<string> Events) Show()
    {
        var tab = new SessionTab(new FakeSession(), new Border(),
            new SessionOrigin("box", Protocol.Ssh, null, () => Task.CompletedTask));
        var events = new List<string>();
        tab.CloseRequested += (_, _) => events.Add("close");
        tab.Selected += (_, _) => events.Add("select");
        var window = new Window
        {
            Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { tab.Header } },
            Width = 800,
            Height = 200,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var centre = tab.Header.TranslatePoint(new Point(tab.Header.Bounds.Width / 2, tab.Header.Bounds.Height / 2), window)!.Value;
        return (tab, window, centre, events);
    }

    [AvaloniaFact]
    public void Middle_click_on_a_tab_closes_it()
    {
        var (_, window, centre, events) = Show();

        window.MouseDown(centre, MouseButton.Middle);
        window.MouseUp(centre, MouseButton.Middle);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["close"], events);
    }

    [AvaloniaFact]
    public void Middle_button_released_off_the_tab_leaves_it_open()
    {
        var (_, window, centre, events) = Show();

        window.MouseDown(centre, MouseButton.Middle);
        window.MouseMove(new Point(700, 150));
        window.MouseUp(new Point(700, 150), MouseButton.Middle);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(events);
    }

    [AvaloniaFact]
    public void Left_click_still_selects_the_tab()
    {
        var (_, window, centre, events) = Show();

        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["select"], events);
    }
}
