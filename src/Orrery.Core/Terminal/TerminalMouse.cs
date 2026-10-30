using System.Text;

namespace Orrery.Core.Terminal;

/// <summary>Which mouse events a program asked for (xterm DEC private modes 9, 1000, 1002, 1003).</summary>
public enum MouseTracking
{
    None,

    /// <summary>Mode 9: button presses only, no modifiers.</summary>
    Press,

    /// <summary>Mode 1000: presses and releases, and the wheel.</summary>
    Normal,

    /// <summary>Mode 1002: also movement while a button is held (dragging).</summary>
    ButtonMotion,

    /// <summary>Mode 1003: all movement.</summary>
    AnyMotion,
}

public enum TerminalMouseButton
{
    Left,
    Middle,
    Right,
    WheelUp,
    WheelDown,

    /// <summary>Movement with no button held.</summary>
    None,
}

public enum TerminalMouseEvent
{
    Press,
    Release,
    Motion,
}

/// <summary>Encodes mouse events the way xterm reports them to programs such as htop, tmux and vim.</summary>
public static class TerminalMouse
{
    // The classic encoding puts 32 + value in one byte, so positions past 223 can't be sent.
    const int ClassicLimit = 223;

    /// <summary>Whether a program in <paramref name="tracking"/> mode wants this event.</summary>
    public static bool Wants(MouseTracking tracking, TerminalMouseEvent kind, TerminalMouseButton button)
    {
        bool wheel = button is TerminalMouseButton.WheelUp or TerminalMouseButton.WheelDown;
        return tracking != MouseTracking.None && kind switch
        {
            TerminalMouseEvent.Press => true,
            TerminalMouseEvent.Release => tracking != MouseTracking.Press && !wheel, // The wheel has no release.
            _ => tracking == MouseTracking.AnyMotion || (tracking == MouseTracking.ButtonMotion && button != TerminalMouseButton.None),
        };
    }

    /// <summary>
    /// The bytes to send for an event at <paramref name="column"/>, <paramref name="row"/> (zero-based cells),
    /// or null when the classic encoding can't express the position.
    /// </summary>
    public static byte[]? Encode(TerminalMouseEvent kind, TerminalMouseButton button, int column, int row,
        TerminalModifiers modifiers, bool sgr, bool pressOnly = false)
    {
        int code = button switch
        {
            TerminalMouseButton.Left => 0,
            TerminalMouseButton.Middle => 1,
            TerminalMouseButton.Right => 2,
            TerminalMouseButton.WheelUp => 64,
            TerminalMouseButton.WheelDown => 65,
            _ => 3, // Motion with no button.
        };

        if (kind == TerminalMouseEvent.Motion)
        {
            code += 32;
        }

        // Mode 9 reports no modifiers.
        if (!pressOnly)
        {
            if (modifiers.HasFlag(TerminalModifiers.Shift)) code += 4;
            if (modifiers.HasFlag(TerminalModifiers.Alt)) code += 8;
            if (modifiers.HasFlag(TerminalModifiers.Control)) code += 16;
        }

        if (sgr)
        {
            // SGR keeps the button on release and says so with a lowercase m.
            char final = kind == TerminalMouseEvent.Release ? 'm' : 'M';
            return Encoding.ASCII.GetBytes($"\u001b[<{code};{column + 1};{row + 1}{final}");
        }

        if (column + 1 > ClassicLimit || row + 1 > ClassicLimit)
        {
            return null;
        }

        // The classic encoding can't say which button was released.
        if (kind == TerminalMouseEvent.Release)
        {
            code = (code & ~3) | 3;
        }

        return [0x1B, (byte)'[', (byte)'M', (byte)(32 + code), (byte)(32 + column + 1), (byte)(32 + row + 1)];
    }
}
