namespace Orrery.Streaming;

/// <summary>A frame <see cref="FrameAssembler"/> put back together from its datagrams.</summary>
public sealed record AssembledFrame(MediaKind Kind, MediaPacketFlags Flags, byte Stream, uint FrameNumber, ulong CaptureMicroseconds, byte[] Data);

/// <summary>
/// Puts frames back together from datagrams made by <see cref="FramePacketizer"/>, in whatever order they arrive.
/// Frames are only ever handed on in order per stream: once a frame completes, older frames still missing packets are
/// dropped, and their late packets are ignored. Datagrams come from the network and aren't trusted: anything
/// malformed, inconsistent with the rest of its frame, or too big is counted and ignored, and memory is bounded by
/// <see cref="MaxFramesInProgress"/> and the largest frame allowed. Not thread-safe.
/// </summary>
public sealed class FrameAssembler(int maxFrameBytes = FrameAssembler.DefaultMaxFrameBytes)
{
    /// <summary>16 MB: well above an 8K keyframe at any bitrate OSP uses.</summary>
    public const int DefaultMaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>Frames that can be waiting for packets at once; a newer frame beyond that drops the oldest.</summary>
    public const int MaxFramesInProgress = 8;

    sealed class Partial(MediaPacketHeader first)
    {
        public readonly MediaPacketHeader First = first;
        public readonly byte[]?[] Pieces = new byte[]?[first.PacketCount];
        public int Received;
        public int Bytes;
    }

    readonly Dictionary<(byte Stream, uint Frame), Partial> _partial = [];
    readonly Dictionary<byte, uint> _lastCompleted = [];

    public long FramesCompleted { get; private set; }

    /// <summary>Frames given up on: a newer one completed first, or too many were waiting at once.</summary>
    public long FramesDropped { get; private set; }

    /// <summary>Datagrams ignored: malformed, inconsistent, too big, for a frame already finished or dropped, or FEC parity (no FEC yet).</summary>
    public long PacketsRejected { get; private set; }

    public long PacketsDuplicated { get; private set; }

    /// <summary>Adds one datagram; returns the frame it completes, if it completes one.</summary>
    public AssembledFrame? Add(ReadOnlySpan<byte> datagram)
    {
        if (!MediaPacketHeader.TryRead(datagram, out var header)
            || (header.Flags & MediaPacketFlags.Parity) != 0
            || datagram.Length == MediaPacketHeader.Size
            || (_lastCompleted.TryGetValue(header.Stream, out uint last) && !IsNewer(header.FrameNumber, last)))
        {
            PacketsRejected++;
            return null;
        }

        var key = (header.Stream, header.FrameNumber);
        if (!_partial.TryGetValue(key, out var partial))
        {
            if (_partial.Count >= MaxFramesInProgress)
            {
                DropOldest();
            }

            partial = new Partial(header);
            _partial.Add(key, partial);
        }
        else if (partial.First with { PacketIndex = header.PacketIndex } != header)
        {
            PacketsRejected++;
            return null;
        }

        if (partial.Pieces[header.PacketIndex] is not null)
        {
            PacketsDuplicated++;
            return null;
        }

        var piece = datagram[MediaPacketHeader.Size..];
        if (partial.Bytes + piece.Length > maxFrameBytes)
        {
            Drop(key);
            PacketsRejected++;
            return null;
        }

        partial.Pieces[header.PacketIndex] = piece.ToArray();
        partial.Bytes += piece.Length;
        if (++partial.Received < header.PacketCount)
        {
            return null;
        }

        var data = new byte[partial.Bytes];
        int offset = 0;
        foreach (var p in partial.Pieces)
        {
            p!.CopyTo(data, offset);
            offset += p.Length;
        }

        _partial.Remove(key);
        _lastCompleted[header.Stream] = header.FrameNumber;
        foreach (var older in _partial.Keys.Where(k => k.Stream == header.Stream && !IsNewer(k.Frame, header.FrameNumber)).ToList())
        {
            Drop(older);
        }

        FramesCompleted++;
        return new AssembledFrame(header.Kind, header.Flags, header.Stream, header.FrameNumber, header.CaptureMicroseconds, data);
    }

    /// <summary>Whether frame <paramref name="a"/> comes after <paramref name="b"/>, allowing for the number wrapping.</summary>
    static bool IsNewer(uint a, uint b) => (int)(a - b) > 0;

    void DropOldest()
    {
        var oldest = _partial.Keys.Aggregate((x, y) => IsNewer(x.Frame, y.Frame) ? y : x);
        Drop(oldest);
    }

    void Drop((byte Stream, uint Frame) key)
    {
        _partial.Remove(key);
        FramesDropped++;
    }
}
