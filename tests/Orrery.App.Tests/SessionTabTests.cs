using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Net.Sockets;
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

        public void Become(SessionState state, string? message = null, Exception? error = null)
        {
            State = state;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message, error));
        }
    }

    static SessionTab NewTab(IConnectionSession session, bool canRestart = true, int attempt = 0) =>
        new(session, new Border(), new SessionOrigin("box", Protocol.Rdp, null, () => Task.CompletedTask, AutoReconnect: canRestart)
        {
            Restart = canRestart ? () => throw new InvalidOperationException("not in this test") : null,
        })
        { ReconnectAttempt = attempt };

    [AvaloniaFact]
    public void A_drop_after_connecting_is_retried_but_not_an_unexplained_first_failure()
    {
        var dropped = new ScriptedSession();
        var droppedTab = NewTab(dropped);
        dropped.Become(SessionState.Connected);
        dropped.Become(SessionState.Failed, "reset");
        Dispatcher.UIThread.RunJobs();
        Assert.True(droppedTab.CanRetryByItself);

        // Never connected, and nothing says it was the network.
        var neverUp = new ScriptedSession();
        var neverUpTab = NewTab(neverUp);
        neverUp.Become(SessionState.Failed, "refused");
        Dispatcher.UIThread.RunJobs();
        Assert.False(neverUpTab.CanRetryByItself);

        // ...unless it was itself an automatic try, which keeps going while the server is down.
        var retry = new ScriptedSession();
        var retryTab = NewTab(retry, attempt: 2);
        retry.Become(SessionState.Failed, "refused");
        Dispatcher.UIThread.RunJobs();
        Assert.True(retryTab.CanRetryByItself);

        // Logged off.
        var loggedOff = new ScriptedSession();
        var loggedOffTab = NewTab(loggedOff);
        loggedOff.Become(SessionState.Connected);
        loggedOff.Become(SessionState.Disconnected);
        Dispatcher.UIThread.RunJobs();
        Assert.False(loggedOffTab.CanRetryByItself);
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
        Assert.False(tab.CanRetryByItself);

        var off = new ScriptedSession();
        var offTab = NewTab(off, canRestart: false);
        off.Become(SessionState.Connected);
        off.Become(SessionState.Failed);
        Dispatcher.UIThread.RunJobs();
        Assert.False(offTab.CanRetryByItself);
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

    // ---- Connection-lost screen ----

    static readonly SocketException Refused = new((int)SocketError.ConnectionRefused);

    static (SessionTab Tab, ScriptedSession Session, Window Window) ShowFailing(bool canRestart = true)
    {
        var session = new ScriptedSession();
        var tab = NewTab(session, canRestart);
        var window = new Window { Content = tab.Host, Width = 900, Height = 700 };
        window.Show();
        tab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();
        return (tab, session, window);
    }

    static IEnumerable<TextBlock> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));

    [AvaloniaFact]
    public void A_network_error_shows_the_screen_with_what_happened_and_is_retried_even_before_connecting()
    {
        var (tab, session, window) = ShowFailing();

        session.Become(SessionState.Failed, Refused.Message, Refused);
        Dispatcher.UIThread.RunJobs();

        Assert.True(tab.IsShowingFailure);
        Assert.Equal(FailureKind.Refused, tab.Failure!.Kind);
        Assert.True(tab.CanRetryByItself);
        var texts = Texts(window).Select(t => t.Text).ToList();
        Assert.Contains("Couldn't connect", texts);
        Assert.Contains("box turned down the connection on port 3389.", texts);
    }

    [AvaloniaFact]
    public void The_screen_is_read_out_and_its_Reconnect_button_says_what_happened()
    {
        var (_, session, window) = ShowFailing();
        var said = new List<string>();
        void Heard(string text) => said.Add(text);
        Orrery.App.Controls.ScreenReader.Announced += Heard;
        try
        {
            session.Become(SessionState.Failed, Refused.Message, Refused);
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            Orrery.App.Controls.ScreenReader.Announced -= Heard;
        }

        Assert.Contains(said, s => s.StartsWith("Couldn't connect. ", StringComparison.Ordinal) && s.Contains("port 3389"));
        var reconnect = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Reconnect"));
        Assert.Contains("port 3389", Avalonia.Automation.AutomationProperties.GetHelpText(reconnect));
    }

    [AvaloniaFact]
    public void A_refused_login_waits_for_the_user_even_after_a_drop()
    {
        var (tab, session, window) = ShowFailing();
        session.Become(SessionState.Connected);
        session.Become(SessionState.Failed, "The username or password was rejected.",
            new SessionFailureException(FailureKind.LoginFailed, "The username or password was rejected."));
        Dispatcher.UIThread.RunJobs();

        Assert.False(tab.CanRetryByItself);
        var texts = Texts(window).Select(t => t.Text).ToList();
        Assert.Contains("Login refused", texts);
        Assert.Contains("This needs you, so Orrery won't try again by itself.", texts);
    }

    [AvaloniaFact]
    public void Each_kind_of_failure_has_its_own_icon_and_heading_not_just_a_colour()
    {
        (string? Icon, string? Heading) Look(Exception error, bool connectFirst)
        {
            var (tab, session, window) = ShowFailing();
            if (connectFirst)
            {
                session.Become(SessionState.Connected);
            }

            session.Become(SessionState.Failed, error.Message, error);
            Dispatcher.UIThread.RunJobs();
            var icon = tab.IconShown;
            var heading = Texts(window).First().Text;
            window.Close();
            return (icon, heading);
        }

        var retry = Look(new SessionFailureException(FailureKind.ServerClosed, "gone"), connectFirst: true);
        var needsYou = Look(new SessionFailureException(FailureKind.HostKeyChanged, "changed"), connectFirst: false);
        var other = Look(new NotSupportedException("The server redirected the connection."), connectFirst: false);

        Assert.Equal(3, new[] { retry.Icon, needsYou.Icon, other.Icon }.Distinct().Count());
        Assert.Equal("Connection lost", retry.Heading);
        Assert.Equal("Host key changed", needsYou.Heading);
        Assert.Equal("Couldn't connect", other.Heading);
    }

    [AvaloniaFact]
    public void Reconnect_has_the_focus_so_enter_reconnects()
    {
        var (tab, session, window) = ShowFailing();
        int asked = 0;
        tab.ReconnectRequested += (_, _) => asked++;

        session.Become(SessionState.Failed, Refused.Message, Refused);
        Dispatcher.UIThread.RunJobs();
        var focused = Assert.IsType<Button>(window.FocusManager!.GetFocusedElement());
        Assert.Equal("Reconnect", focused.Content);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, asked);
    }

    [AvaloniaFact]
    public async Task Copy_details_puts_what_happened_when_and_the_technical_message_on_the_clipboard()
    {
        var (tab, session, window) = ShowFailing();
        session.Become(SessionState.Failed, Refused.Message, Refused);
        Dispatcher.UIThread.RunJobs();

        var copy = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Copy details"));
        copy.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var text = await window.Clipboard!.TryGetTextAsync();

        Assert.Equal(tab.DetailsText, text);
        Assert.Contains("box turned down the connection on port 3389.", text);
        Assert.Contains("SocketException (ConnectionRefused)", text);
        Assert.Contains($"At {tab.FailedAt:yyyy-MM-dd HH:mm}", text);
    }

    [AvaloniaFact]
    public void Stop_ends_the_countdown_and_further_tries()
    {
        var (tab, session, _) = ShowFailing();
        session.Become(SessionState.Connected);
        session.Become(SessionState.Failed, Refused.Message, Refused);
        Dispatcher.UIThread.RunJobs();
        int reconnects = 0;
        tab.ShowReconnecting(1, 10, TimeSpan.FromSeconds(30), () => reconnects++);

        tab.StopReconnecting();
        Dispatcher.UIThread.RunJobs();

        Assert.False(tab.IsReconnecting);
        Assert.True(tab.RetryStopped);
        Assert.False(tab.CanRetryByItself);
        Assert.False(tab.RetrySoon(TimeSpan.Zero));
        Assert.Equal(0, reconnects);
    }

    [AvaloniaFact]
    public void The_network_coming_back_brings_a_waiting_try_forward()
    {
        var (tab, session, _) = ShowFailing();
        session.Become(SessionState.Failed, Refused.Message, Refused);
        Dispatcher.UIThread.RunJobs();
        int reconnects = 0;
        tab.ShowReconnecting(4, 10, TimeSpan.FromSeconds(60), () => reconnects++);

        Assert.True(tab.RetrySoon(TimeSpan.Zero));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, reconnects);
    }

    [AvaloniaFact]
    public void With_reconnecting_off_the_screen_says_so()
    {
        var (tab, session, window) = ShowFailing(canRestart: false);
        session.Become(SessionState.Connected);
        session.Become(SessionState.Failed, Refused.Message, Refused);
        Dispatcher.UIThread.RunJobs();

        Assert.False(tab.CanRetryByItself);
        Assert.Contains("Reconnecting by itself is off for this connection.", Texts(window).Select(t => t.Text));
    }

    [AvaloniaFact]
    public void The_screen_goes_once_the_session_is_no_longer_failed()
    {
        var (tab, session, _) = ShowFailing();
        session.Become(SessionState.Failed, Refused.Message, Refused);
        Dispatcher.UIThread.RunJobs();
        Assert.True(tab.IsShowingFailure);

        session.Become(SessionState.Connecting);
        Dispatcher.UIThread.RunJobs();
        Assert.False(tab.IsShowingFailure);
    }

    [AvaloniaFact]
    public void A_local_shell_tab_says_local_and_names_the_user_and_computer()
    {
        var session = new ScriptedSession();
        var tab = new SessionTab(session, new Border(), new SessionOrigin("PowerShell 7", Protocol.Ssh, null, () => Task.CompletedTask) { Local = true });

        Assert.Equal("LOCAL", tab.Origin.Label);
        Assert.Equal("user@box", tab.Address);
        Assert.Contains(tab.Header.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "LOCAL");

        // Nothing to reconnect to: a local shell is never started again by itself. (WasCutOff became
        // CanRetryByItself with the connection-lost screen.)
        session.Become(SessionState.Connected);
        session.Become(SessionState.Failed);
        Dispatcher.UIThread.RunJobs();
        Assert.False(tab.CanRetryByItself);
        Assert.Equal("PowerShell 7, local terminal", tab.Header.Label);
    }

    [AvaloniaFact]
    public void A_local_shell_that_cannot_start_says_so_and_offers_restart_not_reconnect()
    {
        var session = new ScriptedSession();
        var tab = new SessionTab(session, new Border(), new SessionOrigin("PowerShell 7", Protocol.Ssh, null, () => Task.CompletedTask) { Local = true });
        var window = new Window { Content = tab.Host, Width = 900, Height = 700 };
        window.Show();
        tab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();

        session.Become(SessionState.Failed, "The system cannot find the file specified.");
        Dispatcher.UIThread.RunJobs();

        Assert.True(tab.IsShowingFailure);
        Assert.False(tab.CanRetryByItself);
        var texts = Texts(window).Select(t => t.Text).ToList();
        Assert.Contains("Couldn't start the shell", texts);
        Assert.DoesNotContain("Couldn't connect", texts);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Restart"));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Reconnect"));
        Assert.StartsWith("Couldn't start the shell: \"PowerShell 7\", local terminal on user@box", tab.DetailsText);
        window.Close();
    }
}
