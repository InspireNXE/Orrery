using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Orrery.Core.Sessions;

namespace Orrery.App.Sessions;

/// <summary>
/// Puts a borderless window over every display at once, for a remote desktop spread over them. Windows only: Wayland
/// doesn't let a window place itself or span outputs, and macOS gives each display its own full-screen space.
/// </summary>
/// <remarks>
/// The size is set in pixels with SetWindowPos rather than through Avalonia, whose sizes are in the units of one
/// display's scaling. Placed twice: when the window lands on a display with another scaling, Windows sends it a new
/// size for that scaling, and the second placement, now on that display, covers the area again.
/// </remarks>
[SupportedOSPlatform("windows")]
static class SpanningWindow
{
    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    /// <summary>Covers <paramref name="area"/>, in the desktop's pixels. False when the window has no handle yet.</summary>
    public static bool Cover(Window window, ScreenArea area)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
        {
            return false;
        }

        return SetWindowPos(handle, IntPtr.Zero, area.X, area.Y, area.Width, area.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Limits the window to <paramref name=areas/>, in the desktop's pixels, so displays between chosen ones show
    /// what's under the window and take its clicks; null gives it all of its rectangle back.
    /// </summary>
    public static bool Limit(Window window, ScreenArea covered, IReadOnlyList<ScreenArea>? areas)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
        {
            return false;
        }

        if (areas is null)
        {
            return SetWindowRgn(handle, IntPtr.Zero, true) != 0;
        }

        // Windows owns the region once it's set, so it's only deleted here when setting it fails.
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        foreach (var area in areas)
        {
            IntPtr part = CreateRectRgn(area.X - covered.X, area.Y - covered.Y, area.Right - covered.X, area.Bottom - covered.Y);
            CombineRgn(region, region, part, RGN_OR);
            DeleteObject(part);
        }

        if (SetWindowRgn(handle, region, true) == 0)
        {
            DeleteObject(region);
            return false;
        }

        return true;
    }

    const int RGN_OR = 2;

    [DllImport("user32.dll")]
    static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
