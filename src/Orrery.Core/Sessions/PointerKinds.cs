namespace Orrery.Core.Sessions;

/// <summary>The standard pointers a remote desktop's shape can be recognized as, to show this computer's own instead.</summary>
public enum PointerKind
{
    Arrow,
    IBeam,
    Hand,
    Wait,
    AppStarting,
    Cross,
    Help,
    No,
    SizeAll,
    SizeNorthSouth,
    SizeWestEast,
    SizeNorthwestSoutheast,
    SizeNortheastSouthwest,
    UpArrow,
}

/// <summary>
/// A pointer's outline: which pixels it covers, whatever their color, and its hotspot. A server sends a picture, not
/// which pointer it is, so a remote shape is told apart by comparing its outline with the standard pointers'.
/// </summary>
public sealed class PointerOutline
{
    public PointerOutline(PointerKind kind, int width, int height, int hotspotX, int hotspotY, bool[] covered)
    {
        if (covered.Length != width * height)
        {
            throw new ArgumentException("The outline doesn't match its size.", nameof(covered));
        }

        Kind = kind;
        Width = width;
        Height = height;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
        Covered = covered;
    }

    public PointerKind Kind { get; }
    public int Width { get; }
    public int Height { get; }
    public int HotspotX { get; }
    public int HotspotY { get; }
    public bool[] Covered { get; }
}

public static class PointerKinds
{
    /// <summary>How much of the two outlines' pixels together both must cover (intersection over union).</summary>
    public const double MinOverlap = 0.85;

    /// <summary>The faintest pixel counted as part of a shape, out of 255: soft shadows' edges aren't.</summary>
    public const int MinAlpha = 48;

    /// <summary>The pixels <paramref name="shape"/> covers. Inverted pixels count: IronRDP draws them opaque.</summary>
    public static bool[] Covered(RemotePointerShape shape)
    {
        var covered = new bool[shape.Width * shape.Height];
        for (int i = 0; i < covered.Length; i++)
        {
            covered[i] = shape.Rgba[i * 4 + 3] >= MinAlpha;
        }

        return covered;
    }

    /// <summary>
    /// The standard pointer <paramref name="shape"/> is, among <paramref name="outlines"/> of the same size and
    /// hotspot (within a pixel), or null when none overlaps it by <see cref="MinOverlap"/>: a shape an app drew itself.
    /// </summary>
    public static PointerKind? Recognize(RemotePointerShape shape, IEnumerable<PointerOutline> outlines)
    {
        if (shape.IsHidden)
        {
            return null;
        }

        bool[]? covered = null;
        PointerKind? best = null;
        double bestOverlap = MinOverlap;
        foreach (var outline in outlines)
        {
            if (outline.Width != shape.Width || outline.Height != shape.Height
                || Math.Abs(outline.HotspotX - shape.HotspotX) > 1 || Math.Abs(outline.HotspotY - shape.HotspotY) > 1)
            {
                continue;
            }

            covered ??= Covered(shape);
            double overlap = Overlap(covered, outline.Covered);
            if (overlap >= bestOverlap)
            {
                (best, bestOverlap) = (outline.Kind, overlap);
            }
        }

        return best;
    }

    static double Overlap(bool[] a, bool[] b)
    {
        int both = 0, either = 0;
        for (int i = 0; i < a.Length; i++)
        {
            both += a[i] && b[i] ? 1 : 0;
            either += a[i] || b[i] ? 1 : 0;
        }

        return either == 0 ? 0 : (double)both / either;
    }
}
