using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Orrery.Core.Sessions;

namespace Orrery.Core.Recording;

/// <summary>
/// The screen codec (docs/design-session-recording.md §2.2): lossless, RGB at 3 bytes a pixel, compressed with
/// Brotli. A key frame is <c>width, height, Brotli(pixels)</c>; a change frame is <c>count, count × (x, y, width,
/// height), Brotli(the rectangles' pixels XOR the last recorded pixels there, one after another)</c>.
/// </summary>
public static class ScreenFrames
{
    public const int MaxSide = 8192;
    public const int MaxRectangles = 1000;
    const int BrotliQuality = 1;
    const int BrotliWindow = 22;

    public static byte[] EncodeKeyFrame(ReadOnlySpan<byte> rgb, int width, int height)
    {
        var packed = Compress(rgb, 8, out int length);
        BinaryPrimitives.WriteInt32LittleEndian(packed, width);
        BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(4), height);
        return packed[..length];
    }

    public static byte[] EncodeChange(IReadOnlyList<FrameRect> rectangles, ReadOnlySpan<byte> xorPixels)
    {
        int head = 2 + rectangles.Count * 16;
        var packed = Compress(xorPixels, head, out int length);
        BinaryPrimitives.WriteUInt16LittleEndian(packed, (ushort)rectangles.Count);
        for (int i = 0; i < rectangles.Count; i++)
        {
            var r = rectangles[i];
            var at = packed.AsSpan(2 + i * 16);
            BinaryPrimitives.WriteInt32LittleEndian(at, r.X);
            BinaryPrimitives.WriteInt32LittleEndian(at[4..], r.Y);
            BinaryPrimitives.WriteInt32LittleEndian(at[8..], r.Width);
            BinaryPrimitives.WriteInt32LittleEndian(at[12..], r.Height);
        }

        return packed[..length];
    }

    // The result has `offset` bytes free at the start for the frame's own header.
    static byte[] Compress(ReadOnlySpan<byte> data, int offset, out int length)
    {
        var output = new byte[offset + BrotliEncoder.GetMaxCompressedLength(data.Length)];
        if (!BrotliEncoder.TryCompress(data, output.AsSpan(offset), out int written, BrotliQuality, BrotliWindow))
        {
            throw new InvalidOperationException("Brotli couldn't compress a frame.");
        }

        length = offset + written;
        return output;
    }

    /// <summary>
    /// A <see cref="RecordingEventType.PointerShape"/> event's payload: empty for the usual arrow; else width, height,
    /// hotspot x and y (16 bits each), then Brotli(the RGBA pixels), none for a hidden pointer.
    /// </summary>
    public static byte[] EncodePointerShape(RemotePointerShape? shape)
    {
        if (shape is null)
        {
            return [];
        }

        if (shape.IsHidden)
        {
            return new byte[8];
        }

        var output = Compress(shape.Rgba, 8, out int length);
        BinaryPrimitives.WriteUInt16LittleEndian(output, (ushort)shape.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(2), (ushort)shape.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4), (ushort)shape.HotspotX);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(6), (ushort)shape.HotspotY);
        return output[..length];
    }

    /// <summary>The shape in a <see cref="EncodePointerShape"/> payload, or false when it's damaged or too big.</summary>
    public static bool TryDecodePointerShape(byte[] payload, out RemotePointerShape? shape)
    {
        shape = null;
        if (payload.Length == 0)
        {
            return true;
        }

        if (payload.Length < 8)
        {
            return false;
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(payload), height = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2));
        if (width == 0 && height == 0)
        {
            shape = RemotePointerShape.Hidden;
            return payload.Length == 8;
        }

        if (width is < 1 or > RemotePointerShape.MaxSide || height is < 1 or > RemotePointerShape.MaxSide)
        {
            return false;
        }

        var rgba = new byte[width * height * 4];
        if (!Decompress(payload.AsSpan(8), rgba, rgba.Length))
        {
            return false;
        }

        shape = RemotePointerShape.Create(width, height, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6)), rgba);
        return shape is not null;
    }

    /// <summary>Decompresses exactly <paramref name="expected"/> bytes into <paramref name="destination"/>, or fails.</summary>
    internal static bool Decompress(ReadOnlySpan<byte> source, Span<byte> destination, int expected)
    {
        if (destination.Length < expected)
        {
            return false;
        }

        using var decoder = new BrotliDecoder();
        var status = decoder.Decompress(source, destination[..expected], out int consumed, out int written);
        return status == System.Buffers.OperationStatus.Done && written == expected && consumed == source.Length;
    }

    /// <summary>Copies RGBA pixels to RGB, dropping alpha.</summary>
    public static void RgbaToRgb(ReadOnlySpan<byte> rgba, Span<byte> rgb)
    {
        for (int i = 0, o = 0; i + 3 < rgba.Length; i += 4, o += 3)
        {
            rgb[o] = rgba[i];
            rgb[o + 1] = rgba[i + 1];
            rgb[o + 2] = rgba[i + 2];
        }
    }
}

/// <summary>
/// Damage waiting for the next frame, kept as a few rectangles rather than one union, so a clock in one corner and a
/// window in another don't make a whole-screen frame. Rectangles that touch, or nearly do, are merged.
/// </summary>
public sealed class DamageList
{
    const int Near = 16;
    const int MaxKept = 32;
    readonly List<FrameRect> _rects = [];

    public bool IsEmpty => _rects.Count == 0;

    public IReadOnlyList<FrameRect> Rectangles => _rects;

