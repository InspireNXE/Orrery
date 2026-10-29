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

    void SendPointerMove(int x, int y);
    void SendPointerButton(PointerButton button, bool pressed);
}
