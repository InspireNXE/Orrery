using Xunit;

namespace Orrery.Host.Tests;

public class FrameMarkerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096 + 17)]
    [InlineData(123_456)]
    public void A_drawn_marker_reads_back_at_full_size(long frame)
    {
        var pixels = new byte[FrameMarker.Width * FrameMarker.Height * 4];
        FrameMarker.Draw(pixels, frame);

        Assert.True(FrameMarker.TryDecode(FrameMarker.Read(pixels, FrameMarker.Width * 4, 1, 1), out int number));
        Assert.Equal(frame % FrameMarker.Modulus, number);
    }

    [Fact]
    public void A_marker_reads_back_at_half_size()
    {
        // The viewer shows a 2560x1440 stream in a 1280x720 window: every other pixel of the drawn marker.
        var full = new byte[FrameMarker.Width * FrameMarker.Height * 4];
        FrameMarker.Draw(full, 2741);
        int width = FrameMarker.Width / 2, height = FrameMarker.Height / 2;
        var half = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            full.AsSpan((y * 2 * FrameMarker.Width) * 4, FrameMarker.Width * 4).ToArray()
                .Where((_, i) => i / 4 % 2 == 0).ToArray().CopyTo(half, y * width * 4);
        }

        Assert.True(FrameMarker.TryDecode(FrameMarker.Read(half, width * 4, 0.5, 0.5), out int number));
        Assert.Equal(2741, number);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)0xFFFF)]
    public void Blank_screens_are_not_markers(ushort bits)
    {
        Assert.False(FrameMarker.TryDecode(bits, out _));
    }

    [Fact]
    public void Every_single_bit_error_is_caught()
    {
        ushort bits = FrameMarker.Encode(1234);
        for (int bit = 0; bit < 16; bit++)
        {
            Assert.False(FrameMarker.TryDecode((ushort)(bits ^ (1 << bit)), out _));
        }
    }

    [Theory]
    [InlineData(100, 1000, 100)]
    [InlineData(4090, 4100, 4090)]
    [InlineData(5, 4100, 4101 - 4096)]
    [InlineData(4, 4100, 4100)]
    public void A_number_resolves_to_the_latest_frame_it_can_be(int number, long newest, long expected)
    {
        Assert.Equal(expected, FrameMarker.Resolve(number, newest));
    }
}
