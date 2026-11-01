using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class RemoteFrameViewTests
{
    [Theory]
    [InlineData(1.0, false, 1200, 800)]
    [InlineData(1.5, false, 1800, 1200)] // Device pixels: sharp, small text.
    [InlineData(1.5, true, 1200, 800)]   // Screen units: enlarged 1.5× to fill the view.
    [InlineData(2.0, true, 1200, 800)]
    public void Asks_for_device_pixels_unless_zoomed(double displayScale, bool zoom, int width, int height)
    {
        Assert.Equal((width, height), RemoteFrameView.DesktopSize(new Size(1200, 800), displayScale, zoom));
    }

    /// <summary>A connected desktop whose frames can't be copied, as when the bitmap and the frame disagree.</summary>
    sealed class UndrawableDesktop : IFrameBufferSession
    {
        public int Disconnects { get; private set; }
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State { get; private set; } = SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
        public int Width => 64;
        public int Height => 48;
        public void Update() => FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) =>
            throw new ArgumentOutOfRangeException(nameof(destinationStride));
        public void SendKey(ushort scancode, bool pressed) { }
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }

        public Task DisconnectAsync()
        {
            Disconnects++;
            State = SessionState.Disconnected;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(State, null));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public void A_frame_that_cant_be_drawn_ends_the_session_instead_of_the_app()
    {
        var session = new UndrawableDesktop();
        var view = new RemoteFrameView(session);
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        session.Update();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, session.Disconnects);
        Assert.False(view.HasFrame);
        var status = view.Children.OfType<Notice>().Single();
        Assert.True(status.IsVisible);
        Assert.StartsWith("Couldn't", status.Text); // The failure stays up after the session reports it disconnected.
        window.Close();
    }

    /// <summary>A VNC desktop that can raise cut text.</summary>
    sealed class CutTextDesktop : IFrameBufferSession, IClipboardSession
    {
        public ConnectionTarget Target { get; } = new("desk", 5900, "user", null);
        public SessionState State => SessionState.Connected;
#pragma warning disable CS0067 // Part of the interface; this fake never raises them.
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
#pragma warning restore CS0067
        public event EventHandler<string>? RemoteClipboardChanged;
        public int Width => 64;
        public int Height => 48;
        public void CutText(string text) => RemoteClipboardChanged?.Invoke(this, text);
        public void SendClipboard(string text) { }
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

    [AvaloniaFact]
    public void Cut_text_while_the_clipboard_is_locked_does_not_reach_the_dispatcher()
    {
        var session = new CutTextDesktop();
        var view = new RemoteFrameView(session)
        {
            // A Windows clipboard another process holds open fails every write.
            SetClipboardText = _ => throw new COMException("Clipboard can't be opened", unchecked((int)0x800401D0)),
        };
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var unhandled = new List<Exception>();
        void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
        {
            unhandled.Add(e.Exception);
            e.Handled = true; // So a failure here fails the test instead of ending the test host.
        }

        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        try
        {
            session.CutText("from the server");
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            Dispatcher.UIThread.UnhandledException -= OnUnhandled;
        }

        Assert.Empty(unhandled);
        window.Close();
    }

    /// <summary>A connected desktop that records the sizes it's asked for.</summary>
    sealed class ResizableDesktop : IFrameBufferSession
    {
        public List<(int Width, int Height)> Requests { get; } = [];
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State => SessionState.Connected;
#pragma warning disable CS0067 // Part of the interface; this fake never raises them.
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
#pragma warning restore CS0067
        public int Width => 1024;
        public int Height => 768;
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) { }
        public void SendKey(ushort scancode, bool pressed) { }
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) => Requests.Add((width, height));
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_fixed_desktop_size_isnt_changed_when_the_view_resizes(bool follows)
    {
        var session = new ResizableDesktop();
        var window = new Window { Content = new RemoteFrameView(session) { FollowsViewSize = follows }, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Width = 1000;

        // Past the view's settle delay, letting its timer run.
        for (int i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(100);
        }

        Assert.Equal(follows, session.Requests.Count > 0);
        window.Close();
    }
}
