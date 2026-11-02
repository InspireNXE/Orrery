using System.Buffers.Binary;

namespace Orrery.Protocols.Rdp;

/// <summary>
/// CF_DIB, the device-independent bitmap Windows puts on its clipboard: a BITMAPINFO header and the pixels, without
/// the BITMAPFILEHEADER of a .bmp file.
/// </summary>
static class RdpDib
{
    const int InfoHeaderSize = 40;
    const int FileHeaderSize = 14;
    const uint BiRgb = 0;
    const uint BiBitfields = 3;
    const uint BiAlphaBitfields = 6;

    /// <summary>The most pixels an image may have either way, a little over an 8K screen.</summary>
    internal const long MaxPixels = 40_000_000;

    /// <summary>A 32-bit CF_DIB of BGRA pixels given top row first.</summary>
    public static byte[] FromBgra(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels || bgra.Length != width * height * 4)
        {
            throw new ArgumentException("Not an image of that size.", nameof(bgra));
        }

        int stride = width * 4;
        var dib = new byte[InfoHeaderSize + stride * height];
        var header = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(header, InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height); // Positive: bottom row first.
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], BiRgb);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);
        for (int row = 0; row < height; row++)
        {
            bgra.Slice(row * stride, stride).CopyTo(dib.AsSpan(InfoHeaderSize + (height - 1 - row) * stride));
        }

        return dib;
    }

    /// <summary>
    /// The CF_DIB as a .bmp file, which image decoders read, or null when its header doesn't describe an image the
    /// data holds. Server data is untrusted, so the header is checked against the data's length.
    /// </summary>
    public static byte[]? ToBmpFile(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < InfoHeaderSize)
        {
            return null;
        }

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        if (headerSize < InfoHeaderSize || headerSize > dib.Length || width <= 0 || height is 0 or int.MinValue
            || (long)width * Math.Abs(height) > MaxPixels || bitCount is not (1 or 4 or 8 or 16 or 24 or 32) || colorsUsed > 256)
        {
            return null;
        }

        // A BITMAPINFOHEADER keeps its color masks after it; the larger headers hold them inside.
        int masks = headerSize == InfoHeaderSize ? compression switch { BiBitfields => 12, BiAlphaBitfields => 16, _ => 0 } : 0;
        long colors = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1L << bitCount : 0;
        long pixelOffset = headerSize + masks + colors * 4;
        long rowBytes = ((long)width * bitCount + 31) / 32 * 4;

        // Compressed pixels (RLE, JPEG, PNG) are shorter than this and checked by the decoder instead.
        bool uncompressed = compression is BiRgb or BiBitfields or BiAlphaBitfields;
        if (pixelOffset > dib.Length || (uncompressed && pixelOffset + rowBytes * Math.Abs(height) > dib.Length))
        {
            return null;
        }

        var file = new byte[FileHeaderSize + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), (int)(FileHeaderSize + pixelOffset));
        dib.CopyTo(file.AsSpan(FileHeaderSize));
        return file;
    }
}
