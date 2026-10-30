namespace Orrery.Core.Sessions;

/// <summary>
/// The largest remote desktop Orrery shows. RDP and VNC servers announce their own desktop size, so it is
/// checked before anything is allocated for it. 8192 is the most MS-RDPEDISP lets a client ask for, and an
/// 8192x8192 RGBA frame is 256 MiB.
/// </summary>
public static class DesktopLimits
{
    public const int MaxSide = 8192;

    /// <summary>Whether both sides are from 1 to <see cref="MaxSide"/> pixels.</summary>
    public static bool IsSupported(int width, int height) => width is >= 1 and <= MaxSide && height is >= 1 and <= MaxSide;

    /// <summary>Why a session ends when the server announces a size that isn't <see cref="IsSupported"/>.</summary>
    public static string RefusalMessage(int width, int height) => width > MaxSide || height > MaxSide
        ? $"The server asked for a {width}x{height} desktop, which is larger than Orrery supports."
        : $"The server asked for a {width}x{height} desktop, which Orrery can't show.";
}
