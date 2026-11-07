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

/// <summary>
/// A session that can tell the server this display's scaling, so the remote desktop is drawn at full resolution and
/// sized like local apps (RDP, as mstsc does). The view then asks for desktop sizes in device pixels.
/// </summary>
public interface IDisplayScaleSession
{
    /// <summary>Whether the session sends the scaling, as it was started to.</summary>
    bool FollowsDisplayScale { get; }

    /// <summary>
    /// The display's scaling (1.5 for 150%), sent with the next <see cref="IFrameBufferSession.RequestResize"/> or
    /// monitor layout. Ignored unless <see cref="FollowsDisplayScale"/>.
    /// </summary>
    void SetDisplayScale(double renderScaling);
}

/// <summary>
/// A session that takes keys as X11 keysyms (what a key types, e.g. 'a' or Shift_L) rather than scancodes
/// (where the key sits). VNC works this way, so the remote side doesn't need the local keyboard layout.
/// </summary>
public interface IKeysymInput
{
    void SendKeysym(uint keysym, bool pressed);
}

/// <summary>
/// A session that shares clipboard text with the remote side and leaves it to the view to talk to the local
/// clipboard: VNC, and RDP on Linux and macOS. RDP on Windows talks to the local clipboard inside IronRDP instead.
/// </summary>
public interface IClipboardSession
{
    /// <summary>Whether this session exchanges clipboard text through this interface at all.</summary>
    bool ExchangesClipboard => true;

    /// <summary>The remote clipboard changed. Raised on a background thread.</summary>
    event EventHandler<string>? RemoteClipboardChanged;

    /// <summary>Puts <paramref name="text"/> on the remote clipboard.</summary>
    void SendClipboard(string text);

    /// <summary>
    /// Something copied wasn't shared, or a copy between the two sides failed, with a sentence saying why, for the user.
    /// Raised on a background thread.
    /// </summary>
    event EventHandler<string>? ClipboardRefused { add { } remove { } }

    /// <summary>Whether images and files are shared too, through the members below.</summary>
    bool ExchangesFiles => false;

    /// <summary>The remote side copied an image, encoded as PNG or BMP. Raised on a background thread.</summary>
    event EventHandler<byte[]>? RemoteClipboardImageChanged { add { } remove { } }

    /// <summary>
    /// The remote side copied files, now downloaded: the paths of the copied items in a private folder that lasts
    /// until the session ends or the next files are copied. Raised on a background thread.
    /// </summary>
    event EventHandler<IReadOnlyList<string>>? RemoteClipboardFilesChanged { add { } remove { } }

    /// <summary>
    /// Files the remote side copied finished downloading after something newer was copied, so they weren't put on the
    /// clipboard over it: the paths of the copied items, kept until the session ends. Raised on a background thread.
    /// </summary>
    event EventHandler<IReadOnlyList<string>>? RemoteClipboardFilesReady { add { } remove { } }

    /// <summary>How a download of copied files is going: raised as it starts, as it goes and once as it ends. Raised on a background thread.</summary>
    event EventHandler<ClipboardTransfer>? ClipboardTransferChanged { add { } remove { } }

    /// <summary>Puts <paramref name="image"/> on the remote clipboard.</summary>
    void SendClipboardImage(ClipboardImage image) { }

    /// <summary>Puts the files and folders at <paramref name="paths"/> on the remote clipboard; the remote reads them when it pastes.</summary>
    void SendClipboardFiles(IReadOnlyList<string> paths) { }

    /// <summary>Stops the download of copied files, if one is going.</summary>
    void CancelClipboardTransfer() { }
}

/// <summary>A clipboard image: <paramref name="Bgra"/> holds its pixels top row first, 4 bytes each, alpha not premultiplied.</summary>
/// <param name="Png">The same image as PNG, for a remote side that takes it.</param>
public sealed record ClipboardImage(int Width, int Height, byte[] Bgra, byte[] Png);

/// <summary>A download of copied files: <paramref name="Done"/> of <paramref name="Total"/> bytes, finished once <paramref name="Ended"/>.</summary>
public sealed record ClipboardTransfer(int Files, long Done, long Total, bool Ended);
