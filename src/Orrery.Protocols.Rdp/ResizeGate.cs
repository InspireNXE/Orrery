using System.Diagnostics;

namespace Orrery.Protocols.Rdp;

/// <summary>A desktop size, and the display scaling in percent, to ask the server for.</summary>
internal readonly record struct DesktopRequest(int Width, int Height, int Scale);

/// <summary>
/// Decides when a resize goes to the server. One isn't sent while the server is still working on the last, as
/// FreeRDP holds them back: a window dragged across the screen would otherwise leave a slow server a queue of sizes
/// to go through one by one, each a full redraw, before the one wanted. The newest size wanted meanwhile is kept, and
/// sent once the server has resized, or once <see cref="AnswerWait"/> has passed without it doing so (a server that
/// leaves the size as it is says nothing). Not thread-safe: the session uses it under its stage lock.
/// </summary>
internal sealed class ResizeGate
{
    /// <summary>How long a resize the server hasn't answered holds back the next.</summary>
    internal static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(1);

    DesktopRequest? _asked;
    long _askedAt;
    DesktopRequest? _waiting;

    internal enum Decision
    {
        /// <summary>Nothing to ask for: the desktop is that already, or about to be.</summary>
        Skip,

        /// <summary>Ask the server now.</summary>
        Send,

        /// <summary>Kept until the server has answered the last resize.</summary>
        Hold,
    }

    /// <summary>Whether a size is kept to send later.</summary>
    public bool HasWaiting => _waiting is not null;

    /// <summary>What to do with a size wanted now, given the desktop's own (<paramref name="current"/>).</summary>
    /// <param name="now">A <see cref="Stopwatch"/> timestamp.</param>
    public Decision Offer(DesktopRequest wanted, DesktopRequest current, long now)
    {
        if (Pending(now))
        {
            // Kept even when it's the size the desktop has now: the server is leaving that for the one asked for.
            _waiting = wanted == _asked ? null : wanted;
            return _waiting is null ? Decision.Skip : Decision.Hold;
        }

        if (Covers(current, wanted))
        {
            _waiting = null;
            return Decision.Skip;
        }

        return Decision.Send;
    }

    /// <summary>The request went to the server.</summary>
    public void Sent(DesktopRequest asked, long now) => (_asked, _askedAt, _waiting) = (asked, now, null);

    /// <summary>Kept to send once the server can take it, as before its display-control channel opens.</summary>
    public void Keep(DesktopRequest wanted) => _waiting = wanted;

    /// <summary>Forgets the size kept.</summary>
    public void Drop() => _waiting = null;

    /// <summary>The server resized. Returns when the request it answered went out, 0 when there was none.</summary>
    public long Answered()
    {
        long at = _askedAt;
        (_asked, _askedAt) = (null, 0);
        return at;
    }

    /// <summary>The size kept, to send now, and forgets it; null when there's none or the last resize is still pending.</summary>
    public DesktopRequest? TakeWaiting(long now)
    {
        if (_waiting is not { } waiting || Pending(now))
        {
            return null;
        }

        _waiting = null;
        return waiting;
    }

    /// <summary>How long until the resize the server hasn't answered stops holding back the next.</summary>
    public TimeSpan HeldFor(long now)
    {
        if (_askedAt == 0)
        {
            return TimeSpan.Zero;
        }

        var left = AnswerWait - Stopwatch.GetElapsedTime(_askedAt, now);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    bool Pending(long now) => _askedAt != 0 && Stopwatch.GetElapsedTime(_askedAt, now) < AnswerWait;

    /// <summary>
    /// Whether the desktop is already what's wanted: the same, or only 2 pixels narrower than a width that's a
    /// multiple of 4. The first size is rounded down to one, as many servers need, so the view laid out asks for those
    /// 2 pixels back about a second after connecting, and the server would reset its graphics and redraw the whole
    /// desktop for them.
    /// </summary>
    internal static bool Covers(DesktopRequest current, DesktopRequest wanted) =>
        wanted.Height == current.Height && wanted.Scale == current.Scale
        && (wanted.Width == current.Width || (current.Width % 4 == 0 && wanted.Width == current.Width + 2));
}
