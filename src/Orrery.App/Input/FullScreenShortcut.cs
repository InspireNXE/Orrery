using Avalonia.Input;

namespace Orrery.App.Input;

/// <summary>Which desktop's rules a shortcut is checked against.</summary>
public enum ShortcutPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// The key that turns full screen on and off. It reaches Orrery before a session sees it, so it must never be
/// something a person types into a session, something the system keeps for itself, or another Orrery shortcut.
/// <see cref="Problem"/> says why a shortcut can't be used; settings keep only shortcuts that pass.
/// </summary>
public static class FullScreenShortcut
{
    public static ShortcutPlatform CurrentPlatform =>
        OperatingSystem.IsMacOS() ? ShortcutPlatform.MacOS : OperatingSystem.IsWindows() ? ShortcutPlatform.Windows : ShortcutPlatform.Linux;

    /// <summary>Ctrl+Alt+Enter: every keyboard has Enter, and it types nothing. On macOS, the system's Ctrl+Cmd+F.</summary>
    public static KeyGesture DefaultFor(ShortcutPlatform platform) => platform == ShortcutPlatform.MacOS
        ? new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Meta)
        : new KeyGesture(Key.Enter, KeyModifiers.Control | KeyModifiers.Alt);

    public static KeyGesture Default => DefaultFor(CurrentPlatform);

    /// <summary>The saved shortcut, or the default when there's none or it no longer passes the checks.</summary>
    public static KeyGesture FromSettings(string? stored, ShortcutPlatform? platform = null)
    {
        var on = platform ?? CurrentPlatform;
        if (stored is not null && Parse(stored) is { } gesture && Problem(gesture, on) is null)
        {
            return gesture;
        }

        return DefaultFor(on);
    }

    /// <summary>What to save: null for the default, so a later default reaches people who never changed it.</summary>
    public static string? ToSettings(KeyGesture gesture, ShortcutPlatform? platform = null) =>
        SameAs(gesture, DefaultFor(platform ?? CurrentPlatform)) ? null : Format(gesture);

    /// <summary>A key press as a shortcut, or null while only modifiers are down.</summary>
    public static KeyGesture? FromKeyPress(Key key, KeyModifiers modifiers) => IsModifier(key)
        ? null
        : new KeyGesture(Normalise(key), modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta));

    public static bool Matches(KeyGesture gesture, Key key, KeyModifiers modifiers) =>
        Normalise(key) == Normalise(gesture.Key) && modifiers == gesture.KeyModifiers;

    public static bool SameAs(KeyGesture a, KeyGesture b) => Matches(a, b.Key, b.KeyModifiers);

    /// <summary>Why <paramref name="gesture"/> can't be the full-screen shortcut, or null if it can.</summary>
    public static string? Problem(KeyGesture gesture, ShortcutPlatform? platform = null)
    {
        var on = platform ?? CurrentPlatform;
        var mods = gesture.KeyModifiers;
        var key = Normalise(gesture.Key);
        bool mac = on == ShortcutPlatform.MacOS;

        if (IsModifier(key))
        {
            return "it needs a key besides the modifiers.";
        }

        if (!mac && mods.HasFlag(KeyModifiers.Meta))
        {
            return on == ShortcutPlatform.Windows
                ? "Windows keeps shortcuts with the Windows key for itself."
                : "the desktop usually keeps shortcuts with the Super key for itself.";
        }

        int count = new[] { KeyModifiers.Control, KeyModifiers.Alt, KeyModifiers.Shift, KeyModifiers.Meta }.Count(m => mods.HasFlag(m));
        // Two modifiers, so it's never something typed in a session. Off the Mac one must be Ctrl: Alt+Shift
        // switches keyboard layout on Windows.
        if (count < 2 || !mac && !mods.HasFlag(KeyModifiers.Control))
        {
            return mac
                ? "use at least two modifier keys (such as Ctrl+Cmd), so it never gets in the way of typing in a session."
                : "use at least two modifier keys, one of them Ctrl (such as Ctrl+Alt), so it never gets in the way of typing in a session.";
        }

        // Ctrl+Alt is AltGr on Windows, and many layouts type a character with AltGr and a letter or symbol
        // (Ctrl+Alt+Q is @ on a German keyboard). Linux keyboards with AltGr do the same.
        if (!mac && mods == (KeyModifiers.Control | KeyModifiers.Alt) && TypesACharacter(key))
        {
            return "on many keyboard layouts Ctrl+Alt with a letter, digit or symbol types a character. Use a key like Enter, Home or an F key, or add Shift.";
        }

        if (Reserved(on).FirstOrDefault(r => Matches(r.Gesture, key, mods)) is { Gesture: not null } reserved)
        {
            return reserved.Why;
        }

        return null;
    }

    static IEnumerable<(KeyGesture Gesture, string Why)> Reserved(ShortcutPlatform on)
    {
        const KeyModifiers Ctrl = KeyModifiers.Control, Alt = KeyModifiers.Alt, Shift = KeyModifiers.Shift, Cmd = KeyModifiers.Meta;
        const string System = "the system keeps it for itself.";

        yield return (SessionEscape.For(on), "Orrery uses it to leave a session for its tab.");

        if (on == ShortcutPlatform.MacOS)
        {
            yield return (new KeyGesture(Key.Q, Ctrl | Cmd), "macOS uses it to lock the screen.");
            yield return (new KeyGesture(Key.Escape, Alt | Cmd), "macOS uses it to force apps to quit.");
            yield return (new KeyGesture(Key.Space, Ctrl | Cmd), "macOS uses it for the emoji picker.");
            yield return (new KeyGesture(Key.Tab, Shift | Cmd), System);
            foreach (var digit in new[] { Key.D3, Key.D4, Key.D5 })
            {
                yield return (new KeyGesture(digit, Shift | Cmd), "macOS uses it for screenshots.");
            }

            yield return (new KeyGesture(Key.B, Alt | Cmd), "Orrery uses it to show and hide the settings pane.");
            yield break;
        }

        yield return (new KeyGesture(Key.Delete, Ctrl | Alt), System);
        yield return (new KeyGesture(Key.Escape, Ctrl | Shift), "it opens Task Manager.");
        yield return (new KeyGesture(Key.Tab, Ctrl | Alt), System);
        yield return (new KeyGesture(Key.Tab, Alt | Shift), System);
        yield return (new KeyGesture(Key.F4, Ctrl | Alt), System);
        yield return (new KeyGesture(Key.B, Ctrl | Alt), "Orrery uses it to show and hide the settings pane.");
        yield return (new KeyGesture(Key.C, Ctrl | Shift), "terminals use it to copy.");
        yield return (new KeyGesture(Key.V, Ctrl | Shift), "terminals use it to paste.");

        if (on == ShortcutPlatform.Linux)
        {
            yield return (new KeyGesture(Key.Back, Ctrl | Alt), "some desktops use it to restart the display server.");
            foreach (var arrow in new[] { Key.Left, Key.Right, Key.Up, Key.Down })
            {
                yield return (new KeyGesture(arrow, Ctrl | Alt), "desktops use it to switch workspaces.");
            }

            for (var f = Key.F1; f <= Key.F12; f++)
            {
                yield return (new KeyGesture(f, Ctrl | Alt), "Linux uses it to switch to a text console.");
            }
        }
    }

    /// <summary>Ctrl+Break arrives as Cancel on Windows; both mean the Pause/Break key.</summary>
    static Key Normalise(Key key) => key == Key.Cancel ? Key.Pause : key;

    static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None;

    static bool TypesACharacter(Key key) => key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9
        or Key.OemSemicolon or Key.OemPlus or Key.OemComma or Key.OemMinus or Key.OemPeriod or Key.OemQuestion
        or Key.OemTilde or Key.OemOpenBrackets or Key.OemPipe or Key.OemCloseBrackets or Key.OemQuotes or Key.OemBackslash
        or Key.Space;

    /// <summary>How settings store it, e.g. "Ctrl+Alt+Enter". <see cref="Parse"/> reads it back.</summary>
    public static string Format(KeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Meta");
        parts.Add(Normalise(gesture.Key) == Key.Enter ? "Enter" : Normalise(gesture.Key).ToString());
        return string.Join('+', parts);
    }

    public static KeyGesture? Parse(string text)
    {
        try
        {
            return KeyGesture.Parse(text);
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>For people: "Ctrl+Alt+Enter", "Ctrl+Alt+Break", "Ctrl+Cmd+F".</summary>
    public static string Describe(KeyGesture gesture, ShortcutPlatform? platform = null)
    {
        bool mac = (platform ?? CurrentPlatform) == ShortcutPlatform.MacOS;
        var parts = new List<string>();
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add(mac ? "Option" : "Alt");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add(mac ? "Cmd" : "Win");
        parts.Add(Normalise(gesture.Key) switch
        {
            Key.Enter => "Enter",
            Key.Pause => "Break",
            Key.Back => "Backspace",
            Key.PageUp => "Page Up",
            Key.PageDown => "Page Down",
            Key.Escape => "Esc",
            Key.OemPlus => "=",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            >= Key.D0 and <= Key.D9 and var digit => ((char)('0' + (digit - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 and var pad => $"Num {(char)('0' + (pad - Key.NumPad0))}",
            var other => other.ToString(),
        });
        return string.Join('+', parts);
    }
}
