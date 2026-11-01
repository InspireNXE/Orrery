using Avalonia;
using Orrery.Core.Settings;

namespace Orrery.App.Restore;

/// <summary>
/// Puts a remembered window back on a screen that still exists. A monitor unplugged, or a resolution lowered,
/// since the window was last open must not leave it where it can't be reached.
/// </summary>
static class WindowPlacementFitter
{
    /// <summary>The part of the window that has to be reachable: its title bar, at least this much of it.</summary>
    const int TitleBarHeight = 44, MinVisibleWidth = 120, MinVisibleHeight = 30;

    /// <summary>A screen's usable area (clear of the taskbar) in pixels, and its scale (1.5 for 150%).</summary>
    public readonly record struct ScreenArea(PixelRect WorkArea, double Scaling);

    /// <param name="Position">Null when the window should be centerd instead.</param>
    /// <param name="Size">Device-independent units, no larger than the screen.</param>
    public readonly record struct Fitted(PixelPoint? Position, Size Size);

    /// <param name="screens">The primary screen first.</param>
    /// <param name="minimum">The smallest the window can be; ignored on a screen too small for it.</param>
    public static Fitted Fit(WindowPlacement saved, IReadOnlyList<ScreenArea> screens, Size minimum)
    {
        var size = new Size(saved.Width, saved.Height);
        if (screens.Count == 0)
        {
            return new(null, size);
        }

        // The screen showing most of the title bar. If none shows enough, the window is lost: center it on the main screen.
        ScreenArea? home = null;
        long best = 0;
        foreach (var screen in screens)
        {
            var bar = new PixelRect(saved.X, saved.Y, Pixels(saved.Width, screen.Scaling), Math.Min(Pixels(saved.Height, screen.Scaling), Pixels(TitleBarHeight, screen.Scaling)));
            var shown = bar.Intersect(screen.WorkArea);
            if (shown.Width >= Pixels(MinVisibleWidth, screen.Scaling) && shown.Height >= Pixels(MinVisibleHeight, screen.Scaling) && (long)shown.Width * shown.Height > best)
            {
                (home, best) = (screen, (long)shown.Width * shown.Height);
            }
        }

        var target = home ?? screens[0];
        var room = new Size(target.WorkArea.Width / target.Scaling, target.WorkArea.Height / target.Scaling);
        size = new Size(Math.Min(Math.Max(size.Width, minimum.Width), Math.Max(room.Width, 1)), Math.Min(Math.Max(size.Height, minimum.Height), Math.Max(room.Height, 1)));
        if (home is null)
        {
            return new(null, size);
        }

        // Entirely inside the screen's usable area when it fits.
        int width = Pixels(size.Width, target.Scaling), height = Pixels(size.Height, target.Scaling);
        int x = Math.Clamp(saved.X, target.WorkArea.X, Math.Max(target.WorkArea.X, target.WorkArea.Right - width));
        int y = Math.Clamp(saved.Y, target.WorkArea.Y, Math.Max(target.WorkArea.Y, target.WorkArea.Bottom - height));
        return new(new PixelPoint(x, y), size);
    }

    static int Pixels(double units, double scaling) => (int)Math.Round(units * scaling);
}
