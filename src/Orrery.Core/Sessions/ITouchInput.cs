namespace Orrery.Core.Sessions;

/// <summary>Where a finger is in its touch.</summary>
public enum TouchContactPhase
{
    Started,
    Moved,
    Ended,

    /// <summary>The touch was taken away, such as by the window losing it; the remote side drops it.</summary>
    Canceled,
}

/// <summary>Where a pen is: over the screen without touching it, on it, or gone out of range.</summary>
public enum PenContactPhase
{
    Hovering,
    Touching,
    OutOfRange,
    Canceled,
}

/// <summary>A pen's state with each report. Values that weren't reported are null.</summary>
/// <param name="Pressure">0 to 1.</param>
/// <param name="TiltX">Degrees, -90 to 90.</param>
/// <param name="TiltY">Degrees, -90 to 90.</param>
/// <param name="Rotation">Degrees clockwise, 0 to 359.</param>
public readonly record struct PenState(double? Pressure, bool Barrel, bool Eraser, bool Inverted, double? TiltX = null, double? TiltY = null, double? Rotation = null);

/// <summary>
/// A session that takes touch and pen as themselves rather than as the mouse: RDP, over its input channel (MS-RDPEI),
/// so the remote computer sees fingers, gestures and pen pressure. Until the server opens that channel, and on servers
/// without it, <see cref="AcceptsTouch"/> is false and the view sends touches and pens as the mouse. Positions are in
/// remote desktop pixels.
/// </summary>
public interface ITouchInput
{
    /// <summary>Whether touches can go as touches right now.</summary>
    bool AcceptsTouch { get; }

    /// <summary>Whether a pen can go as a pen right now.</summary>
    bool AcceptsPen { get; }

    /// <param name="contact">Tells fingers apart: the same for every report of one touch.</param>
    /// <param name="pressure">0 to 1, or null when the screen doesn't measure it.</param>
    void SendTouch(ulong contact, TouchContactPhase phase, int x, int y, double? pressure);

    /// <param name="pen">Tells pens apart: the same for every report of one pen.</param>
    void SendPen(ulong pen, PenContactPhase phase, int x, int y, PenState state);
}
