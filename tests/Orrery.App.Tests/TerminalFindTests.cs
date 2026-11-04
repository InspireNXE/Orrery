using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class TerminalFindTests
{
    sealed class FakeSession : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived;
        public void Output(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static (FakeSession Session, TerminalView View, TerminalFindBar Bar, Window Window) Shown()
    {
        var session = new FakeSession();
        var view = new TerminalView(session);
        var bar = new TerminalFindBar(view);
        var window = new Window { Width = 600, Height = 300, Content = new Grid { Children = { view, bar } } };
        window.Show();
        return (session, view, bar, window);
    }

    static void Type(TerminalFindBar bar, string text)
    {
        bar.Query.Text = text;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Typing_marks_every_match_and_goes_to_the_newest()
    {
        var (session, view, bar, window) = Shown();
        session.Output("error one\r\nfine\r\nERROR two\r\n");

        bar.Open();
        Type(bar, "error");

        Assert.Equal(2, view.Matches.Count);
        Assert.Equal(1, view.CurrentMatch);
        Assert.Equal("2 of 2", bar.CountText);
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_goes_to_older_matches_and_shift_enter_to_newer_round_the_ends()
    {
        var (session, view, bar, window) = Shown();
        session.Output("a x\r\nb x\r\nc x\r\n");
        bar.Open();
        Type(bar, "x");
        Assert.Equal(2, view.CurrentMatch);

        bar.Query.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal(1, view.CurrentMatch);
        Assert.Equal("2 of 3", bar.CountText);

        bar.Query.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift });
        bar.Query.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift });
        Assert.Equal(0, view.CurrentMatch);
        window.Close();
    }

    [AvaloniaFact]
    public void No_matches_says_so_and_escape_closes_and_clears_the_marks()
    {
        var (session, view, bar, window) = Shown();
        session.Output("hello\r\n");
        bar.Open();
        Type(bar, "nope");
        Assert.Equal("No matches", bar.CountText);

        Type(bar, "hello");
        Assert.Single(view.Matches);
        bar.Query.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });

        Assert.False(bar.IsVisible);
        Assert.Empty(view.Matches);
        window.Close();
    }

    [AvaloniaFact]
    public void New_output_is_searched_and_the_current_match_stays_current()
    {
        var (session, view, bar, window) = Shown();
        session.Output("x first\r\nx second\r\n");
        bar.Open();
        Type(bar, "x");
        view.FindStep(-1);
        Assert.Equal(0, view.CurrentMatch);

        session.Output("x third\r\n");
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.RefindPending);
        view.RefindNow();

        Assert.Equal(3, view.Matches.Count);
        Assert.Equal(0, view.CurrentMatch);
        Assert.Equal("1 of 3", bar.CountText);
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_shift_f_in_the_terminal_opens_find_with_the_selection()
    {
        var (session, view, bar, window) = Shown();
        session.Output("needle here\r\n");
        view.FindRequested += (_, _) => bar.Open("needle");

        view.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.F,
            KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift,
        });
        Dispatcher.UIThread.RunJobs();

        Assert.True(bar.IsVisible);
        Assert.Equal("needle", bar.Query.Text);
        Assert.Single(view.Matches);
        window.Close();
    }
}
