using System.Runtime.CompilerServices;
using Orrery.Host.Encoding.Amf;
using Orrery.Streaming;
using Xunit;

namespace Orrery.Host.Tests;

public class AmfTests
{
    [Fact]
    public void Versions_read_as_major_minor_release()
    {
        Assert.Equal("1.4.36", AmfRuntime.FormatVersion(Amf.FullVersion));
    }

    [Fact]
    public void A_variant_is_passed_by_value_at_its_generated_size()
    {
        // SetProperty takes AMFVariantStruct by value, so its size decides how it's passed; it must match the header's.
        Assert.Equal(24, Unsafe.SizeOf<AMFVariantStruct>());
    }

    [Fact]
    public void Every_codec_has_an_encoder()
    {
        Assert.Equal([VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1], AmfCodec.All.Select(c => c.Codec));
    }

    [Theory]
    [InlineData(VideoCodec.H264, new byte[] { 0, 0, 0, 1, 0x09 }, true)]
    [InlineData(VideoCodec.Hevc, new byte[] { 0, 0, 1, 0x46 }, true)]
    [InlineData(VideoCodec.Hevc, new byte[] { 0x12, 0x00 }, false)]
    [InlineData(VideoCodec.Av1, new byte[] { 0x12, 0x00, 0x0A }, true)]
    [InlineData(VideoCodec.Av1, new byte[] { 0, 0, 0, 1 }, false)]
    [InlineData(VideoCodec.H264, new byte[] { 0, 0 }, false)]
    public void A_first_frame_starts_the_way_its_codec_requires(VideoCodec codec, byte[] frame, bool expected)
    {
        Assert.Equal(expected, AmfTestEncoder.StartsCorrectly(codec, frame));
    }
}
