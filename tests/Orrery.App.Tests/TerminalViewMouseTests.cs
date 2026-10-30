using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class TerminalViewMouseTests
{
    sealed class FakeSession : ITerminalSession
    {
        public List<string> Sent { get; } = [];
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived;
        public void Output(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        public void Send(ReadOnlySpan<byte> data) => Sent.Add(Encoding.Latin1.GetString(data));
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static (Window Window, FakeSession Session) Open()
    {
        var session = new FakeSession();
        var window = new Window { Content = new TerminalView(session), Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, session);
    }

    [AvaloniaFact]
    public void Clicks_and_wheel_go_to_a_program_that_asked_for_the_mouse()
    {
        var (window, session) = Open();
        session.Output("\u001b[?1000;1006h");
        Dispatcher.UIThread.RunJobs();
        session.Sent.Clear();

        window.MouseDown(new Avalonia.Point(5, 5), MouseButton.Left);
        window.MouseUp(new Avalonia.Point(5, 5), MouseButton.Left);
        window.MouseWheel(new Avalonia.Point(5, 5), new Avalonia.Vector(0, -1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["\u001b[<0;1;1M", "\u001b[<0;1;1m", "\u001b[<65;1;1M"], session.Sent);
    }

    [AvaloniaFact]
    public void Without_a_request_or_with_shift_the_mouse_stays_local()
    {
        var (window, session) = Open();
        window.MouseDown(new Avalonia.Point(5, 5), MouseButton.Left);
        window.MouseUp(new Avalonia.Point(5, 5), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(session.Sent);

        session.Output("\u001b[?1000h");
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(new Avalonia.Point(5, 5), MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(new Avalonia.Point(5, 5), MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(session.Sent);
    }

    [AvaloniaFact]
    public void Dragging_is_reported_once_per_cell_in_button_motion_mode()
    {
        var (window, session) = Open();
        session.Output("\u001b[?1002;1006h");
        Dispatcher.UIThread.RunJobs();
        session.Sent.Clear();

        window.MouseMove(new Avalonia.Point(5, 5));                 // No button: not wanted in 1002.
        window.MouseDown(new Avalonia.Point(5, 5), MouseButton.Left);
        window.MouseMove(new Avalonia.Point(6, 6), RawInputModifiers.LeftMouseButton);    // Same cell.
        window.MouseMove(new Avalonia.Point(300, 6), RawInputModifiers.LeftMouseButton);  // New cell.
        window.MouseUp(new Avalonia.Point(300, 6), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, session.Sent.Count); // Press, one drag report, release.
        Assert.Equal("\u001b[<0;1;1M", session.Sent[0]);
        Assert.StartsWith("\u001b[<32;", session.Sent[1]);
        Assert.EndsWith(";1M", session.Sent[1]);
        Assert.EndsWith(";1m", session.Sent[2]);
    }

    [AvaloniaFact]
    public void The_pointer_is_an_arrow_while_a_program_has_the_mouse()
    {
        var (window, session) = Open();
        var view = (TerminalView)window.Content!;
        window.MouseMove(new Avalonia.Point(5, 5));
        var textCursor = view.Cursor;

        session.Output("[?1000h");
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(new Avalonia.Point(6, 6));
        var tracking = view.Cursor;
        window.MouseMove(new Avalonia.Point(7, 7), RawInputModifiers.Shift);
        var withShift = view.Cursor;

        Assert.NotSame(textCursor, tracking);
        Assert.Same(textCursor, withShift);
    }
}
