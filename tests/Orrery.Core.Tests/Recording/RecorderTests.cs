using System.Runtime.InteropServices;
using System.Text;
using Orrery.Core.Recording;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Recording;

sealed class FakeTerminal : ITerminalSession
{
    public ConnectionTarget Target { get; } = new("host", 22, "user", null);
    public SessionState State { get; private set; } = SessionState.Connected;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event Action<byte[]>? DataReceived;
    public List<byte[]> Sent { get; } = [];

    public void Output(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
    public void Output(byte[] bytes) => DataReceived?.Invoke(bytes);

    public void Drop()
    {
        State = SessionState.Failed;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(SessionState.Failed, "gone"));
    }

    public void Send(ReadOnlySpan<byte> data) => Sent.Add(data.ToArray());
    public void Resize(int columns, int rows) { }
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeScreen(int width, int height) : IFrameBufferSession
{
    public byte[] Frame = new byte[width * height * 4];
    public int Width { get; private set; } = width;
    public int Height { get; private set; } = height;
    public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
    public SessionState State => SessionState.Connected;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
    public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;

    public void Paint(FrameRect rect, byte r, byte g, byte b)
    {
        for (int y = rect.Y; y < rect.Bottom; y++)
        {
            for (int x = rect.X; x < rect.Right; x++)
            {
                int at = (y * Width + x) * 4;
                (Frame[at], Frame[at + 1], Frame[at + 2], Frame[at + 3]) = (r, g, b, 255);
            }
        }

        FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(rect, resized: false));
    }

