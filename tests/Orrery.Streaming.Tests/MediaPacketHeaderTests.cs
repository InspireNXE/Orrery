using Xunit;

namespace Orrery.Streaming.Tests;

public class MediaPacketHeaderTests
{
    static readonly MediaPacketHeader Sample = new(MediaKind.Video, MediaPacketFlags.Recovery, 1, 0x01020304, 7, 12, 0x1122334455667788);

    [Fact]
    public void A_header_reads_back_as_written()
    {
        var buffer = new byte[MediaPacketHeader.Size];
        Assert.True(Sample.TryWrite(buffer));

        Assert.True(MediaPacketHeader.TryRead(buffer, out var read));
        Assert.Equal(Sample, read);
    }

    [Fact]
    public void Fields_are_big_endian()
    {
        var buffer = new byte[MediaPacketHeader.Size];
        Sample.TryWrite(buffer);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer[4..8]);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 }, buffer[12..20]);
    }

    [Fact]
    public void Writing_into_too_little_room_fails()
    {
        Assert.False(Sample.TryWrite(new byte[MediaPacketHeader.Size - 1]));
    }

    [Fact]
    public void A_short_datagram_is_refused()
    {
        var buffer = new byte[MediaPacketHeader.Size];
        Sample.TryWrite(buffer);

        Assert.False(MediaPacketHeader.TryRead(buffer.AsSpan(0, MediaPacketHeader.Size - 1), out _));
    }

    [Theory]
    [InlineData(0, 0)] // no such kind
    [InlineData(0, 9)] // no such kind
    [InlineData(1, 0x80)] // no such flag
    [InlineData(3, 1)] // the reserved byte
    public void Unknown_or_reserved_values_are_refused(int offset, byte value)
    {
        var buffer = new byte[MediaPacketHeader.Size];
        Sample.TryWrite(buffer);
        buffer[offset] = value;

        Assert.False(MediaPacketHeader.TryRead(buffer, out _));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 12)]
    [InlineData(13, 12)]
    public void A_packet_index_outside_its_count_is_refused(ushort index, ushort count)
    {
        var buffer = new byte[MediaPacketHeader.Size];
        (Sample with { PacketIndex = index, PacketCount = count }).TryWrite(buffer);

        Assert.False(MediaPacketHeader.TryRead(buffer, out _));
    }
}
