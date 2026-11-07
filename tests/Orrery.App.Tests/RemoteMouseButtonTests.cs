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
using PointerButton = Orrery.Core.Sessions.PointerButton;

namespace Orrery.App.Tests;

/// <summary>Mouse buttons reach the remote desktop as they go down and up, including when two overlap.</summary>
public class RemoteMouseButtonTests
{
    sealed class Desktop : IFrameBufferSession
    {
        public List<(PointerButton Button, bool Pressed)> Buttons { get; } = [];
        public ConnectionTarget Target { get; } = new("desk.example", 3389, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated { add { } remove { } }
        public int Width => 800;
        public int Height => 600;
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) { }
        public void SendKey(ushort scancode, bool pressed) { }
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) => Buttons.Add((button, pressed));
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static (Window Window, RemoteFrameView View, Desktop Desktop) Open()
    {
        var desktop = new Desktop();
        var view = new RemoteFrameView(desktop);
        var window = new Window { Content = view, Width = 800, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, desktop);
    }

    /// <summary>A button changing while another is held, as Avalonia reports it: a move, not a press or release.</summary>
    static void Moved(Window window, RemoteFrameView view, Point at, RawInputModifiers held, PointerUpdateKind kind)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        view.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, view, pointer, window, at, 0,
            new PointerPointProperties(held, kind), KeyModifiers.None));
    }

    [AvaloniaFact]
    public void A_quick_left_then_right_click_lets_go_of_the_left_button_on_the_remote()
    {
        var (window, view, desktop) = Open();
        var at = new Point(400, 300);

        // Left down, right down while it's held, left up while right is held, right up last.
        window.MouseDown(at, MouseButton.Left);
        Moved(window, view, at, RawInputModifiers.LeftMouseButton | RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);
        Moved(window, view, at, RawInputModifiers.RightMouseButton, PointerUpdateKind.LeftButtonReleased);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains((PointerButton.Left, false), desktop.Buttons);
        Assert.Contains((PointerButton.Right, true), desktop.Buttons);
        Assert.Contains((PointerButton.Right, false), desktop.Buttons);
        // Each change once, and nothing left down.
        Assert.Equal(4, desktop.Buttons.Count);
        Assert.Equal(desktop.Buttons.Count(b => b.Pressed), desktop.Buttons.Count(b => !b.Pressed));
        window.Close();
    }

    [AvaloniaFact]
    public void A_button_the_remote_still_holds_goes_up_when_the_last_one_does()
    {
        var (window, view, desktop) = Open();
        var at = new Point(400, 300);

        // The right button's release is never reported; the press ending lets go of it anyway.
        window.MouseDown(at, MouseButton.Left);
        Moved(window, view, at, RawInputModifiers.LeftMouseButton | RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            [(PointerButton.Left, true), (PointerButton.Right, true), (PointerButton.Left, false), (PointerButton.Right, false)],
            desktop.Buttons);
        window.Close();
    }

    [AvaloniaFact]
    public void An_ordinary_click_sends_one_press_and_one_release()
    {
        var (window, _, desktop) = Open();
        window.MouseDown(new Point(100, 100), MouseButton.Right);
        window.MouseUp(new Point(100, 100), MouseButton.Right);
        Assert.Equal([(PointerButton.Right, true), (PointerButton.Right, false)], desktop.Buttons);
        window.Close();
    }
}
