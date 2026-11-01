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

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
