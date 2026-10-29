namespace Orrery.Core.Sessions;

/// <summary>A rectangle in remote-desktop pixels. Right and bottom are exclusive.</summary>
public readonly record struct FrameRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public FrameRect Union(FrameRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        int x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        return new FrameRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}

public sealed class FrameUpdatedEventArgs(FrameRect damage, bool resized) : EventArgs
{
    public FrameRect Damage { get; } = damage;

    /// <summary>The desktop size changed; the whole frame should be redrawn.</summary>
    public bool Resized { get; } = resized;
}

public enum PointerButton
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}

/// <summary>
/// A session that produces a remote screen as RGBA pixels (RDP, VNC) and accepts keyboard and pointer input.
/// Events are raised on a background thread.
/// </summary>
public interface IFrameBufferSession : IConnectionSession
{
    int Width { get; }
    int Height { get; }

    event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;

    /// <summary>Copies a region of the current frame, as RGBA32, into <paramref name="destination"/>.</summary>
    void CopyRegion(FrameRect region, IntPtr destination, int destinationStride);

    /// <summary>Sends a PC/AT set-1 scancode. Extended keys carry 0xE0 in the high byte (e.g. 0xE048).</summary>
    void SendKey(ushort scancode, bool pressed);

    /// <summary>
    /// Releases every key still held. Call when the view loses focus, which hides the key-up events
    /// (e.g. after Alt+Tab), so the remote side doesn't think Alt is stuck down.
    /// </summary>
    void ReleaseAllKeys();

    void SendPointerMove(int x, int y);
    void SendPointerButton(PointerButton button, bool pressed);

    /// <summary>
    /// Sends wheel rotation in Windows units: 120 per notch, positive is away from the user (scroll up)
    /// or, when <paramref name="horizontal"/>, to the right.
    /// </summary>
    void SendWheel(int units, bool horizontal);

    /// <summary>
    /// Asks the remote side to change its desktop size, in device pixels. The new size arrives later as a
    /// <see cref="FrameUpdatedEventArgs.Resized"/> update; servers that can't resize ignore the request.
    /// </summary>
    void RequestResize(int width, int height);
}
