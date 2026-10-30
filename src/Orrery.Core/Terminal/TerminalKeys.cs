namespace Orrery.Core.Terminal;

public enum TerminalKey
{
    Enter, Backspace, Tab, Escape,
    Up, Down, Right, Left,
    Home, End, Insert, Delete, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

[Flags]
public enum TerminalModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
}

/// <summary>What xterm sends for special keys. Printable text is sent as typed (UTF-8).</summary>
public static class TerminalKeys
{
    /// <param name="shellPrompt">
    /// True on the main screen, where a shell's line editor is usually reading. Ctrl with arrows, Backspace
    /// and Delete then sends the word commands every line editor binds by default (bash, zsh, fish:
    /// ESC b, ESC f, ESC DEL, ESC d), since the xterm sequences for Ctrl+arrow often aren't bound.
    /// Full-screen programs get xterm's sequences, with ^W for Ctrl+Backspace (an ESC would end vim's insert mode).
    /// </param>
    public static string Encode(TerminalKey key, TerminalModifiers modifiers, bool cursorKeysApplication, bool shellPrompt = false)
    {
        if (modifiers == TerminalModifiers.Control)
        {
            string? word = (key, shellPrompt) switch
            {
                (TerminalKey.Left, true) => "b",
                (TerminalKey.Right, true) => "f",
                (TerminalKey.Backspace, true) => "",
                (TerminalKey.Delete, true) => "d",
                (TerminalKey.Backspace, false) => "",
                _ => null,
            };
            if (word is not null)
            {
                return word;
            }
        }

        // xterm's modifier parameter: 1 + Shift(1) + Alt(2) + Control(4).
        int mod = 1 + (int)modifiers;
        bool modified = modifiers != TerminalModifiers.None;

        string Cursor(char final) =>
            modified ? $"\u001b[1;{mod}{final}" : cursorKeysApplication ? $"\u001bO{final}" : $"\u001b[{final}";

        string Tilde(int code) => modified ? $"\u001b[{code};{mod}~" : $"\u001b[{code}~";

        string Ss3(char final) => modified ? $"\u001b[1;{mod}{final}" : $"\u001bO{final}";

        return key switch
        {
            TerminalKey.Enter => modifiers.HasFlag(TerminalModifiers.Alt) ? "\u001b\r" : "\r",
            TerminalKey.Backspace => modifiers.HasFlag(TerminalModifiers.Control) ? "\b"
                : modifiers.HasFlag(TerminalModifiers.Alt) ? "\u001b\u007f" : "\u007f",
            TerminalKey.Tab => modifiers.HasFlag(TerminalModifiers.Shift) ? "\u001b[Z" : "\t",
            TerminalKey.Escape => "\u001b",
            TerminalKey.Up => Cursor('A'),
            TerminalKey.Down => Cursor('B'),
            TerminalKey.Right => Cursor('C'),
            TerminalKey.Left => Cursor('D'),
            TerminalKey.Home => Cursor('H'),
            TerminalKey.End => Cursor('F'),
            TerminalKey.Insert => Tilde(2),
            TerminalKey.Delete => Tilde(3),
            TerminalKey.PageUp => Tilde(5),
            TerminalKey.PageDown => Tilde(6),
            TerminalKey.F1 => Ss3('P'),
            TerminalKey.F2 => Ss3('Q'),
            TerminalKey.F3 => Ss3('R'),
            TerminalKey.F4 => Ss3('S'),
            TerminalKey.F5 => Tilde(15),
            TerminalKey.F6 => Tilde(17),
            TerminalKey.F7 => Tilde(18),
            TerminalKey.F8 => Tilde(19),
            TerminalKey.F9 => Tilde(20),
            TerminalKey.F10 => Tilde(21),
            TerminalKey.F11 => Tilde(23),
            TerminalKey.F12 => Tilde(24),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Control+letter and friends as the single control byte a terminal sends (Ctrl+C → 0x03).
    /// Null when the character has no control form.
    /// </summary>
    public static string? ControlCharacter(char c) => c switch
    {
        >= 'a' and <= 'z' => ((char)(c - 'a' + 1)).ToString(),
        >= 'A' and <= 'Z' => ((char)(c - 'A' + 1)).ToString(),
        ' ' or '@' or '2' => "\0",
        '[' or '3' => "\u001b",
        '\\' or '4' => "\u001c",
        ']' or '5' => "\u001d",
        '^' or '6' => "\u001e",
        '_' or '-' or '7' => "\u001f",
        '?' or '8' => "\u007f",
        _ => null,
    };

    /// <summary>Wraps pasted text for bracketed-paste mode and strips anything that could end the bracket early.</summary>
    public static string Paste(string text, bool bracketed)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        return bracketed ? $"\u001b[200~{text.Replace("\u001b[201~", string.Empty)}\u001b[201~" : text;
    }
}
