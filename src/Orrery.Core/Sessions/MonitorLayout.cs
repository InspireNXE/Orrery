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
/// </summary>
public static class MonitorLayout
{
    public const int MinSide = 200;

    /// <summary>
    /// The monitors for <paramref name="screens"/>, the primary display the primary monitor (the first, when none is),
    /// with the area they cover together. Null with <paramref name="whyNot"/> set when they can't be one desktop.
    /// </summary>
    public static IReadOnlyList<RemoteMonitor>? Arrange(IReadOnlyList<ScreenArea> screens, out ScreenArea covered, out string? whyNot)
    {
        covered = default;
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

        if (!AllJoined(screens))
        {
            whyNot = "A display doesn't sit against the others, so the remote desktop can't reach it.";
            return null;
        }

        int left = screens.Min(s => s.X), top = screens.Min(s => s.Y);
        int right = screens.Max(s => s.Right), bottom = screens.Max(s => s.Bottom);
        if (right - left > DesktopLimits.MaxSide || bottom - top > DesktopLimits.MaxSide)
        {
            whyNot = $"Together the displays are {right - left}x{bottom - top} pixels, larger than the {DesktopLimits.MaxSide} a side Orrery supports.";
            return null;
        }

        covered = new ScreenArea(left, top, right - left, bottom - top, IsPrimary: false);
        var primary = screens.FirstOrDefault(s => s.IsPrimary) is { Width: > 0 } chosen ? chosen : screens[0];
        return screens.Select(s => new RemoteMonitor(s.X - primary.X, s.Y - primary.Y, s.Width, s.Height, s == primary)).ToList();
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
