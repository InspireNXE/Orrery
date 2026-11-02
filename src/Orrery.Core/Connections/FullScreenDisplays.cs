using Orrery.Core.Sessions;

namespace Orrery.Core.Connections;

/// <summary>Which of this computer's displays a remote desktop fills in full screen.</summary>
public enum FullScreenOn
{
    /// <summary>The display the window is on.</summary>
    ThisDisplay,

    /// <summary>Every display, each one a monitor of the remote desktop.</summary>
    EveryDisplay,

    /// <summary>The displays in <see cref="FullScreenChoice.Displays"/>: one to always open there, or several.</summary>
    ChosenDisplays,
}

/// <summary>
/// A display chosen for full screen, remembered by where it sits on this computer's desktop and its size, in pixels,
/// and its name when the system gives one. The position, not a number, is what's kept, since Windows renumbers
/// displays when they're plugged in again in another order.
/// </summary>
public sealed record ChosenDisplay(int X, int Y, int Width, int Height, string? Name = null)
{
    public static ChosenDisplay Of(ScreenArea screen, string? name = null) => new(screen.X, screen.Y, screen.Width, screen.Height, name);

    public bool Is(ScreenArea screen) => screen.X == X && screen.Y == Y && screen.Width == Width && screen.Height == Height;
}

/// <summary>The resolved full-screen choice; <see cref="Displays"/> is empty unless <see cref="On"/> is chosen displays.</summary>
public sealed record FullScreenChoice(FullScreenOn On, IReadOnlyList<ChosenDisplay> Displays)
{
    public static readonly FullScreenChoice ThisDisplay = new(FullScreenOn.ThisDisplay, []);
    public static readonly FullScreenChoice EveryDisplay = new(FullScreenOn.EveryDisplay, []);

    /// <summary>
    /// The displays of <paramref name="screens"/> this picks, in their order, and how many chosen ones aren't there
    /// now. A display is found by its position and size, or else by its name, so one that only changed resolution
    /// still counts.
    /// </summary>
    public IReadOnlyList<int> Pick(IReadOnlyList<ScreenArea> screens, IReadOnlyList<string?> names, out int missing)
    {
        missing = 0;
        switch (On)
        {
            case FullScreenOn.EveryDisplay:
                return Enumerable.Range(0, screens.Count).ToList();
            case FullScreenOn.ThisDisplay:
                return [];
        }

        var picked = new SortedSet<int>();
        foreach (var display in Displays)
        {
            int found = Enumerable.Range(0, screens.Count).FirstOrDefault(i => display.Is(screens[i]), -1);
            if (found < 0 && display.Name is { Length: > 0 } name)
            {
                found = Enumerable.Range(0, screens.Count).FirstOrDefault(i => !picked.Contains(i) && names.ElementAtOrDefault(i) == name, -1);
            }

            if (found < 0)
            {
                missing++;
            }
            else
            {
                picked.Add(found);
            }
        }

        return picked.ToList();
    }
}
