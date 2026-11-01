using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
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

    sealed class ScriptedSession : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new("box", 3389, "user", null);
        public SessionState State { get; private set; } = SessionState.Connecting;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Become(SessionState state, string? message = null)
        {
            State = state;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
        }
    }

    static SessionTab NewTab(IConnectionSession session, bool canRestart = true, int attempt = 0) =>
        new(session, new Border(), new SessionOrigin("box", Protocol.Rdp, null, () => Task.CompletedTask, AutoReconnect: canRestart)
        {
            Restart = canRestart ? () => throw new InvalidOperationException("not in this test") : null,
        })
        { ReconnectAttempt = attempt };

    [AvaloniaFact]
    public void Only_a_drop_after_connecting_counts_as_cut_off()
    {
        var dropped = new ScriptedSession();
        var droppedTab = NewTab(dropped);
        dropped.Become(SessionState.Connected);
        dropped.Become(SessionState.Failed, "reset");
        Dispatcher.UIThread.RunJobs();
        Assert.True(droppedTab.WasCutOff);

        // Never connected: a wrong host or a refused login, not a drop.
        var neverUp = new ScriptedSession();
        var neverUpTab = NewTab(neverUp);
        neverUp.Become(SessionState.Failed, "refused");
        Dispatcher.UIThread.RunJobs();
        Assert.False(neverUpTab.WasCutOff);

        // ...unless it was itself an automatic try, which keeps going while the server is down.
        var retry = new ScriptedSession();
        var retryTab = NewTab(retry, attempt: 2);
        retry.Become(SessionState.Failed, "refused");
        Dispatcher.UIThread.RunJobs();
        Assert.True(retryTab.WasCutOff);

        // Logged off.
        var loggedOff = new ScriptedSession();
        var loggedOffTab = NewTab(loggedOff);
        loggedOff.Become(SessionState.Connected);
        loggedOff.Become(SessionState.Disconnected);
        Dispatcher.UIThread.RunJobs();
        Assert.False(loggedOffTab.WasCutOff);
    }

    [AvaloniaFact]
    public void A_drop_the_user_caused_or_with_reconnecting_off_isnt_retried()
    {
        var session = new ScriptedSession();
        var tab = NewTab(session);
        session.Become(SessionState.Connected);
        tab.EndedByUser = true;
        session.Become(SessionState.Failed);
        Dispatcher.UIThread.RunJobs();
        Assert.False(tab.WasCutOff);

        var off = new ScriptedSession();
        var offTab = NewTab(off, canRestart: false);
        off.Become(SessionState.Connected);
        off.Become(SessionState.Failed);
        Dispatcher.UIThread.RunJobs();
        Assert.False(offTab.WasCutOff);
    }

    [AvaloniaFact]
    public void A_tab_remembers_that_its_session_connected_and_why_it_failed()
    {
        var session = new ScriptedSession();
        var tab = NewTab(session);

        session.Become(SessionState.Connected);
        session.Become(SessionState.Failed, "The connection was reset.");
        Dispatcher.UIThread.RunJobs();

        Assert.True(tab.HasConnected);
        Assert.Equal("The connection was reset.", tab.LastMessage);
    }

    [AvaloniaFact]
    public void The_reconnect_notice_reconnects_once_its_time_is_up()
    {
        var tab = NewTab(new ScriptedSession());
        int reconnects = 0;

        tab.ShowReconnecting(1, 5, TimeSpan.Zero, () => reconnects++);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, reconnects);
        Assert.False(tab.IsReconnecting);
    }

    [AvaloniaFact]
    public void Cancelling_the_reconnect_notice_stops_it()
    {
        var tab = NewTab(new ScriptedSession());
        int reconnects = 0;

        tab.ShowReconnecting(2, 5, TimeSpan.FromSeconds(30), () => reconnects++);
        Assert.True(tab.IsReconnecting);
        tab.CancelReconnect();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, reconnects);
        Assert.False(tab.IsReconnecting);
    }

    [AvaloniaFact]
    public void A_local_shell_tab_says_local_and_names_the_user_and_computer()
    {
        var session = new ScriptedSession();
        var tab = new SessionTab(session, new Border(), new SessionOrigin("PowerShell 7", Protocol.Ssh, null, () => Task.CompletedTask) { Local = true });

        Assert.Equal("LOCAL", tab.Origin.Label);
        Assert.Equal("user@box", tab.Address);
        Assert.Contains(tab.Header.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "LOCAL");

        // Nothing to reconnect to: a local shell is never started again by itself.
        session.Become(SessionState.Connected);
        session.Become(SessionState.Failed);
        Dispatcher.UIThread.RunJobs();
        Assert.False(tab.WasCutOff);
    }
}
