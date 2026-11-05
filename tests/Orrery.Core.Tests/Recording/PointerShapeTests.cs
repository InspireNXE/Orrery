using Orrery.Core.Recording;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Recording;

sealed class FakePointerScreen(int width, int height) : IFrameBufferSession, IRemotePointerSession
{
    readonly FakeScreen _screen = new(width, height);

    public RemotePointerShape? PointerShape { get; private set; }
    public event EventHandler? PointerShapeChanged;

    public void SetShape(RemotePointerShape? shape)
    {
        PointerShape = shape;
        PointerShapeChanged?.Invoke(this, EventArgs.Empty);
    }

    public FakeScreen Screen => _screen;
    public int Width => _screen.Width;
    public int Height => _screen.Height;
    public ConnectionTarget Target => _screen.Target;
    public SessionState State => SessionState.Connected;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }

    public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated
    {
        add => _screen.FrameUpdated += value;
        remove => _screen.FrameUpdated -= value;
    }

    public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) => _screen.CopyRegion(region, destination, destinationStride);
    public void SendKey(ushort scancode, bool pressed) { }
    public void ReleaseAllKeys() { }
    public void SendPointerMove(int x, int y) { }
    public void SendPointerButton(PointerButton button, bool pressed) { }
    public void SendWheel(int units, bool horizontal) { }
    public void RequestResize(int width2, int height2) { }
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class PointerShapeTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _privateKey;
    readonly byte[] _publicKey;
    TimeSpan _now;

    public PointerShapeTests()
    {
        Directory.CreateDirectory(_dir);
        (_privateKey, _publicKey) = RecordingTestKeys.New();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static readonly RecordingInfo Info = new("desk", "RDP", DateTimeOffset.UnixEpoch);

    // A 3x2 shape: red, green, see-through on top; blue, half-white, black below.
    static RemotePointerShape Shape(int hotspotX = 1, int hotspotY = 1) => RemotePointerShape.Create(3, 2, hotspotX, hotspotY,
        [255, 0, 0, 255, 0, 255, 0, 255, 9, 9, 9, 0, 0, 0, 255, 255, 255, 255, 255, 128, 0, 0, 0, 255])!;

    RecordingWriter NewWriter(bool pointerShapes) =>
        RecordingWriter.Create(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".orrec"), RecordingKind.Screen, _publicKey, pointerShapes: pointerShapes);

    List<RecordedEvent> Read(string path, out int version)
    {
        using var reader = RecordingReader.Open(path, _privateKey);
        version = reader.Header.Version;
        return reader.Events().ToList();
    }

    [Fact]
    public void A_shape_the_arrow_and_a_hidden_pointer_come_back_as_they_went_in()
    {
        var shape = Shape(2, 0);
        Assert.True(ScreenFrames.TryDecodePointerShape(ScreenFrames.EncodePointerShape(shape), out var back));
        Assert.Equal((3, 2, 2, 0), (back!.Width, back.Height, back.HotspotX, back.HotspotY));
        Assert.Equal(shape.Rgba, back.Rgba);

        Assert.True(ScreenFrames.TryDecodePointerShape(ScreenFrames.EncodePointerShape(null), out var arrow));
        Assert.Null(arrow);
        Assert.True(ScreenFrames.TryDecodePointerShape(ScreenFrames.EncodePointerShape(RemotePointerShape.Hidden), out var hidden));
        Assert.Same(RemotePointerShape.Hidden, hidden);
    }

    [Fact]
    public void A_damaged_or_oversized_shape_is_refused()
    {
        var payload = ScreenFrames.EncodePointerShape(Shape());
        Assert.False(ScreenFrames.TryDecodePointerShape(payload[..^1], out _));
        Assert.False(ScreenFrames.TryDecodePointerShape(payload[..5], out _));

        // A size bigger than any pointer is refused before anything is allocated for it.
        var huge = (byte[])payload.Clone();
        huge[0] = 0xFF;
        huge[1] = 0xFF;
        Assert.False(ScreenFrames.TryDecodePointerShape(huge, out _));

        Assert.Null(RemotePointerShape.Create(3, 2, 0, 0, new byte[5]));
        Assert.Null(RemotePointerShape.Create(RemotePointerShape.MaxSide + 1, 1, 0, 0, new byte[(RemotePointerShape.MaxSide + 1) * 4]));
    }

    [Fact]
    public void A_shape_is_drawn_from_its_hotspot_blending_what_is_see_through()
    {
        var pixels = new byte[4 * 4 * 3];
        Array.Fill(pixels, (byte)100);
        RemotePointerShape.Draw(Shape(), pixels, 4, 4, 2, 2);

        (byte, byte, byte) At(int x, int y) => (pixels[(y * 4 + x) * 3], pixels[(y * 4 + x) * 3 + 1], pixels[(y * 4 + x) * 3 + 2]);
        Assert.Equal((255, 0, 0), At(1, 1));
        Assert.Equal((0, 255, 0), At(2, 1));
        Assert.Equal((100, 100, 100), At(3, 1));
        Assert.Equal((0, 0, 255), At(1, 2));
        Assert.Equal((178, 178, 178), At(2, 2));
        Assert.Equal((0, 0, 0), At(3, 2));
        Assert.Equal((100, 100, 100), At(0, 0));

        // Hidden draws nothing; no shape draws the usual arrow.
        var plain = new byte[4 * 4 * 3];
        RemotePointerShape.Draw(RemotePointerShape.Hidden, plain, 4, 4, 1, 1);
        Assert.All(plain, b => Assert.Equal(0, b));
        RemotePointerShape.Draw(null, plain, 4, 4, 0, 0);
        Assert.Contains(plain, b => b == 255);
    }

    [Fact]
    public void Premultiplied_pixels_are_turned_straight()
    {
        byte[] rgba = [64, 32, 0, 128, 10, 20, 30, 255, 0, 0, 0, 0];
        RemotePointerShape.Unpremultiply(rgba);
        Assert.Equal([128, 64, 0, 128, 10, 20, 30, 255, 0, 0, 0, 0], rgba);
    }

    [Fact]
    public async Task The_shape_is_recorded_when_it_changes_and_after_every_key_frame()
    {
        var session = new FakePointerScreen(16, 16);
        var shape = Shape();
        session.SetShape(shape);
        var recorder = new ScreenRecorder(NewWriter(pointerShapes: true), Info, 1, long.MaxValue, () => _now) { Pointer = () => (5, 5) };
        recorder.Attach(session);
        recorder.CaptureFrame();
        recorder.CaptureFrame(); // nothing changed: nothing written

        session.SetShape(RemotePointerShape.Hidden);
        recorder.CaptureFrame();
        session.SetShape(null);
        recorder.CaptureFrame();

        _now += TimeSpan.FromMinutes(1);
        session.Screen.Paint(new FrameRect(0, 0, 16, 16), 1, 2, 3);
        recorder.CaptureFrame(); // a key frame, with the shape again
        recorder.Stop();
        await recorder.Completion;

        var events = Read(recorder.Path, out int version);
        Assert.Equal(RecordingFile.VersionWithPointerShapes, version);
        var shapes = events.Where(e => e.Type == RecordingEventType.PointerShape).ToList();
        Assert.Equal(4, shapes.Count);
        Assert.Equal(2, events.Count(e => e.Type == RecordingEventType.KeyFrame));

        // Each key frame is followed by the shape as it was then.
        var afterKeyFrames = events.Select((e, i) => (e, i)).Where(p => p.e.Type == RecordingEventType.KeyFrame).Select(p => events[p.i + 1].Type);
        Assert.All(afterKeyFrames, t => Assert.Equal(RecordingEventType.PointerShape, t));

        var decoder = new ScreenDecoder();
        var seen = new List<RemotePointerShape?>();
        foreach (var e in events)
        {
            Assert.True(decoder.Apply(e));
            if (e.Type == RecordingEventType.PointerShape)
            {
                seen.Add(decoder.PointerShape);
            }
        }

        Assert.Equal(shape.Rgba, seen[0]!.Rgba);
        Assert.Same(RemotePointerShape.Hidden, seen[1]);
        Assert.Null(seen[2]);
        Assert.Null(seen[3]);
    }

    [Fact]
    public async Task A_recording_not_made_for_shapes_has_none_and_opens_where_older_ones_do()
    {
        var session = new FakePointerScreen(16, 16);
        session.SetShape(Shape());
        var recorder = new ScreenRecorder(NewWriter(pointerShapes: false), Info, 1, long.MaxValue, () => _now);
        recorder.Attach(session);
        recorder.CaptureFrame();
        recorder.Stop();
        await recorder.Completion;

        var events = Read(recorder.Path, out int version);
        Assert.Equal(RecordingFile.Version, version);
        Assert.DoesNotContain(events, e => e.Type == RecordingEventType.PointerShape);
    }
}
