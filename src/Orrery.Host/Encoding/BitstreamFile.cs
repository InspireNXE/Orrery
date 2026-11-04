using System.Buffers.Binary;
using Orrery.Streaming;

namespace Orrery.Host.Encoding;

/// <summary>
/// Writes encoded frames to a file a player can open, to check what an encoder made: H.264 and HEVC as raw Annex B,
/// which needs no container, and AV1 in IVF, the simplest one players read. Timestamps are in milliseconds.
/// </summary>
sealed class BitstreamFile : IDisposable
{
    const int IvfHeaderSize = 32;
    const int IvfFrameCountOffset = 24;

    readonly Stream _stream;
    readonly VideoCodec _codec;
    readonly byte[] _frameHeader = new byte[12];
    uint _frames;
    long _lastMilliseconds = -1;

    public BitstreamFile(Stream stream, VideoCodec codec, int width, int height)
    {
        _stream = stream;
        _codec = codec;
        if (codec == VideoCodec.Av1)
        {
            Span<byte> header = stackalloc byte[IvfHeaderSize];
            header.Clear();
            "DKIF"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 0); // version
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], IvfHeaderSize);
            "AV01"u8.CopyTo(header[8..]);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], checked((ushort)width));
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], checked((ushort)height));
            BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 1000); // timebase denominator: milliseconds
            BinaryPrimitives.WriteUInt32LittleEndian(header[20..], 1); // timebase numerator
            _stream.Write(header);
        }
    }

    /// <summary>The file extension players expect for <paramref name="codec"/>.</summary>
    public static string Extension(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => ".h264",
        VideoCodec.Hevc => ".h265",
        _ => ".ivf",
    };

    /// <summary>
    /// Appends a frame. Two frames can be presented within the same millisecond, and players reject a timestamp that
    /// doesn't increase, so one that doesn't is moved a millisecond past the last.
    /// </summary>
    public void Write(ReadOnlySpan<byte> frame, long milliseconds)
    {
        milliseconds = Math.Max(milliseconds, _lastMilliseconds + 1);
        _lastMilliseconds = milliseconds;
        if (_codec == VideoCodec.Av1)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_frameHeader, (uint)frame.Length);
            BinaryPrimitives.WriteInt64LittleEndian(_frameHeader.AsSpan(4), milliseconds);
            _stream.Write(_frameHeader);
        }

        _stream.Write(frame);
        _frames++;
    }

    /// <summary>Fills in the IVF frame count, when the stream can seek back to it, and closes the stream.</summary>
    public void Dispose()
    {
        if (_codec == VideoCodec.Av1 && _stream.CanSeek)
        {
            Span<byte> count = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(count, _frames);
            _stream.Position = IvfFrameCountOffset;
            _stream.Write(count);
        }

        _stream.Dispose();
    }
}