    public void ResizeTo(int width2, int height2)
    {
        (Width, Height) = (width2, height2);
        Frame = new byte[width2 * height2 * 4];
        FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, width2, height2), resized: true));
    }

    public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride)
    {
        for (int row = region.Y; row < Math.Min(region.Bottom, Height); row++)
        {
            Marshal.Copy(Frame, (row * Width + region.X) * 4, destination + row * destinationStride + region.X * 4, (Math.Min(region.Right, Width) - region.X) * 4);
        }
    }

    public void SendKey(ushort scancode, bool pressed) { }
    public void ReleaseAllKeys() { }
    public void SendPointerMove(int x, int y) { }
    public void SendPointerButton(PointerButton button, bool pressed) { }
    public void SendWheel(int units, bool horizontal) { }
    public void RequestResize(int width2, int height2) { }
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class RecorderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _privateKey;
    readonly byte[] _publicKey;
    TimeSpan _now;

    public RecorderTests()
    {
        Directory.CreateDirectory(_dir);
        (_privateKey, _publicKey) = RecordingTestKeys.New();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static readonly RecordingInfo Info = new("web-01", "SSH", DateTimeOffset.UnixEpoch);

    RecordingWriter NewWriter(RecordingKind kind) => RecordingWriter.Create(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".orrec"), kind, _publicKey);

    List<RecordedEvent> Read(string path, out RecordingEnding ending)
    {
        using var reader = RecordingReader.Open(path, _privateKey);
        var events = reader.Events().ToList();
        ending = reader.Ending;
        return events;
    }

    [Fact]
    public async Task Terminal_output_and_resizes_are_recorded_with_their_times_and_input_never_by_default()
    {
        var session = new FakeTerminal();
        var recorder = new TerminalRecorder(NewWriter(RecordingKind.Terminal), Info, 80, 24, long.MaxValue, () => _now);
        recorder.Attach(session);

        _now = TimeSpan.FromSeconds(1);
        session.Output("$ ls\r\n");
        session.Send("password\r"u8);
        _now = TimeSpan.FromSeconds(2);
        recorder.Resized(100, 30);
        session.Output([0xC3]); // half a UTF-8 character, kept as it came
        recorder.Stop();
        await recorder.Completion;

        var events = Read(recorder.Path, out var ending);
        Assert.Equal(RecordingEnding.Closed, ending);
        Assert.Equal(Info, RecordingFile.ReadInfo(events[0].Payload));
        Assert.Equal((80, 24), RecordingFile.ReadPair(events[1].Payload));
        var outputs = events.Where(e => e.Type == RecordingEventType.Output).ToList();
        Assert.Equal("$ ls\r\n", Encoding.UTF8.GetString(outputs[0].Payload));
        Assert.Equal(TimeSpan.FromSeconds(1), outputs[0].Time);
        Assert.Equal([0xC3], outputs[1].Payload);
        Assert.Contains(events, e => e.Type == RecordingEventType.Resize && RecordingFile.ReadPair(e.Payload) == (100, 30));
        Assert.DoesNotContain(events, e => Encoding.UTF8.GetString(e.Payload).Contains("password"));
    }

    [Fact]
    public async Task A_reconnect_continues_the_same_file_with_markers()
    {
        var first = new FakeTerminal();
        var second = new FakeTerminal();
        var recorder = new TerminalRecorder(NewWriter(RecordingKind.Terminal), Info, 80, 24, long.MaxValue, () => _now);
        recorder.Attach(first);
        first.Output("before");
        first.Drop();
        recorder.Attach(second);
        first.Output("from the old session, after it was replaced");
        second.Output("after");
        recorder.Stop();
        await recorder.Completion;

        var events = Read(recorder.Path, out _);
        var text = events.Where(e => e.Type is RecordingEventType.Output or RecordingEventType.Marker).Select(e => Encoding.UTF8.GetString(e.Payload)).ToList();
        Assert.Equal(["before", "Connection lost", "Reconnected", "after"], text);
    }

    [Fact]
    public async Task The_size_limit_stops_the_recording_and_says_why()
    {
        var session = new FakeTerminal();
        var recorder = new TerminalRecorder(NewWriter(RecordingKind.Terminal), Info, 80, 24, maxBytes: 100_000, () => _now);
        var stopped = new TaskCompletionSource();
        recorder.Stopped += (_, _) => stopped.TrySetResult();
        recorder.Attach(session);
        for (int i = 0; i < 50; i++)
        {
            session.Output(new string('x', 10_000));
        }

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(recorder.IsRecording);
        Assert.Contains("size limit", recorder.StopReason);
        Read(recorder.Path, out var ending);
        Assert.Equal(RecordingEnding.Closed, ending);
    }

    [Fact]
    public async Task A_screen_records_a_key_frame_then_only_what_changed()
    {
        var screen = new FakeScreen(64, 48);
        screen.Paint(new FrameRect(0, 0, 64, 48), 10, 20, 30);
        var recorder = new ScreenRecorder(NewWriter(RecordingKind.Screen), Info with { Protocol = "RDP" }, 1, long.MaxValue, () => _now);
        recorder.Attach(screen);
        recorder.CaptureFrame();

        screen.Paint(new FrameRect(5, 6, 4, 3), 200, 100, 50);
        screen.Paint(new FrameRect(50, 40, 2, 2), 1, 2, 3);
        recorder.CaptureFrame();
        recorder.CaptureFrame(); // nothing changed: nothing written
        recorder.Stop();
        await recorder.Completion;

        var events = Read(recorder.Path, out _);
        Assert.Single(events, e => e.Type == RecordingEventType.KeyFrame);
        Assert.Single(events, e => e.Type == RecordingEventType.Change);

        var decoder = new ScreenDecoder();
        foreach (var e in events)
        {
            Assert.True(decoder.Apply(e));
        }

        AssertPixel(decoder, 0, 0, (10, 20, 30));
        AssertPixel(decoder, 6, 7, (200, 100, 50));
        AssertPixel(decoder, 51, 41, (1, 2, 3));
        AssertPixel(decoder, 9, 6, (10, 20, 30));
    }

    [Fact]
    public async Task A_screen_that_changes_a_lot_gets_a_key_frame_once_the_changes_outweigh_the_last_one()
    {
        var screen = new FakeScreen(32, 32);
        var recorder = new ScreenRecorder(NewWriter(RecordingKind.Screen), Info, 1, long.MaxValue, () => _now);
        recorder.Attach(screen);
        recorder.CaptureFrame();
        for (int i = 0; i < 40; i++)
        {
            screen.Paint(new FrameRect(0, 0, 32, 32), (byte)(i * 37), (byte)(i * 11), (byte)i);
            recorder.CaptureFrame(); // all in the same second, so only the amount of change makes key frames
        }

        recorder.Stop();
        await recorder.Completion;

        var frames = Read(recorder.Path, out _).Where(e => e.Type is RecordingEventType.KeyFrame or RecordingEventType.Change).ToList();
        Assert.True(frames.Count(e => e.Type == RecordingEventType.KeyFrame) > 1);
        // A change comes only while the changes since the last key frame are under the limit, so the file stays small.
        long keyFrame = 0, changes = 0;
        foreach (var e in frames)
        {
            if (e.Type == RecordingEventType.KeyFrame)
            {
                keyFrame = e.Payload.Length;
                changes = 0;
                continue;
            }

            Assert.True(changes < keyFrame * ScreenRecorder.KeyFrameAfterChanges);
            changes += e.Payload.Length;
        }
    }

    [Fact]
    public async Task A_resize_and_the_pointer_are_recorded()
    {
        var screen = new FakeScreen(32, 32);
        var recorder = new ScreenRecorder(NewWriter(RecordingKind.Screen), Info, 1, long.MaxValue, () => _now) { Pointer = () => (3, 4) };
        recorder.Attach(screen);
        recorder.CaptureFrame();
        screen.ResizeTo(40, 20);
        screen.Paint(new FrameRect(0, 0, 40, 20), 9, 9, 9);
        recorder.CaptureFrame();
        recorder.Stop();
        await recorder.Completion;

        var events = Read(recorder.Path, out _);
        Assert.Equal(2, events.Count(e => e.Type == RecordingEventType.KeyFrame));
        Assert.Contains(events, e => e.Type == RecordingEventType.Resize && RecordingFile.ReadPair(e.Payload) == (40, 20));
        Assert.Single(events, e => e.Type == RecordingEventType.Pointer);

        var decoder = new ScreenDecoder();
        events.ForEach(e => decoder.Apply(e));
        Assert.Equal((40, 20), (decoder.Width, decoder.Height));
        Assert.Equal((3, 4), decoder.Pointer);
        AssertPixel(decoder, 39, 19, (9, 9, 9));
    }

    [Fact]
    public void Damage_close_together_merges_and_far_apart_stays_apart()
    {
        var damage = new DamageList();
        damage.Add(new FrameRect(0, 0, 10, 10));
        damage.Add(new FrameRect(12, 0, 10, 10));
        damage.Add(new FrameRect(500, 500, 10, 10));

        Assert.Equal(2, damage.Rectangles.Count);
        Assert.Contains(new FrameRect(0, 0, 22, 10), damage.Rectangles);

        for (int i = 0; i < 40; i++)
        {
            damage.Add(new FrameRect(i * 100, 1000, 1, 1));
        }

        Assert.True(damage.Rectangles.Count <= 32);
    }

    [Theory]
    [InlineData(-1, 0, 4, 4)]
    [InlineData(30, 0, 4, 4)]
    [InlineData(0, 0, 0, 4)]
    public void A_change_outside_the_screen_is_refused(int x, int y, int w, int h)
    {
        var decoder = new ScreenDecoder();
        Assert.True(decoder.Apply(Event(RecordingEventType.KeyFrame, ScreenFrames.EncodeKeyFrame(new byte[32 * 32 * 3], 32, 32))));

        var change = ScreenFrames.EncodeChange([new FrameRect(x, y, w, h)], new byte[Math.Max(0, w * h * 3)]);
        Assert.False(decoder.Apply(Event(RecordingEventType.Change, change)));
    }

    [Fact]
    public void A_frame_that_decompresses_to_more_than_it_says_is_refused()
    {
        var decoder = new ScreenDecoder();
        var tooBig = ScreenFrames.EncodeKeyFrame(new byte[33 * 32 * 3], 32, 32);
        Assert.False(decoder.Apply(Event(RecordingEventType.KeyFrame, tooBig)));
        Assert.False(decoder.Apply(Event(RecordingEventType.KeyFrame, ScreenFrames.EncodeKeyFrame([], ScreenFrames.MaxSide + 1, 1))));
    }

    static RecordedEvent Event(RecordingEventType type, byte[] payload) => new(type, TimeSpan.Zero, payload, default);

    static void AssertPixel(ScreenDecoder decoder, int x, int y, (byte R, byte G, byte B) expected)
    {
        int at = (y * decoder.Width + x) * 3;
        Assert.Equal(expected, (decoder.Rgb[at], decoder.Rgb[at + 1], decoder.Rgb[at + 2]));
    }
}
