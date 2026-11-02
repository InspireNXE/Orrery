using System.Buffers.Binary;
using System.Text;
using Orrery.Core.Recording;
using Orrery.Core.Sessions;
using Orrery.Core.Tests.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Recording;

sealed class FakeTap : IAudioTap
{
    public readonly Queue<AudioChunk> Chunks = new();
    public bool Enabled;

    public void SetEnabled(bool enabled) => Enabled = enabled;

    public AudioChunk? TryTake() => Enabled && Chunks.Count > 0 ? Chunks.Dequeue() : null;

    public bool Muted { get; set; }

    public TimeSpan? SoundAge { get; set; }
}

/// <summary>A session that only plays sound, which is all the recorder's sound needs.</summary>
sealed class FakeSoundSession : IConnectionSession, IAudioSession
{
    public FakeTap Tap { get; } = new();
    public IAudioTap? AudioTap => Tap;
    public ConnectionTarget Target { get; } = new("desk", 3389, "user", null);
    public SessionState State => SessionState.Connected;
    public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
    public Task DisconnectAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SoundTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _privateKey;
    readonly byte[] _publicKey;
    TimeSpan _now;

    public SoundTests()
    {
        Directory.CreateDirectory(_dir);
        (_privateKey, _publicKey) = RecordingTestKeys.New();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static readonly RecordingInfo Info = new("desk", "RDP", DateTimeOffset.UnixEpoch);

    static byte[] Tone(int frames, int channels, short level = 1000)
    {
        var pcm = new byte[frames * channels * 2];
        for (int i = 0; i < frames * channels; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(i % 2 == 0 ? level : -level));
        }

        return pcm;
    }

