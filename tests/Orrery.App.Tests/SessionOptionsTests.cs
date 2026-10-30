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

public class SessionOptionsTests
{
    sealed class FakeTerminal : ITerminalSession
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

    sealed class FakeDesktop : IFrameBufferSession
    {
        public List<(ushort Code, bool Pressed)> Keys { get; } = [];
        public int Clicks { get; private set; }
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated { add { } remove { } }
        public int Width => 0;
        public int Height => 0;
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) { }
        public void SendKey(ushort scancode, bool pressed) => Keys.Add((scancode, pressed));
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) => Clicks++;
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static Window Show(Control view)
    {
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        view.Focus();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Ctrl_plus_minus_and_zero_ask_for_a_text_size_and_a_new_size_reflows_the_terminal()
    {
        var session = new FakeTerminal();
        var view = new TerminalView(session);
        var window = Show(view);
        var steps = new List<int>();
        view.TextSizeRequested += (_, step) => steps.Add(step);

        window.KeyPressQwerty(PhysicalKey.Equal, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.Minus, RawInputModifiers.Control);
        window.KeyPressQwerty(PhysicalKey.Digit0, RawInputModifiers.Control);
        Assert.Equal([1, -1, 0], steps);

        var (columns, rows) = view.GridSize;
        view.TextSize = 28;
        Assert.True(view.GridSize.Columns <= columns); // The headless renderer gives text no width, so only rows show it.
        Assert.True(view.GridSize.Rows < rows);
        window.Close();
    }

    [AvaloniaFact]
    public void Saved_output_has_the_scrollback_until_it_is_cleared()
    {
        var session = new FakeTerminal();
        var view = new TerminalView(session);
        var window = Show(view);
        session.Output(string.Join("\r\n", Enumerable.Range(1, 60).Select(i => $"line {i}")));
        Dispatcher.UIThread.RunJobs();

        Assert.StartsWith("line 1" + Environment.NewLine, view.AllText());
        view.ClearScrollback();
        Assert.DoesNotContain("line 1" + Environment.NewLine, view.AllText());
        Assert.EndsWith("line 60", view.AllText());
        window.Close();
    }

    [AvaloniaFact]
    public void Key_combos_press_in_order_and_let_go_in_reverse()
    {
        var session = new FakeDesktop();
        var view = new RemoteFrameView(session);
        var window = Show(view);

        view.SendKeys(RemoteKeyCombo.CtrlAltDel);

        Assert.Equal([(0x1D, true), (0x38, true), (0xE053, true), (0xE053, false), (0x38, false), (0x1D, false)], session.Keys);
        window.Close();
    }

    [AvaloniaFact]
    public void View_only_sends_no_keys_or_clicks()
    {
        var session = new FakeDesktop();
        var view = new RemoteFrameView(session) { ViewOnly = true };
        var window = Show(view);

        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        window.MouseDown(new Avalonia.Point(100, 100), MouseButton.Left);
        window.MouseUp(new Avalonia.Point(100, 100), MouseButton.Left);

        Assert.Empty(session.Keys);
        Assert.Equal(0, session.Clicks);

        view.ViewOnly = false;
        window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
        Assert.NotEmpty(session.Keys);
        window.Close();
    }
}
