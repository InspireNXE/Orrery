using Orrery.Core.Sessions;
using Orrery.Core.Settings;

namespace Orrery.App.Sessions;

/// <summary>
/// Daltonization of remote desktops: colors a color-blind viewer would confuse are pushed apart into ones they can
/// tell apart. The deficiency is simulated with the matrix S of Machado, Oliveira and Fernandes (2009); what it loses,
/// c - Sc, is moved by E into channels the viewer does see and added back (Fidaner, Lin and Ozguven). All of that is
/// linear in linear-light RGB, so it folds into one 3x3 matrix, I + strength × E(I - S), worked out once. Grays come
/// through unchanged because S keeps them.
/// </summary>
/// <remarks>
/// Per pixel it costs nine table lookups and three more for the result: the tables hold the matrix times each
/// channel's 256 values already turned into linear light, and the sRGB curve back from 14-bit linear light. Results
/// outside the screen's colors are clamped.
/// </remarks>
sealed class ColorCorrection
{
    // Machado et al. 2009 at full severity, for linear RGB.
    static readonly double[,] Protan =
    {
        { 0.152286, 1.052583, -0.204868 },
        { 0.114503, 0.786281, 0.099216 },
        { -0.003882, -0.048116, 1.051998 },
    };

    static readonly double[,] Deutan =
    {
        { 0.367322, 0.860646, -0.227968 },
        { 0.280085, 0.672501, 0.047413 },
        { -0.011820, 0.042940, 0.968881 },
    };

    static readonly double[,] Tritan =
    {
        { 1.255528, -0.076749, -0.178779 },
        { -0.078411, 0.930809, 0.147602 },
        { 0.004733, 0.691367, 0.303900 },
    };

    // Where the lost color goes. Red-green: what red loses becomes more green and blue, so a red and a green differ
    // in blue-yellow, which the viewer sees. Blue-yellow: what blue loses goes to red and green instead.
    static readonly double[,] RedGreenShift =
    {
        { 0, 0, 0 },
        { 0.7, 1, 0 },
        { 0.7, 0, 1 },
    };

    static readonly double[,] BlueYellowShift =
    {
        { 1, 0, 0.7 },
        { 0, 1, 0.7 },
        { 0, 0, 0 },
    };

    /// <summary>Steps of linear light between black and white in the tables: 14 bits, fine enough that every byte value survives the round trip.</summary>
    const int LinearMax = (1 << 14) - 1;

    static readonly byte[] Encode = BuildEncode();

    // Row-major: entry (row × 3 + column) × 256 + value is matrix[row, column] × linear(value) in LinearMax steps.
    readonly int[] _tables = new int[9 * 256];

    ColorCorrection(double[,] matrix)
    {
        Matrix = matrix;
        for (int v = 0; v < 256; v++)
        {
            double linear = ToLinear(v / 255.0);
            for (int i = 0; i < 9; i++)
            {
                _tables[i * 256 + v] = (int)Math.Round(matrix[i / 3, i % 3] * linear * LinearMax);
            }
        }
    }

    /// <summary>The 3x3 matrix applied to linear RGB.</summary>
    public double[,] Matrix { get; }

    /// <summary>The color vision this corrects for; None for one made from a matrix.</summary>
    public ColorVision Vision { get; private init; }

    /// <summary>How strongly, 1 to 100 percent; 0 for one made from a matrix.</summary>
    public int Strength { get; private init; }

    /// <summary>The correction for <paramref name="vision"/> at <paramref name="strengthPercent"/>; null when it would change nothing.</summary>
    public static ColorCorrection? For(ColorVision vision, int strengthPercent) =>
        vision == ColorVision.None || strengthPercent <= 0 ? null
            : new ColorCorrection(MatrixFor(vision, Math.Min(strengthPercent, 100) / 100.0)) { Vision = vision, Strength = Math.Min(strengthPercent, 100) };

    /// <summary>
    /// What turns colors shown with <paramref name="shownWith"/> into colors shown with <paramref name="wanted"/>
    /// (either may be none): <paramref name="wanted"/> after undoing <paramref name="shownWith"/>. Null when they're
    /// the same. Undoing is exact except where the first correction pushed a color past what the screen shows.
    /// </summary>
    public static ColorCorrection? Between(ColorCorrection? shownWith, ColorCorrection? wanted)
    {
        if (shownWith is null)
        {
            return wanted;
        }

        if (wanted is not null && wanted.Vision == shownWith.Vision && wanted.Strength == shownWith.Strength)
        {
            return null;
        }

        var matrix = Invert(shownWith.Matrix);
        if (wanted is not null)
        {
            matrix = Multiply(wanted.Matrix, matrix);
        }

        return IsIdentity(matrix) ? null : new ColorCorrection(matrix);
    }

    /// <summary>
    /// The matrix as an SVG feColorMatrix's values, which apply it to linear-light RGB as this does (its
    /// color-interpolation-filters is linearRGB), so a web page is corrected the same as a remote desktop.
    /// </summary>
    public string FilterValues
    {
        get
        {
            var m = Matrix;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{m[0, 0]:0.######} {m[0, 1]:0.######} {m[0, 2]:0.######} 0 0 {m[1, 0]:0.######} {m[1, 1]:0.######} {m[1, 2]:0.######} 0 0 {m[2, 0]:0.######} {m[2, 1]:0.######} {m[2, 2]:0.######} 0 0 0 0 0 1 0");
        }
    }

    static double[,] Multiply(double[,] a, double[,] b)
    {
        var result = new double[3, 3];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                for (int k = 0; k < 3; k++)
                {
                    result[row, column] += a[row, k] * b[k, column];
                }
            }
        }

