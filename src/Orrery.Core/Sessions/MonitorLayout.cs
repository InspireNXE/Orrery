namespace Orrery.Core.Sessions;

/// <summary>A display of this computer, in its desktop's pixels.</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height, bool IsPrimary)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>A monitor of the remote desktop, placed relative to its primary monitor, which is at 0,0.</summary>
public readonly record struct RemoteMonitor(int Left, int Top, int Width, int Height, bool IsPrimary);

/// <summary>A remote desktop that can be spread over several displays of this computer, one monitor for each.</summary>
public interface IMultiMonitorSession
{
    /// <summary>
    /// Asks the server for these monitors (<see cref="MonitorLayout.Arrange"/>). False when it can't take a layout now,
    /// such as before its display-control channel is open; <see cref="IFrameBufferSession.RequestResize"/> still works.
    /// </summary>
    bool RequestMonitorLayout(IReadOnlyList<RemoteMonitor> monitors);
}

/// <summary>
/// Turns this computer's displays into the remote desktop's monitors, as MS-RDPEDISP takes them: one primary at 0,0
/// and the others around it, each from 200 to 8192 pixels a side, none overlapping and each touching another.
/// Displays chosen with a gap between them, such as the outer two of three, are moved together for the remote
/// desktop, as mstsc does with selectedmonitors, so it stays one piece; here they stay where they are.
/// </summary>
public static class MonitorLayout
{
    public const int MinSide = 200;

    /// <summary>
    /// The monitors for <paramref name="screens"/>, the primary display the primary monitor (the first, when none is),
    /// with the area they cover together. Null with <paramref name="whyNot"/> set when they can't be one desktop.
    /// </summary>
    public static IReadOnlyList<RemoteMonitor>? Arrange(IReadOnlyList<ScreenArea> screens, out ScreenArea covered, out string? whyNot) =>
        Arrange(screens, out covered, out _, out whyNot);

    /// <param name="movedTogether">True when displays had to be moved together: the monitors don't sit as the displays do.</param>
    public static IReadOnlyList<RemoteMonitor>? Arrange(IReadOnlyList<ScreenArea> screens, out ScreenArea covered, out bool movedTogether, out string? whyNot)
    {
        covered = default;
        movedTogether = false;
        whyNot = null;
        if (screens.Count < 2)
        {
            whyNot = "This computer has only one display.";
            return null;
        }

        if (screens.Any(s => s.Width is < MinSide or > DesktopLimits.MaxSide || s.Height is < MinSide or > DesktopLimits.MaxSide))
        {
            whyNot = $"A display is smaller than {MinSide} or larger than {DesktopLimits.MaxSide} pixels a side.";
            return null;
        }

        for (int i = 0; i < screens.Count; i++)
        {
            for (int j = i + 1; j < screens.Count; j++)
            {
                if (Overlap(screens[i], screens[j]))
                {
                    whyNot = "Two displays overlap, as mirrored displays do.";
                    return null;
                }
            }
        }

        var placed = screens;
        if (!AllJoined(placed))
        {
            placed = MovedTogether(screens);
            movedTogether = true;
            if (!AllJoined(placed))
            {
                whyNot = "A display doesn't sit against the others, even with the gaps between them closed, so the remote desktop can't reach it.";
                return null;
            }
        }

        int width = placed.Max(s => s.Right) - placed.Min(s => s.X), height = placed.Max(s => s.Bottom) - placed.Min(s => s.Y);
        if (width > DesktopLimits.MaxSide || height > DesktopLimits.MaxSide)
        {
            whyNot = $"Together the displays are {width}x{height} pixels, larger than the {DesktopLimits.MaxSide} a side Orrery supports.";
            return null;
        }

        int left = screens.Min(s => s.X), top = screens.Min(s => s.Y);
        covered = new ScreenArea(left, top, screens.Max(s => s.Right) - left, screens.Max(s => s.Bottom) - top, IsPrimary: false);
        int primaryIndex = screens.Select((s, i) => (s, i)).FirstOrDefault(p => p.s.IsPrimary, (default, 0)).i;
        var primary = placed[primaryIndex];
        return placed.Select((s, i) => new RemoteMonitor(s.X - primary.X, s.Y - primary.Y, s.Width, s.Height, i == primaryIndex)).ToList();
    }

    /// <summary>
    /// The displays with the empty columns and rows between them taken out: each moves left by the width of the gaps
    /// to its left, and up by the height of those above, so they keep their order and meet where a gap was.
    /// </summary>
    internal static IReadOnlyList<ScreenArea> MovedTogether(IReadOnlyList<ScreenArea> screens)
    {
        var dx = GapsBefore(screens.Select(s => (s.X, s.Right)).ToList());
        var dy = GapsBefore(screens.Select(s => (s.Y, s.Bottom)).ToList());
        return screens.Select((s, i) => s with { X = s.X - dx[i], Y = s.Y - dy[i] }).ToList();
    }

    /// <summary>For each span, how much of the line before its start no span covers, after the first one.</summary>
    static int[] GapsBefore(IReadOnlyList<(int Start, int End)> spans)
    {
        var gaps = new List<(int End, int Length)>();
        int reached = int.MinValue;
        foreach (var (start, end) in spans.OrderBy(s => s.Start))
        {
            if (reached != int.MinValue && start > reached)
            {
                gaps.Add((start, start - reached));
            }

            reached = Math.Max(reached, end);
        }

        return spans.Select(s => gaps.Where(g => g.End <= s.Start).Sum(g => g.Length)).ToArray();
    }

    /// <summary>Every display reachable from the first through displays it sits against, so no group stands apart.</summary>
    static bool AllJoined(IReadOnlyList<ScreenArea> screens)
    {
        var reached = new bool[screens.Count];
        var next = new Stack<int>([0]);
        reached[0] = true;
        while (next.TryPop(out int i))
        {
            for (int j = 0; j < screens.Count; j++)
            {
                if (!reached[j] && Touch(screens[i], screens[j]))
                {
                    reached[j] = true;
                    next.Push(j);
                }
            }
        }

        return reached.All(r => r);
    }

    static bool Overlap(ScreenArea a, ScreenArea b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    /// <summary>Sharing part of an edge: side by side with some height in common, or one above the other with some width.</summary>
    static bool Touch(ScreenArea a, ScreenArea b) =>
        ((a.Right == b.X || b.Right == a.X) && a.Y < b.Bottom && b.Y < a.Bottom)
        || ((a.Bottom == b.Y || b.Bottom == a.Y) && a.X < b.Right && b.X < a.Right);
}
