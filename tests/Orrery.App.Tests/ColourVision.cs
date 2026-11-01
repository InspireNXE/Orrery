using Avalonia.Media;

namespace Orrery.App.Tests;

public enum Deficiency { None, Protanopia, Deuteranopia, Tritanopia }

/// <summary>
/// Colour science for the accessibility tests: colour blindness simulated with the matrices of Machado, Oliveira and
/// Fernandes (2009) at full severity, applied to linear sRGB; CIEDE2000 colour difference; WCAG 2 contrast ratio.
/// </summary>
public static class ColourVision
{
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

    /// <summary>How <paramref name="colour"/> looks with <paramref name="deficiency"/>, as linear RGB in 0..1.</summary>
    public static (double R, double G, double B) Simulate(Color colour, Deficiency deficiency)
    {
        var (r, g, b) = Linear(colour);
        if (deficiency == Deficiency.None)
        {
            return (r, g, b);
        }

        var m = deficiency switch
        {
            Deficiency.Protanopia => Protan,
            Deficiency.Deuteranopia => Deutan,
            _ => Tritan,
        };
        return (
            Math.Clamp(m[0, 0] * r + m[0, 1] * g + m[0, 2] * b, 0, 1),
            Math.Clamp(m[1, 0] * r + m[1, 1] * g + m[1, 2] * b, 0, 1),
            Math.Clamp(m[2, 0] * r + m[2, 1] * g + m[2, 2] * b, 0, 1));
    }

    /// <summary>CIEDE2000 difference between two colours as seen with <paramref name="deficiency"/>.</summary>
    public static double Difference(Color a, Color b, Deficiency deficiency) =>
        DeltaE2000(Lab(Simulate(a, deficiency)), Lab(Simulate(b, deficiency)));

    /// <summary>WCAG 2 contrast ratio, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(Linear(a)), lb = Luminance(Linear(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static (double R, double G, double B) Linear(Color c) => (ToLinear(c.R), ToLinear(c.G), ToLinear(c.B));

    static double ToLinear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    static double Luminance((double R, double G, double B) c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    /// <summary>CIE L*a*b* (D65) from linear sRGB.</summary>
    public static (double L, double A, double B) Lab((double R, double G, double B) c)
    {
        double x = (0.4124564 * c.R + 0.3575761 * c.G + 0.1804375 * c.B) / 0.95047;
        double y = 0.2126729 * c.R + 0.7151522 * c.G + 0.0721750 * c.B;
        double z = (0.0193339 * c.R + 0.1191920 * c.G + 0.9503041 * c.B) / 1.08883;

        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIEDE2000 (Sharma, Wu and Dalal 2005), with kL = kC = kH = 1.</summary>
    public static double DeltaE2000((double L, double A, double B) x, (double L, double A, double B) y)
    {
        const double Deg = Math.PI / 180;
        double c1 = Math.Sqrt(x.A * x.A + x.B * x.B), c2 = Math.Sqrt(y.A * y.A + y.B * y.B);
        double cMean = (c1 + c2) / 2;
        double c7 = Math.Pow(cMean, 7);
        double g = 0.5 * (1 - Math.Sqrt(c7 / (c7 + Math.Pow(25, 7))));
        double a1 = (1 + g) * x.A, a2 = (1 + g) * y.A;
        double cp1 = Math.Sqrt(a1 * a1 + x.B * x.B), cp2 = Math.Sqrt(a2 * a2 + y.B * y.B);

        static double Hue(double b, double a)
        {
            if (a == 0 && b == 0) return 0;
            double h = Math.Atan2(b, a) / (Math.PI / 180);
            return h < 0 ? h + 360 : h;
        }

        double hp1 = Hue(x.B, a1), hp2 = Hue(y.B, a2);
        double dL = y.L - x.L, dC = cp2 - cp1;
        double dh = cp1 * cp2 == 0 ? 0 : Math.Abs(hp2 - hp1) <= 180 ? hp2 - hp1 : hp2 - hp1 > 180 ? hp2 - hp1 - 360 : hp2 - hp1 + 360;
        double dH = 2 * Math.Sqrt(cp1 * cp2) * Math.Sin(dh / 2 * Deg);

        double lMean = (x.L + y.L) / 2, cpMean = (cp1 + cp2) / 2;
        double hMean = cp1 * cp2 == 0 ? hp1 + hp2
            : Math.Abs(hp1 - hp2) <= 180 ? (hp1 + hp2) / 2
            : hp1 + hp2 < 360 ? (hp1 + hp2 + 360) / 2 : (hp1 + hp2 - 360) / 2;
        double t = 1 - 0.17 * Math.Cos((hMean - 30) * Deg) + 0.24 * Math.Cos(2 * hMean * Deg)
            + 0.32 * Math.Cos((3 * hMean + 6) * Deg) - 0.20 * Math.Cos((4 * hMean - 63) * Deg);
        double dTheta = 30 * Math.Exp(-Math.Pow((hMean - 275) / 25, 2));
        double cp7 = Math.Pow(cpMean, 7);
        double rc = 2 * Math.Sqrt(cp7 / (cp7 + Math.Pow(25, 7)));
        double sl = 1 + 0.015 * Math.Pow(lMean - 50, 2) / Math.Sqrt(20 + Math.Pow(lMean - 50, 2));
        double sc = 1 + 0.045 * cpMean, sh = 1 + 0.015 * cpMean * t;
        double rt = -Math.Sin(2 * dTheta * Deg) * rc;

        return Math.Sqrt(Math.Pow(dL / sl, 2) + Math.Pow(dC / sc, 2) + Math.Pow(dH / sh, 2) + rt * (dC / sc) * (dH / sh));
    }
}
