using System.Buffers.Binary;

namespace Orrery.Streaming;

/// <summary>What a media datagram carries.</summary>
public enum MediaKind : byte
{
    Video = 1,
    Audio = 2,
}

[Flags]
public enum MediaPacketFlags : byte
{
    None = 0,

    /// <summary>Part of a frame the decoder can start from: a keyframe or a recovery frame.</summary>
    Recovery = 1,

    /// <summary>FEC parity rather than frame data.</summary>
    Parity = 2,
}

/// <summary>
/// The header at the start of every video and audio datagram: which frame a packet belongs to and where it goes in
/// it. Big-endian, <see cref="Size"/> bytes. A draft that will grow FEC group fields with the FEC itself.
/// </summary>
/// <param name="Stream">Which stream on the connection: a monitor's video, or an audio stream.</param>
/// <param name="CaptureMicroseconds">When the host captured the frame, on its own clock, for pacing and A/V sync.</param>
public readonly record struct MediaPacketHeader(
    MediaKind Kind,
    MediaPacketFlags Flags,
    byte Stream,
    uint FrameNumber,
    ushort PacketIndex,
    ushort PacketCount,
    ulong CaptureMicroseconds)
{
    public const int Size = 20;

    /// <summary>Writes the header to the start of <paramref name="destination"/>; false if it's too short.</summary>
    public bool TryWrite(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            return false;
        }

        destination[0] = (byte)Kind;
        destination[1] = (byte)Flags;
        destination[2] = Stream;
        destination[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], FrameNumber);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], PacketIndex);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], PacketCount);
        BinaryPrimitives.WriteUInt64BigEndian(destination[12..], CaptureMicroseconds);
        return true;
    }

    /// <summary>
    /// Reads a header from the start of <paramref name="source"/>. The other side is untrusted: false for a short
    /// datagram, an unknown kind or flag, a reserved byte that isn't zero, or a packet index outside its count.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out MediaPacketHeader header)
    {
        header = default;
        if (source.Length < Size)
        {
            return false;
        }

        var kind = (MediaKind)source[0];
        var flags = (MediaPacketFlags)source[1];
        ushort index = BinaryPrimitives.ReadUInt16BigEndian(source[8..]);
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(source[10..]);
        if (kind is not (MediaKind.Video or MediaKind.Audio)
            || (flags & ~(MediaPacketFlags.Recovery | MediaPacketFlags.Parity)) != 0
            || source[3] != 0
            || count == 0
            || index >= count)
        {
            return false;
        }

        header = new MediaPacketHeader(
            kind,
            flags,
            source[2],
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]),
            index,
            count,
            BinaryPrimitives.ReadUInt64BigEndian(source[12..]));
        return true;
    }
}