    public void Add(FrameRect rect)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < _rects.Count; i++)
            {
                if (Touches(_rects[i], rect))
                {
                    rect = rect.Union(_rects[i]);
                    _rects.RemoveAt(i);
                    merged = true;
                    break;
                }
            }
        }
        while (merged);

        _rects.Add(rect);
        if (_rects.Count > MaxKept)
        {
            var all = _rects.Aggregate(default(FrameRect), (sum, r) => sum.Union(r));
            _rects.Clear();
            _rects.Add(all);
        }
    }

    public void Clear() => _rects.Clear();

    static bool Touches(FrameRect a, FrameRect b) =>
        a.X - Near < b.Right && b.X - Near < a.Right && a.Y - Near < b.Bottom && b.Y - Near < a.Bottom;
}

/// <summary>Rebuilds the screen from a recording's frames, for the player and export.</summary>
public sealed class ScreenDecoder
{
    byte[] _rgb = [];
    byte[] _scratch = [];

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>The pixels, RGB, row after row; empty until the first key frame.</summary>
    public ReadOnlySpan<byte> Rgb => _rgb;

    /// <summary>Where the pointer was last, or null before it's been recorded.</summary>
    public (int X, int Y)? Pointer { get; private set; }

    /// <summary>What the pointer looked like last; null for the usual arrow, as in recordings made before shapes were.</summary>
    public RemotePointerShape? PointerShape { get; private set; }

    /// <summary>A key frame has arrived, so there's a picture.</summary>
    public bool HasFrame { get; private set; }

    /// <summary>Applies one event; false when it's damaged, which leaves the picture as it was.</summary>
    public bool Apply(RecordedEvent e)
    {
        switch (e.Type)
        {
            case RecordingEventType.Resize when RecordingFile.ReadPair(e.Payload) is var (w, h):
                return Resize(w, h);
            case RecordingEventType.KeyFrame:
                return KeyFrame(e.Payload);
            case RecordingEventType.Change:
                return Change(e.Payload);
            case RecordingEventType.Pointer when RecordingFile.ReadPair(e.Payload) is var (x, y):
                Pointer = (x, y);
                return true;
            case RecordingEventType.PointerShape:
                if (!ScreenFrames.TryDecodePointerShape(e.Payload, out var shape))
                {
                    return false;
                }

                PointerShape = shape;
                return true;
            case RecordingEventType.Resize or RecordingEventType.Pointer:
                return false;
            default:
                return true;
        }
    }

    bool Resize(int width, int height)
    {
        if (width is < 1 or > ScreenFrames.MaxSide || height is < 1 or > ScreenFrames.MaxSide)
        {
            return false;
        }

        if (width != Width || height != Height)
        {
            Width = width;
            Height = height;
            _rgb = new byte[width * height * 3];
            HasFrame = false;
        }

        return true;
    }

    bool KeyFrame(byte[] payload)
    {
        if (payload.Length < 8)
        {
            return false;
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(payload), height = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4));
        if (!Resize(width, height) || !ScreenFrames.Decompress(payload.AsSpan(8), _rgb, _rgb.Length))
        {
            return false;
        }

        HasFrame = true;
        return true;
    }

    bool Change(byte[] payload)
    {
        if (!HasFrame || payload.Length < 2)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        int head = 2 + count * 16;
        if (count is 0 or > ScreenFrames.MaxRectangles || payload.Length < head)
        {
            return false;
        }

        var rects = new FrameRect[count];
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            var at = payload.AsSpan(2 + i * 16);
            var r = new FrameRect(BinaryPrimitives.ReadInt32LittleEndian(at), BinaryPrimitives.ReadInt32LittleEndian(at[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(at[8..]), BinaryPrimitives.ReadInt32LittleEndian(at[12..]));
            if (r.X < 0 || r.Y < 0 || r.Width < 1 || r.Height < 1 || r.Width > Width - r.X || r.Height > Height - r.Y)
            {
                return false;
            }

            rects[i] = r;
            total += (long)r.Width * r.Height * 3;
        }

        // Rectangles may overlap, but never add up to more than a few screens.
        if (total > (long)_rgb.Length * 4)
        {
            return false;
        }

        if (_scratch.Length < total)
        {
            _scratch = new byte[total];
        }

        if (!ScreenFrames.Decompress(payload.AsSpan(head), _scratch, (int)total))
        {
            return false;
        }

        int offset = 0;
        foreach (var r in rects)
        {
            int rowBytes = r.Width * 3;
            for (int row = 0; row < r.Height; row++)
            {
                var target = _rgb.AsSpan(((r.Y + row) * Width + r.X) * 3, rowBytes);
                Xor(target, _scratch.AsSpan(offset, rowBytes));

                offset += rowBytes;
            }
        }

        return true;
    }

    /// <summary>XORs <paramref name="xor"/> into <paramref name="target"/>, a vector at a time: seeking replays a lot of these.</summary>
    static void Xor(Span<byte> target, ReadOnlySpan<byte> xor)
    {
        int i = 0;
        for (int step = Vector<byte>.Count; i <= target.Length - step; i += step)
        {
            (new Vector<byte>(target[i..]) ^ new Vector<byte>(xor[i..])).CopyTo(target[i..]);
        }

        for (; i < target.Length; i++)
        {
            target[i] ^= xor[i];
        }
    }

    /// <summary>The picture as opaque RGBA, for a bitmap or an encoder.</summary>
    public void CopyRgba(Span<byte> destination)
    {
        for (int i = 0, o = 0; i + 2 < _rgb.Length && o + 3 < destination.Length; i += 3, o += 4)
        {
            destination[o] = _rgb[i];
            destination[o + 1] = _rgb[i + 1];
            destination[o + 2] = _rgb[i + 2];
            destination[o + 3] = 255;
        }
    }
}
