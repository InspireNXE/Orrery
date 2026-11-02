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