    ScreenRecorder NewRecorder(bool recordSound) => new(
        RecordingWriter.Create(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".orrec"), RecordingKind.Screen, _publicKey),
        Info, 1, long.MaxValue, () => _now, recordSound);

    List<RecordedEvent> Read(string path)
    {
        using var reader = RecordingReader.Open(path, _privateKey);
        return reader.Events().ToList();
    }

    [Fact]
    public async Task Sound_is_recorded_while_attached_and_silence_is_left_out()
    {
        var session = new FakeSoundSession();
        var recorder = NewRecorder(recordSound: true);
        recorder.Attach(session);
        Assert.True(session.Tap.Enabled);

        session.Tap.Chunks.Enqueue(new AudioChunk(48_000, 2, Tone(960, 2)));
        session.Tap.Chunks.Enqueue(new AudioChunk(48_000, 2, new byte[960 * 4]));
        _now = TimeSpan.FromSeconds(1);
        recorder.CaptureSound();

        recorder.Detach();
        Assert.False(session.Tap.Enabled);
        recorder.Stop();
        await recorder.Completion;

        var sound = Assert.Single(Read(recorder.Path), e => e.Type == RecordingEventType.Audio);
        Assert.Equal(TimeSpan.FromSeconds(1), sound.Time);
        var (rate, channels, pcm) = RecordingFile.ReadAudio(sound.Payload)!.Value;
        Assert.Equal((48_000, 2), (rate, channels));
        Assert.Equal(Tone(960, 2), pcm.ToArray());
    }

    [Fact]
    public async Task Sound_is_left_alone_when_the_setting_is_off()
    {
        var session = new FakeSoundSession();
        var recorder = NewRecorder(recordSound: false);
        recorder.Attach(session);
        Assert.False(session.Tap.Enabled);
        recorder.Stop();
        await recorder.Completion;
        Assert.DoesNotContain(Read(recorder.Path), e => e.Type == RecordingEventType.Audio);
    }

    [Fact]
    public async Task Sound_can_be_turned_on_and_off_while_recording_and_each_change_is_marked()
    {
        var session = new FakeSoundSession();
        var recorder = NewRecorder(recordSound: false);
        recorder.Attach(session);
        Assert.True(recorder.CanRecordSound);
        Assert.False(session.Tap.Enabled);

        recorder.RecordsSound = true;
        Assert.True(session.Tap.Enabled);
        session.Tap.Chunks.Enqueue(new AudioChunk(48_000, 2, Tone(960, 2)));
        _now = TimeSpan.FromSeconds(1);
        recorder.CaptureSound();

        _now = TimeSpan.FromSeconds(2);
        recorder.RecordsSound = false;
        Assert.False(session.Tap.Enabled);
        recorder.Stop();
        await recorder.Completion;

        var events = Read(recorder.Path);
        Assert.Single(events, e => e.Type == RecordingEventType.Audio);
        Assert.Equal(["Sound on", "Sound off"], events.Where(e => e.Type == RecordingEventType.Marker).Select(e => Encoding.UTF8.GetString(e.Payload)));
    }

    [Fact]
    public async Task A_session_without_sound_has_none_to_record_and_the_frame_rate_can_change()
    {
        var recorder = NewRecorder(recordSound: true);
        Assert.False(recorder.CanRecordSound); // nothing attached yet
        Assert.Equal(1, recorder.FramesPerSecond);
        recorder.FramesPerSecond = 10;
        Assert.Equal(10, recorder.FramesPerSecond);
        recorder.FramesPerSecond = 99;
        Assert.Equal(ScreenRecorder.MaxFramesPerSecond, recorder.FramesPerSecond);
        recorder.Stop();
        await recorder.Completion;
        recorder.FramesPerSecond = 5; // after stopping, still fine
    }

    [Fact]
    public void Sound_that_is_not_whole_frames_or_out_of_range_is_refused()
    {
        Assert.Null(RecordingFile.ReadAudio(RecordingFile.AudioPayload(48_000, 2, new byte[3])));
        Assert.Null(RecordingFile.ReadAudio(RecordingFile.AudioPayload(1_000, 2, new byte[4])));
        Assert.Null(RecordingFile.ReadAudio(RecordingFile.AudioPayload(48_000, 9, new byte[18])));
        Assert.NotNull(RecordingFile.ReadAudio(RecordingFile.AudioPayload(22_050, 1, new byte[2])));
    }

    [Fact]
    public void A_sound_track_follows_on_through_jitter_and_fills_real_gaps_with_silence()
    {
        var track = new SoundTrack(new SoundFormat(48_000, 2));
        var piece = RecordingFile.AudioPayload(48_000, 2, Tone(960, 2)); // 20 ms

        // Two pieces taken together share a stamp: the second follows the first.
        track.Add(TimeSpan.Zero, piece);
        track.Add(TimeSpan.Zero, piece);
        Assert.Equal(1920, track.Length);

        // 60 ms later than where the sound has got to is within the slack; a second later is a gap.
        track.Add(TimeSpan.FromMilliseconds(100), piece);
        Assert.Equal(2880, track.Length);
        track.Add(TimeSpan.FromSeconds(2), piece);
        Assert.Equal(96_000 + 960, track.Length);

        var taken = track.Take();
        Assert.Equal((96_000 + 960) * 4, taken.Length);
        Assert.True(taken.AsSpan(2880 * 4, 1000).IndexOfAnyExcept((byte)0) < 0);
        Assert.Empty(track.Take());
    }

    [Fact]
    public void Sound_is_converted_to_the_track_format()
    {
        var mono = Tone(2205, 1); // 100 ms at 22.05 kHz
        var stereo = SoundTrack.Convert(mono, 22_050, 1, new SoundFormat(48_000, 2));
        Assert.Equal(4800 * 4, stereo.Length);
        Assert.Equal(BinaryPrimitives.ReadInt16LittleEndian(stereo), BinaryPrimitives.ReadInt16LittleEndian(stereo.AsSpan(2)));

        var six = Tone(480, 6);
        Assert.Equal(480 * 4, SoundTrack.Convert(six, 48_000, 6, new SoundFormat(48_000, 2)).Length);
    }

    string ScreenWithSound(int seconds)
    {
        var path = Path.Combine(_dir, "sound.orrec");
        using var writer = RecordingWriter.Create(path, RecordingKind.Screen, _publicKey);
        const int width = 64, height = 48;
        writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(width, height));
        writer.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(new byte[width * height * 3], width, height));
        for (int ms = 0; ms < seconds * 1000; ms += 20)
        {
            writer.Append(RecordingEventType.Audio, TimeSpan.FromMilliseconds(ms), RecordingFile.AudioPayload(44_100, 2, Tone(882, 2)));
        }

        writer.Complete(TimeSpan.FromSeconds(seconds));
        return path;
    }

    [Fact]
    public void A_screen_export_carries_the_whole_sound_track()
    {
        var path = ScreenWithSound(2);
        using var reader = RecordingReader.Open(path, _privateKey);
        var format = SoundFormat.Of(reader);
        Assert.Equal(new SoundFormat(44_100, 2), format);
        long bytes = ScreenExport.Frames(reader, 5, 64, 48, skipIdle: true, sound: format).Sum(f => (long)f.Sound.Length);
        Assert.Equal(2 * 44_100 * 4, bytes);
    }

    [Fact]
    public void An_avi_with_sound_has_a_pcm_stream_and_indexes_its_chunks()
    {
        var sound = new SoundFormat(48_000, 2);
        using var stream = new MemoryStream();
        using (var avi = new AviWriter(stream, 32, 32, 5, sound))
        {
            avi.AddFrame([0xFF, 0xD8, 0xFF, 0xD9]);
            avi.AddSound(Tone(9600, 2));
            avi.AddFrame([], repeat: true);
            avi.AddSound(Tone(9600, 2));
            avi.Finish();
        }

        var bytes = stream.ToArray();
        var text = Encoding.ASCII.GetString(bytes);
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(text.IndexOf("avih", StringComparison.Ordinal) + 8 + 24)));

        int auds = text.IndexOf("auds", StringComparison.Ordinal);
        Assert.True(auds > 0);
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(auds + 20)));       // scale: a sample frame
        Assert.Equal(192_000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(auds + 24))); // rate: bytes a second
        Assert.Equal(19_200, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(auds + 32))); // length in sample frames

        int idx1 = text.IndexOf("idx1", StringComparison.Ordinal);
        Assert.Equal(4 * 16, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(idx1 + 4)));
        Assert.Equal(2, CountOf(text[idx1..], "01wb"));
    }

    [WindowsFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void A_windows_mp4_export_has_an_aac_track()
    {
        if (MediaFoundation.Codecs().FirstOrDefault(c => !c.HardwareOnly) is not { } codec)
        {
            return;
        }

        var path = ScreenWithSound(3);
        var output = Path.Combine(_dir, "sound.mp4");
        using (var reader = RecordingReader.Open(path, _privateKey))
        {
            MediaFoundation.Export(reader, codec, output, framesPerSecond: 5, skipIdle: false);
        }

        var text = Encoding.ASCII.GetString(File.ReadAllBytes(output));
        Assert.Equal(2, CountOf(text, "trak"));
        Assert.Contains("mp4a", text);
        Assert.Contains("soun", text);
    }

    static int CountOf(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
