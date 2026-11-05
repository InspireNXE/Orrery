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

    /// <summary>A desktop that draws, and that the server can drop.</summary>
    sealed class DroppableDesktop : IFrameBufferSession
    {
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State { get; private set; } = SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
#pragma warning disable CS0067 // Part of the interface; this fake never raises it.
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
#pragma warning restore CS0067
        public int Width => 64;
        public int Height => 48;
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) { }
        public void SendKey(ushort scancode, bool pressed) { }
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Drop()
        {
            State = SessionState.Disconnected;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(State, null));
        }
    }

    [AvaloniaFact]
    public void A_dropped_desktop_is_dimmed_under_the_notice()
    {
        var session = new DroppableDesktop();
        var view = new RemoteFrameView(session);
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(view.Dimmed);

        session.Drop();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.Dimmed);
        var status = view.Children.OfType<Notice>().Single();
        Assert.Equal("Disconnected.", status.Text);
        Assert.True(view.Children.IndexOf(status) > view.Children.IndexOf(view.Children.OfType<Border>().First(b => b.IsVisible && b.Child is null)));
        window.Close();
    }

    /// <summary>A desktop that notes whether the view had its bitmap locked each time it was asked for pixels.</summary>
    sealed class WatchedDesktop : IFrameBufferSession
    {
        public RemoteFrameView? View { get; set; }
        public List<bool> BitmapLockedDuringCopy { get; } = [];
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State => SessionState.Connected;
#pragma warning disable CS0067 // Part of the interface; this fake never raises it.
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
        public int Width => 64;
        public int Height => 48;
        public void Update(FrameRect rect) => FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(rect, resized: false));

        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride)
        {
            BitmapLockedDuringCopy.Add(View!.DrawingIntoBitmap);
            for (int row = region.Y; row < region.Bottom; row++)
            {
                for (int x = region.X; x < region.Right; x++)
                {
                    Marshal.WriteInt32(destination + (nint)row * destinationStride + (nint)x * 4, unchecked((int)0xFF336699));
                }
            }
        }

        public void SendKey(ushort scancode, bool pressed) { }
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class GrowingDesktop : IFrameBufferSession
    {
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State => SessionState.Connected;
#pragma warning disable CS0067 // Part of the interface; this fake never raises it.
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
        public int Width { get; private set; } = 788;
        public int Height { get; private set; } = 743;

        public void Resize(int width, int height)
        {
            (Width, Height) = (width, height);
            FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, width, height), resized: true));
        }

        public void Update() => FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, 10, 10), resized: false));
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

    // The bar under the tabs shows the desktop's size, so it hears when a resize brings a new one, and only then.
    [AvaloniaFact]
    public void A_desktop_that_comes_in_at_a_new_size_says_so()
    {
        var session = new GrowingDesktop();
        var view = new RemoteFrameView(session);
        int resized = 0;
        view.DesktopResized += (_, _) => resized++;
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        session.Resize(788, 743);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, resized);

        session.Update();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, resized);

        session.Resize(1280, 720);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, resized);
        window.Close();
    }

    // Waiting for the session's lock with the bitmap locked froze the window: the wait lets Windows run a paint, which
    // waits for the render thread, which waits for the bitmap.
    [AvaloniaFact]
    public void The_session_is_asked_for_pixels_before_the_bitmap_is_locked()
    {
        var session = new WatchedDesktop();
        var view = new RemoteFrameView(session);
        session.View = view;
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        session.Update(new FrameRect(0, 0, 64, 48));
        Dispatcher.UIThread.RunJobs();
        session.Update(new FrameRect(10, 5, 20, 7));
        Dispatcher.UIThread.RunJobs();

        Assert.NotEmpty(session.BitmapLockedDuringCopy);
        Assert.DoesNotContain(true, session.BitmapLockedDuringCopy);
        Assert.True(view.HasFrame);
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
        public event EventHandler<string>? ClipboardRefused;
        public void Refuse(string reason) => ClipboardRefused?.Invoke(this, reason);
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
    public void Clipboard_text_the_session_refused_shows_across_the_top_and_is_read_out()
    {
        var session = new CutTextDesktop();
        var view = new RemoteFrameView(session);
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var spoken = new List<string>();
        void Heard(string text) => spoken.Add(text);
        ScreenReader.Announced += Heard;
        try
        {
            Assert.Null(view.BannerText);
            Task.Run(() => session.Refuse("too long")).Wait();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("too long", view.BannerText);
            Assert.Contains("too long", spoken);
            AccessibilityAudit.AssertAllNamed(window);
        }
        finally
        {
            ScreenReader.Announced -= Heard;
            window.Close();
        }
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
