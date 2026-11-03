using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Orrery.Core.Recording;
using Xunit;

namespace Orrery.Core.Tests.Recording;

public sealed class RecordingExportTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _privateKey;
    readonly byte[] _publicKey;

    public RecordingExportTests()
    {
        Directory.CreateDirectory(_dir);
        (_privateKey, _publicKey) = RecordingTestKeys.New();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Make(RecordingKind kind, Action<RecordingWriter> fill)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".orrec");
        using var writer = RecordingWriter.Create(path, kind, _publicKey);
        fill(writer);
        writer.Complete(TimeSpan.FromSeconds(10));
        return path;
    }

    [Fact]
    public void A_cast_export_holds_the_output_resizes_and_markers()
    {
        var path = Make(RecordingKind.Terminal, w =>
        {
            w.Append(RecordingEventType.Info, TimeSpan.Zero, RecordingFile.InfoPayload(new RecordingInfo("web-01", "SSH", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))));
            w.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(100, 30));
            w.Append(RecordingEventType.Output, TimeSpan.FromMilliseconds(1500), [(byte)'h', 0xC3]);
            w.Append(RecordingEventType.Output, TimeSpan.FromMilliseconds(1600), [0xA9, (byte)'\n']); // é split across two events
            w.Append(RecordingEventType.Resize, TimeSpan.FromSeconds(2), RecordingFile.Pair(120, 40));
            w.Append(RecordingEventType.Marker, TimeSpan.FromSeconds(3), "Reconnected"u8);
        });

        using var reader = RecordingReader.Open(path, _privateKey);
        var text = new StringWriter();
        Assert.Equal(RecordingEnding.Closed, AsciicastExport.Write(reader, text));

        var lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var header = JsonDocument.Parse(lines[0]);
        Assert.Equal(2, header.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(100, header.RootElement.GetProperty("width").GetInt32());
        Assert.Equal(30, header.RootElement.GetProperty("height").GetInt32());
        Assert.Equal("web-01", header.RootElement.GetProperty("title").GetString());
        Assert.Equal(1_700_000_000, header.RootElement.GetProperty("timestamp").GetInt64());

        var events = lines.Skip(1).Select(l => JsonSerializer.Deserialize<JsonElement[]>(l)!).ToList();
        Assert.Equal(1.5, events[0][0].GetDouble());
        Assert.Equal("o", events[0][1].GetString());
        Assert.Equal("h", events[0][2].GetString());
        Assert.Equal("é\n", events[1][2].GetString());
        Assert.Equal("r 120x40", $"{events[2][1].GetString()} {events[2][2].GetString()}");
        Assert.Equal("m Reconnected", $"{events[3][1].GetString()} {events[3][2].GetString()}");
    }

    [Fact]
    public void A_screen_recording_steps_at_a_steady_rate_and_skips_idle_time()
    {
        var path = Make(RecordingKind.Screen, w =>
        {
            w.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(4, 4));
            w.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(new byte[4 * 4 * 3], 4, 4));
            w.Append(RecordingEventType.Change, TimeSpan.FromSeconds(60), ScreenFrames.EncodeChange([new(0, 0, 1, 1)], [255, 0, 0]));
        });

        using var reader = RecordingReader.Open(path, _privateKey);
        var frames = ScreenExport.Frames(reader, 2, 4, 4, skipIdle: true).Select(f => (f.Time, f.Changed, Red: f.Rgb[0])).ToList();

        // 60 s idle plays as 2 s: about 4 frames at 2 a second, then the change.
        Assert.InRange(frames.Count, 4, 7);
        Assert.True(frames[0].Changed);
        Assert.All(frames.Skip(1).SkipLast(1), f => Assert.False(f.Changed));
        Assert.Equal(255, frames[^1].Red);

        using var again = RecordingReader.Open(path, _privateKey);
        Assert.InRange(ScreenExport.Frames(again, 2, 4, 4, skipIdle: false).Count(), 120, 123);
    }

    [Fact]
    public void An_avi_has_its_sizes_counts_and_index_filled_in()
    {
        var stream = new MemoryStream();
        using (var avi = new AviWriter(stream, 320, 200, 5))
        {
            avi.AddFrame([1, 2, 3]);
            avi.AddFrame([], repeat: true);
            avi.AddFrame([4, 5, 6, 7]);
        }

        var bytes = stream.ToArray();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal("AVI ", Encoding.ASCII.GetString(bytes, 8, 4));

        int avih = IndexOf(bytes, "avih");
        Assert.Equal(200_000, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(avih + 8)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(avih + 8 + 16)));
        Assert.Equal(320, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(avih + 8 + 32)));

        int movi = IndexOf(bytes, "movi");
        int idx1 = IndexOf(bytes, "idx1");
        Assert.Equal(48, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(idx1 + 4)));
        // Each index entry points at its chunk, counted from the "movi" fourcc.
        for (int i = 0; i < 3; i++)
        {
            int entry = idx1 + 8 + i * 16;
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entry + 8));
            Assert.Equal("00dc", Encoding.ASCII.GetString(bytes, movi + offset, 4));
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entry + 12)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(movi + offset + 4)));
        }

        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(idx1 + 8 + 16 + 4))); // the repeat isn't a key frame
    }

    [Fact]
    public void Ffmpeg_is_started_with_a_list_of_arguments_and_a_full_output_path()
    {
        var start = Ffmpeg.ExportStart(@"C:\Tools\ffmpeg.exe", new FfmpegCodec("H.264", "libx264"), 1919, 1080, 5, "-y out.mp4");

        Assert.False(start.UseShellExecute);
        Assert.Empty(start.Arguments);
        Assert.Contains("libx264", start.ArgumentList);
        Assert.Contains("1919x1080", start.ArgumentList);
        var output = start.ArgumentList[^1];
        Assert.True(Path.IsPathFullyQualified(output));
        Assert.False(output.StartsWith('-'));
    }

    [Fact]
    public void Ffmpeg_takes_sound_from_the_pipe_as_a_second_input()
    {
        var start = Ffmpeg.ExportStart(@"C:\Tools\ffmpeg.exe", new FfmpegCodec("H.264", "libx264"), 640, 480, 5, "out.mp4",
            new SoundFormat(44_100, 2), @"\\.\pipe\orrery-sound-1");

        var arguments = start.ArgumentList.ToList();
        int second = arguments.LastIndexOf("-i");
        Assert.Equal(@"\\.\pipe\orrery-sound-1", arguments[second + 1]);
        Assert.Equal(["-f", "s16le", "-ar", "44100", "-ac", "2"], arguments[(second - 6)..second]);
        Assert.Contains("1:a", arguments);
        Assert.Contains("aac", arguments);
        Assert.True(Path.IsPathFullyQualified(arguments[^1]));
    }

    [Fact]
    public void Steady_sound_runs_on_in_silence_two_frames_ahead()
    {
        var tone = new byte[4_800 * 2]; // 0.1 s of mono at 48 kHz
        var path = Make(RecordingKind.Screen, w =>
        {
            w.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(4, 4));
            w.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(new byte[4 * 4 * 3], 4, 4));
            w.Append(RecordingEventType.Audio, TimeSpan.Zero, RecordingFile.AudioPayload(48_000, 1, tone));
            w.Append(RecordingEventType.Change, TimeSpan.FromSeconds(3), ScreenFrames.EncodeChange([new(0, 0, 1, 1)], [255, 0, 0]));
        });

        var format = new SoundFormat(48_000, 1);
        using var reader = RecordingReader.Open(path, _privateKey);
        long laid = 0;
        foreach (var frame in ScreenExport.Frames(reader, 10, 4, 4, skipIdle: false, sound: format, steadySound: true))
        {
            laid += frame.Sound.Length / format.BlockAlign;
            Assert.True(laid >= (long)((frame.Time.TotalSeconds + 0.2) * format.SampleRate) - 1, $"short at {frame.Time}");
        }

        // Without it, a quiet stretch is laid only once it's over a second long, and never ahead of the picture.
        using var again = RecordingReader.Open(path, _privateKey);
        long plain = 0;
        var end = TimeSpan.Zero;
        foreach (var frame in ScreenExport.Frames(again, 10, 4, 4, skipIdle: false, sound: format))
        {
            plain += frame.Sound.Length / format.BlockAlign;
            end = frame.Time;
            Assert.True(plain <= Math.Max(4_800, (long)(frame.Time.TotalSeconds * format.SampleRate)), $"ahead at {frame.Time}");
        }

        Assert.True(plain >= (long)((end.TotalSeconds - 1.1) * format.SampleRate), $"{plain} short of {end}");
    }

    [Fact]
    public void An_hour_between_sounds_goes_out_a_second_at_a_time()
    {
        var beep = new byte[480 * 2]; // 10 ms of mono at 48 kHz
        var path = Make(RecordingKind.Screen, w =>
        {
            w.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(4, 4));
            w.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(new byte[4 * 4 * 3], 4, 4));
            w.Append(RecordingEventType.Audio, TimeSpan.Zero, RecordingFile.AudioPayload(48_000, 1, beep));
            w.Append(RecordingEventType.Audio, TimeSpan.FromHours(1), RecordingFile.AudioPayload(48_000, 1, beep));
        });

        var format = new SoundFormat(48_000, 1);
        using var reader = RecordingReader.Open(path, _privateKey);
        int largest = 0;
        foreach (var frame in ScreenExport.Frames(reader, 1, 4, 4, skipIdle: false, sound: format))
        {
            largest = Math.Max(largest, frame.Sound.Length);
        }

        Assert.True(largest <= 3 * format.BytesPerSecond, $"{largest:N0} bytes in one frame");
    }

    [FfmpegFact]
    public void An_ffmpeg_export_has_the_sound_through_a_long_silence()
    {
        var tone = new byte[4_410 * 4]; // 0.1 s of stereo at 44.1 kHz
        for (int i = 0; i < tone.Length; i += 2)
        {
            BinaryPrimitives.WriteInt16LittleEndian(tone.AsSpan(i), (short)(i % 200 * 100));
        }

        var path = Make(RecordingKind.Screen, w =>
        {
            w.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(64, 48));
            w.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(new byte[64 * 48 * 3], 64, 48));
            for (int i = 0; i < 5; i++)
            {
                w.Append(RecordingEventType.Audio, TimeSpan.FromMilliseconds(100 * i), RecordingFile.AudioPayload(44_100, 2, tone));
            }

            w.Append(RecordingEventType.Change, TimeSpan.FromSeconds(8), ScreenFrames.EncodeChange([new(0, 0, 1, 1)], [255, 0, 0]));
        });

        var output = Path.Combine(_dir, "out.mp4");
        var ffmpeg = FfmpegFactAttribute.Path!;
        using var reader = RecordingReader.Open(path, _privateKey);
        var sound = SoundFormat.Of(reader)!;
        using var pipe = new SoundPipe();
        using var process = System.Diagnostics.Process.Start(Ffmpeg.ExportStart(ffmpeg, new FfmpegCodec("MPEG-4", "mpeg4"), 64, 48, 25, output, sound, pipe.Path))!;
        var errors = process.StandardError.ReadToEndAsync();
        pipe.Start();
        try
        {
            foreach (var frame in ScreenExport.Frames(reader, 25, 64, 48, skipIdle: false, sound: sound, steadySound: true))
            {
                process.StandardInput.BaseStream.Write(frame.Rgb);
                pipe.Write(frame.Sound);
            }

            pipe.Complete();
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // ffmpeg stopped reading: its message is checked below.
        }

        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "ffmpeg didn't finish");
        Assert.True(process.ExitCode == 0, errors.Result);

        var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg, ["-hide_banner", "-i", output]) { RedirectStandardError = true })!;
        var info = probe.StandardError.ReadToEnd();
        probe.WaitForExit();
        Assert.Contains("Audio: aac", info);
        Assert.Contains("44100 Hz, stereo", info);
        Assert.Contains("Duration: 00:00:10.", info); // the recording ends at 10 s, after 9.5 s of silence
    }

    [Fact]
    public void Codecs_are_found_in_an_encoders_listing()
    {
        const string Listing = """
            Encoders:
             V..... = Video
             ------
             V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
             V....D hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)
             A....D aac                  AAC (Advanced Audio Coding)
            """;

        var codecs = Ffmpeg.ParseCodecs(Listing);
        Assert.Equal([new FfmpegCodec("H.264", "libx264"), new FfmpegCodec("H.265", "hevc_nvenc")], codecs);
    }

    [Fact]
    public async Task Ffmpeg_is_only_taken_from_a_full_path_that_exists()
    {
        Assert.Null(await Ffmpeg.ProbeAsync("ffmpeg"));
        Assert.Null(await Ffmpeg.ProbeAsync(Path.Combine(_dir, "missing.exe")));
    }

    [Fact]
    public void Retention_deletes_old_then_oldest_and_never_what_is_in_use()
    {
        var now = DateTime.UtcNow;
        string Recording(string name, int days, int size)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, new byte[size]);
            File.SetLastWriteTimeUtc(path, now - TimeSpan.FromDays(days));
            return path;
        }

        var ancient = Recording("ancient.orrec", 100, 10);
        var old = Recording("old.orrec", 20, 600);
        var middle = Recording("middle.orrec", 10, 600);
        var fresh = Recording("fresh.orrec", 1, 600);
        var busy = Recording("busy.orrec", 200, 10);
        var other = Recording("notes.txt", 400, 10);

        var deleted = RecordingRetention.Apply(_dir, TimeSpan.FromDays(30), 1300, now, new HashSet<string> { busy });

        Assert.Equal([ancient, old], deleted.Order());
        Assert.True(File.Exists(middle) && File.Exists(fresh) && File.Exists(busy) && File.Exists(other));
    }

    static int IndexOf(byte[] bytes, string fourcc)
    {
        var code = Encoding.ASCII.GetBytes(fourcc);
        return bytes.AsSpan().IndexOf(code);
    }
}

/// <summary>Runs where an ffmpeg can be found, from ORRERY_TEST_FFMPEG or on PATH: shows as skipped elsewhere.</summary>
sealed class FfmpegFactAttribute : FactAttribute
{
    public static readonly string? Path = Find();

    public FfmpegFactAttribute()
    {
        if (Path is null)
        {
            Skip = "No ffmpeg found";
        }
    }

    static string? Find()
    {
        if (Environment.GetEnvironmentVariable("ORRERY_TEST_FFMPEG") is { Length: > 0 } set && File.Exists(set))
        {
            return set;
        }

        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => System.IO.Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }
}
