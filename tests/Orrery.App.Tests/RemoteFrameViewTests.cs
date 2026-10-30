using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
        var status = view.Children.OfType<TextBlock>().Single();
        Assert.True(status.IsVisible);
        Assert.StartsWith("Couldn't", status.Text); // The failure stays up after the session reports it disconnected.
        window.Close();
    }
}
