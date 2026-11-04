namespace Orrery.Streaming;

/// <summary>Takes one datagram; the span is only valid during the call.</summary>
public delegate void DatagramSink(ReadOnlySpan<byte> datagram);

/// <summary>
/// Splits an encoded frame into datagrams, each a <see cref="MediaPacketHeader"/> and a piece of the frame, so that
/// <see cref="FrameAssembler"/> can put it back together. No FEC yet: losing any packet loses the frame.
/// </summary>
public static class FramePacketizer
{
    /// <summary>How many datagrams of at most <paramref name="maxDatagram"/> bytes a frame of <paramref name="frameLength"/> needs.</summary>
    public static int PacketCount(int frameLength, int maxDatagram = OspProtocol.MaxDatagramPayload)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameLength);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDatagram, MediaPacketHeader.Size);
        int payload = maxDatagram - MediaPacketHeader.Size;
        return (frameLength + payload - 1) / payload;
    }

    /// <summary>
    /// Writes <paramref name="frame"/> as datagrams no longer than <paramref name="buffer"/>, which each one is built in,
    /// and hands them to <paramref name="sink"/> in order. <paramref name="header"/> gives every field but the packet
    /// index and count, which are filled in.
    /// </summary>
    public static void Split(ReadOnlySpan<byte> frame, MediaPacketHeader header, Span<byte> buffer, DatagramSink sink)
    {
        int count = PacketCount(frame.Length, buffer.Length);
        if (count > ushort.MaxValue)
        {
            throw new ArgumentException($"A frame of {frame.Length:N0} bytes needs more than {ushort.MaxValue} packets.", nameof(frame));
        }

        int payload = buffer.Length - MediaPacketHeader.Size;
        for (int i = 0; i < count; i++)
        {
            var piece = frame.Slice(i * payload, Math.Min(payload, frame.Length - i * payload));
            (header with { PacketIndex = (ushort)i, PacketCount = (ushort)count }).TryWrite(buffer);
            piece.CopyTo(buffer[MediaPacketHeader.Size..]);
            sink(buffer[..(MediaPacketHeader.Size + piece.Length)]);
        }
    }
}
