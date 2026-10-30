using Avalonia.Input;

namespace Orrery.App.Input;

/// <summary>
/// Turns Avalonia key events into X11 keysyms for VNC. Character keys send what they type on the local
/// layout (so 'é' or 'ß' arrive as typed); special keys send their named keysym.
/// </summary>
static class KeysymMap
{
    static readonly Dictionary<Key, uint> Special = new()
    {
        [Key.Back] = 0xFF08, [Key.Tab] = 0xFF09, [Key.Enter] = 0xFF0D, [Key.Pause] = 0xFF13,
        [Key.Scroll] = 0xFF14, [Key.Escape] = 0xFF1B, [Key.Delete] = 0xFFFF, [Key.Space] = 0x0020,
        [Key.Home] = 0xFF50, [Key.Left] = 0xFF51, [Key.Up] = 0xFF52, [Key.Right] = 0xFF53, [Key.Down] = 0xFF54,
        [Key.PageUp] = 0xFF55, [Key.PageDown] = 0xFF56, [Key.End] = 0xFF57, [Key.Insert] = 0xFF63,
        [Key.PrintScreen] = 0xFF61, [Key.Apps] = 0xFF67, [Key.NumLock] = 0xFF7F, [Key.CapsLock] = 0xFFE5,
        [Key.LeftShift] = 0xFFE1, [Key.RightShift] = 0xFFE2, [Key.LeftCtrl] = 0xFFE3, [Key.RightCtrl] = 0xFFE4,
        [Key.LeftAlt] = 0xFFE9, [Key.RightAlt] = 0xFFEA, [Key.LWin] = 0xFFEB, [Key.RWin] = 0xFFEC,
        [Key.Multiply] = 0xFFAA, [Key.Add] = 0xFFAB, [Key.Subtract] = 0xFFAD, [Key.Decimal] = 0xFFAE, [Key.Divide] = 0xFFAF,
    };

    /// <summary>The keysym for a key press, or null for keys VNC has no use for.</summary>
    public static uint? Get(Key key, PhysicalKey physical, string? text)
    {
        if (physical == PhysicalKey.NumPadEnter)
        {
            return 0xFF8D;
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            return 0xFFBE + (uint)(key - Key.F1);
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            return 0xFFB0 + (uint)(key - Key.NumPad0);
        }

        if (Special.TryGetValue(key, out var keysym))
        {
            return keysym;
        }

        // What the key types, when it types one visible character. With Ctrl held the text is a
        // control character (or nothing), so fall back to the key itself.
        if (text is { Length: > 0 } && char.ConvertToUtf32(text, 0) is var codepoint && codepoint >= 0x20 && codepoint != 0x7F
            && text.Length == char.ConvertFromUtf32(codepoint).Length)
        {
            return FromCodepoint(codepoint);
        }

        return key switch
        {
            >= Key.A and <= Key.Z => (uint)('a' + (key - Key.A)),
            >= Key.D0 and <= Key.D9 => (uint)('0' + (key - Key.D0)),
            _ => null,
        };
    }

    /// <summary>Latin-1 keysyms equal the character; everything else uses the Unicode keysym range.</summary>
    public static uint FromCodepoint(int codepoint) => codepoint < 0x100 ? (uint)codepoint : 0x0100_0000u | (uint)codepoint;
}
