using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class RemotePointerCursorTests
{
    static RemotePointerShape Square(int size)
    {
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (200, 30, 30, 255);
        }

        return RemotePointerShape.Create(size, size, 0, 0, rgba)!;
    }

    // What a Windows server with the default pointers sends for each, and what this computer then shows.
    [Theory]
    [InlineData("aero_arrow.cur", PointerKind.Arrow)]
    [InlineData("aero_link.cur", PointerKind.Hand)]
    [InlineData("aero_ns.cur", PointerKind.SizeNorthSouth)]
    [InlineData("aero_ew.cur", PointerKind.SizeWestEast)]
    [InlineData("aero_nwse.cur", PointerKind.SizeNorthwestSoutheast)]
    [InlineData("aero_nesw.cur", PointerKind.SizeNortheastSouthwest)]
    [InlineData("aero_move.cur", PointerKind.SizeAll)]
    [InlineData("aero_unavail.cur", PointerKind.No)]
    [InlineData("aero_helpsel.cur", PointerKind.Help)]
    [InlineData("aero_busy.ani", PointerKind.Wait)]
    [InlineData("aero_working.ani", PointerKind.AppStarting)]
    [InlineData("beam_i.cur", PointerKind.IBeam)]
    public void A_standard_windows_pointer_is_recognized(string file, PointerKind kind)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' own pointers are only on Windows.");
        foreach (int size in new[] { 32, 48 })
        {
            var shape = WindowsPointerOutlines.AsServerSends(file, size);
            Assert.NotNull(shape);
            Assert.Equal(kind, PointerKinds.Recognize(shape!, WindowsPointerOutlines.For(size, size)));
        }
    }

    [Fact]
    public void A_shape_an_app_drew_is_not_taken_for_a_standard_one()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' own pointers are only on Windows.");
        Assert.Null(PointerKinds.Recognize(Square(32), WindowsPointerOutlines.For(32, 32)));
    }

    [AvaloniaFact]
    public void A_recognized_shape_shows_this_computers_pointer_and_another_its_own_picture()
    {
        var arrowOutline = new PointerOutline(PointerKind.Arrow, 4, 4, 0, 0, [.. Enumerable.Repeat(true, 16)]);
        using var cursors = new RemotePointerCursors { Outlines = (w, h) => w == 4 ? [arrowOutline] : [] };

        var standard = cursors.For(Square(4));
        Assert.Equal(PointerKind.Arrow, cursors.LastKind);
        Assert.Same(standard, cursors.For(Square(4)));

        var drawn = cursors.For(Square(8));
        Assert.Null(cursors.LastKind);
        Assert.Same(drawn, cursors.For(Square(8)));
        Assert.NotSame(standard, drawn);

        cursors.For(RemotePointerShape.Hidden);
        Assert.Null(cursors.LastKind);
        cursors.For(null);
        Assert.Equal(PointerKind.Arrow, cursors.LastKind);
    }

    sealed class PointingDesktop : IFrameBufferSession, IRemotePointerSession
    {
        public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
        public SessionState State { get; set; } = SessionState.Connected;
#pragma warning disable CS0067 // Part of the interfaces; this fake never raises them.
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
#pragma warning restore CS0067
        public RemotePointerShape? PointerShape { get; private set; }
        public event EventHandler? PointerShapeChanged;
        public int Width => 64;
        public int Height => 48;

        public void Point(RemotePointerShape? shape)
        {
            PointerShape = shape;
            PointerShapeChanged?.Invoke(this, EventArgs.Empty);
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

    [AvaloniaFact]
    public void The_view_follows_the_remote_pointer_while_connected()
    {
        var session = new PointingDesktop();
        var view = new RemoteFrameView(session);
        var window = new Window { Content = view, Width = 320, Height = 240 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PointerKind.Arrow, view.RemotePointerKind);

        session.Point(Square(13));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.RemotePointerKind);
        Assert.NotNull(view.GetVisualDescendants().OfType<Image>().First().Cursor);

        window.Close();
    }
}
