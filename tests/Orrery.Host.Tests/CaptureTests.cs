using System.Buffers.Binary;
using Orrery.Host.Encoding;
using Orrery.Streaming;
using Xunit;

namespace Orrery.Host.Tests;

public class CaptureTests
{
    [Fact]
    public void No_options_capture_the_first_monitor_in_AV1_for_ten_seconds()
    {
        var options = CaptureOptions.Parse([], out var error);
        Assert.Null(error);
        Assert.Equal(new CaptureOptions(0, VideoCodec.Av1, 10, null, null), options);
    }

    [Fact]
    public void Every_option_is_read()
    {
        var options = CaptureOptions.Parse(["--monitor", "1", "--codec", "HEVC", "--seconds", "3", "--mbps", "40", "--loopback", "--out", "a.bin"], out _);
        Assert.Equal(new CaptureOptions(1, VideoCodec.Hevc, 3, 40_000_000, "a.bin", Loopback: true), options);
    }

    [Theory]
    [InlineData(new[] { "--codec", "vp9" }, "--codec vp9 isn't valid.")]
    [InlineData(new[] { "--seconds", "0" }, "--seconds 0 isn't valid.")]
    [InlineData(new[] { "--monitor" }, "Unknown option --monitor.")]
    [InlineData(new[] { "--fps", "60" }, "Unknown option --fps.")]
    public void Bad_options_say_what_is_wrong(string[] args, string expected)
    {
        Assert.Null(CaptureOptions.Parse(args, out var error));
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData(1920, 1080, 60, 15_552_000)]
    [InlineData(3840, 2160, 120, 124_416_000)]
    [InlineData(1280, 720, 30, 10_000_000)]
    [InlineData(7680, 4320, 120, 150_000_000)]
    public void The_default_bitrate_follows_pixels_per_second_within_limits(int width, int height, double fps, long expected)
    {
        Assert.Equal(expected, CaptureOptions.DefaultBitrate(width, height, fps));
    }

    [Fact]
    public void AV1_is_written_as_IVF_with_its_frame_count_filled_in()
    {
        var stream = new KeptMemoryStream();
        using (var file = new BitstreamFile(stream, VideoCodec.Av1, 2560, 1440))
        {
            file.Write([0x12, 0x00, 0xAA], 0);
            file.Write([0x12, 0x00], 17);
        }

        byte[] bytes = stream.ToArray();
        Assert.Equal("DKIF"u8.ToArray(), bytes[..4]);
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.Equal("AV01"u8.ToArray(), bytes[8..12]);
        Assert.Equal(2560, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12)));
        Assert.Equal(1440, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14)));
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        // Each frame: 4-byte size, 8-byte timestamp, then the frame.
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32)));
        Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(36)));
        Assert.Equal(new byte[] { 0x12, 0x00, 0xAA }, bytes[44..47]);
        Assert.Equal(17L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(51)));
        Assert.Equal(32 + 12 + 3 + 12 + 2, bytes.Length);
    }

    [Fact]
    public void IVF_timestamps_always_increase()
    {
        var stream = new KeptMemoryStream();
        using (var file = new BitstreamFile(stream, VideoCodec.Av1, 64, 64))
        {
            file.Write([1], 42);
            file.Write([2], 42);
            file.Write([3], 40);
            file.Write([4], 50);
        }

        byte[] bytes = stream.ToArray();
        long[] times = Enumerable.Range(0, 4).Select(i => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32 + i * 13 + 4))).ToArray();
        Assert.Equal([42L, 43L, 44L, 50L], times);
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.Hevc)]
    public void Annex_B_is_written_as_is(VideoCodec codec)
    {
        var stream = new KeptMemoryStream();
        using (var file = new BitstreamFile(stream, codec, 1920, 1080))
        {
            file.Write([0, 0, 0, 1, 0x09], 0);
            file.Write([0, 0, 1, 0x41], 16);
        }

        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x09, 0, 0, 1, 0x41 }, stream.ToArray());
    }

    [Theory]
    [InlineData(VideoCodec.H264, ".h264")]
    [InlineData(VideoCodec.Hevc, ".h265")]
    [InlineData(VideoCodec.Av1, ".ivf")]
    public void Each_codec_gets_the_extension_players_expect(VideoCodec codec, string extension)
    {
        Assert.Equal(extension, BitstreamFile.Extension(codec));
    }

    [Theory]
    [InlineData(0.5, 3)]
    [InlineData(0.95, 5)]
    [InlineData(0.0, 1)]
    [InlineData(1.0, 5)]
    public void Percentiles_are_nearest_rank(double fraction, double expected)
    {
        Assert.Equal(expected, CaptureTest.Percentile([5, 1, 4, 2, 3], fraction));
    }

    /// <summary>A memory stream whose bytes stay readable after BitstreamFile disposes it.</summary>
    sealed class KeptMemoryStream : MemoryStream
    {
        byte[]? _kept;

        public override byte[] ToArray() => _kept ?? base.ToArray();

        protected override void Dispose(bool disposing)
        {
            _kept ??= base.ToArray();
            base.Dispose(disposing);
        }
    }
}
