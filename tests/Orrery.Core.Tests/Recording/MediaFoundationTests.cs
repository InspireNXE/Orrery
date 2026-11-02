using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using Orrery.Core.Recording;
using Orrery.Core.Tests.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Recording;

public sealed class Nv12Tests
{
    [Fact]
    public void White_black_and_red_come_out_as_bt709_in_studio_range()
    {
        // A 2×2 block each: white, black, red, side by side.
        var rgb = new byte[6 * 2 * 3];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 6; x++)
            {
                var color = x < 2 ? (255, 255, 255) : x < 4 ? (0, 0, 0) : (255, 0, 0);
                int i = (y * 6 + x) * 3;
                (rgb[i], rgb[i + 1], rgb[i + 2]) = ((byte)color.Item1, (byte)color.Item2, (byte)color.Item3);
            }
        }

        var nv12 = new byte[Nv12.Size(6, 2)];
        Nv12.FromRgb(rgb, 6, 2, nv12);

        Assert.Equal(235, nv12[0]);
        Assert.Equal(16, nv12[2]);
        Assert.InRange(nv12[4], 62, 64);              // red's Y is 63 in BT.709
        Assert.Equal([128, 128, 128, 128], nv12[12..16]); // gray has no color
        Assert.InRange(nv12[16], 101, 103);           // red's U is 102
        Assert.InRange(nv12[17], 239, 240);           // and V 240
    }

    [Fact]
    public void Odd_sizes_are_rounded_up_and_refused()
    {
        Assert.Equal((1706, 1142), Nv12.EvenSize(1705, 1141));
        Assert.Throws<ArgumentException>(() => Nv12.FromRgb(new byte[3 * 3 * 3], 3, 3, new byte[100]));
    }
}

/// <summary>Real MP4 files through this Windows' own encoders; each codec this PC has is tried.</summary>
[SupportedOSPlatform("windows")]
public sealed class MediaFoundationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly byte[] _privateKey;
    readonly byte[] _publicKey;

    public MediaFoundationTests()
    {
        Directory.CreateDirectory(_dir);
        (_privateKey, _publicKey) = RecordingTestKeys.New();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // "none" keeps the theory from having no cases on a Windows without Media Foundation.
    public static TheoryData<string> CodecNames() =>
        new(OperatingSystem.IsWindows() && MediaFoundation.Codecs() is { Count: > 0 } codecs ? codecs.Select(c => c.Name) : ["none"]);

    [WindowsFact]
    public void Windows_offers_h264_where_it_has_media_foundation()
    {
        var codecs = MediaFoundation.Codecs();
        if (codecs.Count == 0)
        {
            return; // a Windows N or Server without the Media Foundation feature
        }

        Assert.Equal("H.264", codecs[0].Name);
        Assert.False(codecs[0].HardwareOnly);
    }

    [WindowsTheory]
    [MemberData(nameof(CodecNames))]
    public void A_screen_recording_exports_as_a_playable_mp4(string name)
    {
        if (MediaFoundation.Codecs().SingleOrDefault(c => c.Name == name) is not { } codec)
        {
            return;
        }

        // 4 seconds of a 641×361 screen (odd, so it's padded to even) changing once a second.
        var path = Path.Combine(_dir, "screen.orrec");
        using (var writer = RecordingWriter.Create(path, RecordingKind.Screen, _publicKey))
        {
            const int width = 641, height = 361;
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(width, height));
            for (int second = 0; second < 4; second++)
            {
                var pixels = new byte[width * height * 3];
                for (int i = 0; i < pixels.Length; i += 3)
                {
                    pixels[i + second % 3] = (byte)(i * 7 % 251);
                }

                writer.Append(RecordingEventType.KeyFrame, TimeSpan.FromSeconds(second), ScreenFrames.EncodeKeyFrame(pixels, width, height));
            }

            writer.Complete(TimeSpan.FromSeconds(4));
        }

        var output = Path.Combine(_dir, "screen.mp4");
        using (var reader = RecordingReader.Open(path, _privateKey))
        {
            MediaFoundation.Export(reader, codec, output, framesPerSecond: 5, skipIdle: false);
        }

        var bytes = File.ReadAllBytes(output);
        var top = Boxes(bytes, 0, bytes.Length).Select(b => b.Type).ToList();
        Assert.Contains("ftyp", top);
        Assert.Contains("moov", top);
        Assert.Contains("mdat", top);

        // The sample entry names the codec, and every frame is a sample.
        var entry = codec.Name switch { "H.264" => "avc1", "H.265" => "hvc1|hev1", _ => "av01" };
        Assert.Contains(entry.Split('|'), e => IndexOf(bytes, e) >= 0);
        int stsz = IndexOf(bytes, "stsz");
        Assert.True(stsz > 0);
        using (var again = RecordingReader.Open(path, _privateKey))
        {
            Assert.Equal(ScreenExport.Frames(again, 5, 64, 64, skipIdle: false).Count(), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(stsz + 12)));
        }

        // The size is in the track header, padded to even sides, or to 16-pixel blocks for a graphics card.
        int tkhd = IndexOf(bytes, "tkhd");
        int sizeAt = tkhd + 4 + (bytes[tkhd + 4] == 1 ? 88 : 76);
        Assert.Equal(codec.HardwareOnly ? 656 : 642, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(sizeAt)) >> 16);
        Assert.Equal(codec.HardwareOnly ? 368 : 362, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(sizeAt + 4)) >> 16);
    }

    [WindowsFact]
    public void A_canceled_export_leaves_no_file()
    {
        if (MediaFoundation.Codecs().FirstOrDefault() is not { } codec)
        {
            return;
        }

        var path = Path.Combine(_dir, "screen.orrec");
        using (var writer = RecordingWriter.Create(path, RecordingKind.Screen, _publicKey))
        {
            writer.Append(RecordingEventType.Resize, TimeSpan.Zero, RecordingFile.Pair(64, 64));
            writer.Append(RecordingEventType.KeyFrame, TimeSpan.Zero, ScreenFrames.EncodeKeyFrame(new byte[64 * 64 * 3], 64, 64));
            writer.Complete(TimeSpan.FromSeconds(30));
        }

        var output = Path.Combine(_dir, "canceled.mp4");
        using var cancel = new CancellationTokenSource();
        using var reader = RecordingReader.Open(path, _privateKey);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            MediaFoundation.Export(reader, codec, output, 5, skipIdle: false, _ => cancel.Cancel(), cancel.Token));
        Assert.False(File.Exists(output));
    }

    static IEnumerable<(string Type, int Start, int Length)> Boxes(byte[] bytes, int start, int end)
    {
        for (int at = start; at + 8 <= end;)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8));
            }

            if (size < 8 || at + size > end)
            {
                yield break;
            }

            yield return (Encoding.ASCII.GetString(bytes, at + 4, 4), at, (int)size);
            at += (int)size;
        }
    }

    static int IndexOf(byte[] bytes, string type) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(type));
}
