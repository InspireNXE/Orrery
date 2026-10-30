using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>A session that connects while the user is typing in the properties pane mustn't receive the rest of it.</summary>
public class FocusOnConnectTests
{
    sealed class ConnectingTerminal : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State { get; private set; } = SessionState.Connecting;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
#pragma warning disable CS0067 // Part of the interface; the test never raises it.
        public event Action<byte[]>? DataReceived;
#pragma warning restore CS0067
        public void Connect()
        {
            State = SessionState.Connected;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(State, null));
        }

        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class ConnectingDesktop : IFrameBufferSession
    {
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State { get; private set; } = SessionState.Connecting;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
#pragma warning disable CS0067 // Part of the interface; the test never raises it.
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
#pragma warning restore CS0067
        public int Width => 64;
        public int Height => 48;
        public void Connect()
        {
            State = SessionState.Connected;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(State, null));
        }

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

    /// <summary>A window with a session view beside a text box, like the main window's session and properties panes.</summary>
    static (Window Window, TextBox Box) Show(Control view, bool focusBox)
    {
        var box = new TextBox();
        DockPanel.SetDock(box, Dock.Right);
        var window = new Window
        {
            Width = 800,
            Height = 500,
            Content = new DockPanel { Children = { box, view } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (focusBox)
        {
            box.Focus();
            Assert.Same(box, window.FocusManager!.GetFocusedElement());
        }
        else
        {
            window.FocusManager!.ClearFocus();
        }

        return (window, box);
    }

    [AvaloniaFact]
    public void A_terminal_that_connects_leaves_focus_in_a_text_box()
    {
        var session = new ConnectingTerminal();
        var view = new TerminalView(session);
        var (window, box) = Show(view, focusBox: true);

        session.Connect();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(box, window.FocusManager!.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact]
    public void A_terminal_that_connects_takes_focus_when_nothing_has_it()
    {
        var session = new ConnectingTerminal();
        var view = new TerminalView(session);
        var (window, _) = Show(view, focusBox: false);

        session.Connect();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(view, window.FocusManager!.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact]
    public void A_desktop_that_connects_leaves_focus_in_a_text_box()
    {
        var session = new ConnectingDesktop();
        var view = new RemoteFrameView(session);
        var (window, box) = Show(view, focusBox: true);

        session.Connect();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(box, window.FocusManager!.GetFocusedElement());
        window.Close();
    }

    [AvaloniaFact]
    public void A_desktop_that_connects_takes_focus_when_nothing_has_it()
    {
        var session = new ConnectingDesktop();
        var view = new RemoteFrameView(session);
        var (window, _) = Show(view, focusBox: false);

        session.Connect();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(view, window.FocusManager!.GetFocusedElement());
        window.Close();
    }
}
