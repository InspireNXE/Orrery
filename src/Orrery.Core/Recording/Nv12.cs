namespace Orrery.Core.Recording;

/// <summary>
/// RGB to NV12, the picture format video encoders take: a full-size plane of brightness, then a half-size plane of
/// color with U and V interleaved. BT.709 in the 16–235 range, which is what players assume for HD video.
/// </summary>
public static class Nv12
{
    /// <summary>The bytes an NV12 picture of this size takes. Both sides must be even.</summary>
    public static int Size(int width, int height) => width * height * 3 / 2;

    /// <summary>The size rounded up to even sides, which NV12 and the encoders need.</summary>
    public static (int Width, int Height) EvenSize(int width, int height) => (width + (width & 1), height + (height & 1));

    /// <summary>Converts packed RGB, 3 bytes a pixel, into <paramref name="nv12"/>. Both sides must be even.</summary>
    public static void FromRgb(ReadOnlySpan<byte> rgb, int width, int height, Span<byte> nv12)
    {
        if ((width | height) % 2 != 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentException("NV12 needs an even width and height.");
        }

        if (rgb.Length < width * height * 3 || nv12.Length < Size(width, height))
        {
            throw new ArgumentException("The buffers are too small for the picture.");
        }

        var luma = nv12[..(width * height)];
        var chroma = nv12[(width * height)..];
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                // The four pixels of a 2×2 block share one U and V, from their average.
                int r = 0, g = 0, b = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int i = ((y + dy) * width + x + dx) * 3;
                        int pr = rgb[i], pg = rgb[i + 1], pb = rgb[i + 2];
                        luma[(y + dy) * width + x + dx] = (byte)(16 + ((47 * pr + 157 * pg + 16 * pb + 128) >> 8));
                        r += pr;
                        g += pg;
                        b += pb;
                    }
                }

                r = (r + 2) >> 2;
                g = (g + 2) >> 2;
                b = (b + 2) >> 2;
                int c = (y / 2) * width + x;
                chroma[c] = Clamp(128 + ((-26 * r - 86 * g + 112 * b + 128) >> 8));
                chroma[c + 1] = Clamp(128 + ((112 * r - 102 * g - 10 * b + 128) >> 8));
            }
        }
    }

    static byte Clamp(int value) => (byte)Math.Clamp(value, 16, 240);
}
