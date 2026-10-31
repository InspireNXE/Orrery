using System.Globalization;
using Orrery.Core.Sessions;

namespace Orrery.Core.Connections;

/// <summary>
/// An RDP desktop size as typed in a connection's settings: empty for "fit the window" (and follow it as it
/// resizes), or <c>1920x1080</c> for a fixed size the view scales to fit.
/// </summary>
public static class DesktopResolution
{
    /// <summary>MS-RDPEDISP's smallest side, which servers also hold the starting size to.</summary>
    public const int MinSide = 200;

    /// <summary>
    /// Reads <paramref name="text"/>. True with a null size for "fit"; true with a size for a fixed one; false,
    /// with <paramref name="error"/> set, when it isn't either.
    /// </summary>
    public static bool TryParse(string? text, out (int Width, int Height)? size, out string? error)
    {
        (size, error) = (null, null);
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return true;
        }

        var parts = text.Split(['x', 'X', '×', '*'], StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height))
        {
            error = "The desktop size should look like 1920x1080, or be empty to fit the window.";
            return false;
        }

        if (width is < MinSide or > DesktopLimits.MaxSide || height is < MinSide or > DesktopLimits.MaxSide)
        {
            error = $"Each side of the desktop must be from {MinSide} to {DesktopLimits.MaxSide} pixels.";
            return false;
        }

        // Many RDP servers need the width to be a multiple of 4.
        size = (width - width % 4, height);
        return true;
    }
}