        return result;
    }

    /// <summary>The inverse, by cofactors. Every correction is invertible: it's the identity plus a small change.</summary>
    static double[,] Invert(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2], d = m[1, 0], e = m[1, 1], f = m[1, 2], g = m[2, 0], h = m[2, 1], i = m[2, 2];
        double determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        return new double[,]
        {
            { (e * i - f * h) / determinant, (c * h - b * i) / determinant, (b * f - c * e) / determinant },
            { (f * g - d * i) / determinant, (a * i - c * g) / determinant, (c * d - a * f) / determinant },
            { (d * h - e * g) / determinant, (b * g - a * h) / determinant, (a * e - b * d) / determinant },
        };
    }

    static bool IsIdentity(double[,] m)
    {
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                if (Math.Abs(m[row, column] - (row == column ? 1 : 0)) > 1e-6)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>I + <paramref name="strength"/> × E(I - S): the identity for no deficiency or no strength.</summary>
    public static double[,] MatrixFor(ColorVision vision, double strength)
    {
        var result = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            result[i, i] = 1;
        }

        if (vision == ColorVision.None || strength <= 0)
        {
            return result;
        }

        var (simulate, shift) = vision switch
        {
            ColorVision.Protanopia => (Protan, RedGreenShift),
            ColorVision.Deuteranopia => (Deutan, RedGreenShift),
            _ => (Tritan, BlueYellowShift),
        };
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                double sum = 0;
                for (int k = 0; k < 3; k++)
                {
                    double lost = (k == column ? 1 : 0) - simulate[k, column];
                    sum += shift[row, k] * lost;
                }

                result[row, column] += strength * sum;
            }
        }

        return result;
    }

    /// <summary>Corrects one color, as the filter does a pixel.</summary>
    public (byte R, byte G, byte B) Apply(byte r, byte g, byte b)
    {
        var t = _tables;
        return (
            Encode[Math.Clamp(t[r] + t[256 + g] + t[512 + b], 0, LinearMax)],
            Encode[Math.Clamp(t[768 + r] + t[1024 + g] + t[1280 + b], 0, LinearMax)],
            Encode[Math.Clamp(t[1536 + r] + t[1792 + g] + t[2048 + b], 0, LinearMax)]);
    }

    /// <summary>Corrects packed RGB pixels in place, three bytes each, as an exported frame.</summary>
    public unsafe void ApplyRgb(Span<byte> rgb)
    {
        fixed (int* t = _tables)
        fixed (byte* encode = Encode)
        {
            for (int p = 0; p + 2 < rgb.Length; p += 3)
            {
                int r = rgb[p], g = rgb[p + 1], b = rgb[p + 2];
                rgb[p] = encode[Clamp(t[r] + t[256 + g] + t[512 + b])];
                rgb[p + 1] = encode[Clamp(t[768 + r] + t[1024 + g] + t[1280 + b])];
                rgb[p + 2] = encode[Clamp(t[1536 + r] + t[1792 + g] + t[2048 + b])];
            }
        }
    }

    // Big regions are split into bands of rows across the cores; under this many pixels a single thread is quicker.
    const int ParallelPixels = 1 << 17;

    /// <summary>
    /// Corrects <paramref name="region"/> of an RGBA frame in place, as the bitmap a remote desktop is drawn into.
    /// The region is clipped to the frame; alpha is kept.
    /// </summary>
    public unsafe void Apply(IntPtr pixels, int rowBytes, int width, int height, FrameRect region)
    {
        int x = Math.Clamp(region.X, 0, width), y = Math.Clamp(region.Y, 0, height);
        int right = Math.Clamp(region.Right, x, width), bottom = Math.Clamp(region.Bottom, y, height);
        int columns = right - x, rows = bottom - y;
        if (columns == 0 || rows == 0)
        {
            return;
        }

        byte* origin = (byte*)pixels + (nint)y * rowBytes + (nint)x * 4;
        if ((long)columns * rows < ParallelPixels)
        {
            Rows(origin, rowBytes, columns, 0, rows);
            return;
        }

        int bands = Math.Min(Environment.ProcessorCount, rows);
        nint start = (nint)origin;
        Parallel.For(0, bands, band =>
        {
            int first = rows * band / bands, last = rows * (band + 1) / bands;
            Rows((byte*)start, rowBytes, columns, first, last);
        });
    }

    unsafe void Rows(byte* origin, int rowBytes, int columns, int firstRow, int lastRow)
    {
        fixed (int* t = _tables)
        fixed (byte* encode = Encode)
        {
            for (int row = firstRow; row < lastRow; row++)
            {
                byte* p = origin + (nint)row * rowBytes;
                byte* end = p + columns * 4;
                for (; p < end; p += 4)
                {
                    int r = p[0], g = p[1], b = p[2];
                    p[0] = encode[Clamp(t[r] + t[256 + g] + t[512 + b])];
                    p[1] = encode[Clamp(t[768 + r] + t[1024 + g] + t[1280 + b])];
                    p[2] = encode[Clamp(t[1536 + r] + t[1792 + g] + t[2048 + b])];
                }
            }
        }
    }

    static int Clamp(int v) => v < 0 ? 0 : v > LinearMax ? LinearMax : v;

    static double ToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    static double ToSrgb(double v) => v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    static byte[] BuildEncode()
    {
        var table = new byte[LinearMax + 1];
        for (int i = 0; i <= LinearMax; i++)
        {
            table[i] = (byte)Math.Round(ToSrgb((double)i / LinearMax) * 255);
        }

        return table;
    }
}
