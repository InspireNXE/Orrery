namespace Orrery.Host;

/// <summary>
/// A frame number drawn into the top-left corner of each frame <c>--capture --measure</c> sends, as a row of
/// <see cref="Blocks"/> black or white squares: 12 bits of the number (modulo 4096) and a 4-bit check. When the
/// viewer shows the frame on the same desktop, the host's next captures see the marker inside the viewer's window and
/// know exactly which frame reached the screen, and when. Big flat squares survive any bitrate the encoders use,
/// and still read at half size.
/// </summary>
static class FrameMarker
{
    public const int Blocks = 16;
    public const int BlockSize = 32;
    public const int Width = Blocks * BlockSize;
    public const int Height = BlockSize;
    public const int Modulus = 4096;

    /// <summary>The 16 bits drawn for <paramref name="frame"/>: the number in bits 0-11, the check in 12-15.</summary>
    public static ushort Encode(long frame)
    {
        int number = (int)(frame % Modulus);
        return (ushort)(number | (Check(number) << 12));
    }

    /// <summary>The number modulo 4096 that <paramref name="bits"/> carry, or false if the check doesn't match.</summary>
    public static bool TryDecode(ushort bits, out int number)
    {
        number = bits & 0xFFF;
        return bits >> 12 == Check(number);
    }

    /// <summary>
    /// The latest frame up to <paramref name="newest"/> whose number modulo 4096 is <paramref name="number"/>: what a
    /// marker read now refers to, since markers on screen are never more than a few frames old.
    /// </summary>
    public static long Resolve(int number, long newest) => newest - ((newest - number) % Modulus + Modulus) % Modulus;

    /// <summary>Draws <paramref name="frame"/>'s marker into a BGRA buffer <see cref="Width"/> x <see cref="Height"/>, rows packed.</summary>
    public static void Draw(Span<byte> bgra, long frame)
    {
        ushort bits = Encode(frame);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte value = (bits >> (x / BlockSize) & 1) != 0 ? (byte)255 : (byte)0;
                int i = (y * Width + x) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = value;
                bgra[i + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Reads a marker from BGRA pixels where it is shown <paramref name="scaleX"/> by <paramref name="scaleY"/> times
    /// its drawn size, its top-left at the buffer's (0, 0): the middle of each square, white or not.
    /// </summary>
    public static ushort Read(ReadOnlySpan<byte> bgra, int rowPitch, double scaleX, double scaleY)
    {
        int y = (int)(BlockSize / 2.0 * scaleY);
        ushort bits = 0;
        for (int block = 0; block < Blocks; block++)
        {
            int x = (int)((block * BlockSize + BlockSize / 2.0) * scaleX);
            int i = y * rowPitch + x * 4;
            int luma = (bgra[i] + bgra[i + 1] * 2 + bgra[i + 2]) / 4;
            if (luma >= 128)
            {
                bits |= (ushort)(1 << block);
            }
        }

        return bits;
    }

    /// <summary>The three nibbles summed modulo 16, flipped so that all black (frame 0 or nothing) doesn't pass.</summary>
    static int Check(int number) => ((number & 0xF) + (number >> 4 & 0xF) + (number >> 8 & 0xF)) % 16 ^ 0xA;
}
