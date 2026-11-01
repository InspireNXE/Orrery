using Avalonia.Input;

namespace Orrery.App.Input;

/// <summary>
/// The key that takes the keyboard out of a session. A terminal keeps Tab and a remote desktop keeps every key, so
/// this is the way back to the rest of Orrery without a mouse: Ctrl+Alt+Home (Ctrl+Cmd+Home on macOS), the key Remote
/// Desktop Connection uses for its connection bar. It leaves full screen and moves the focus to the session's tab;
/// Enter on the tab goes back into the session.
/// </summary>
public static class SessionEscape
{
    public static KeyGesture For(ShortcutPlatform platform) => platform == ShortcutPlatform.MacOS
        ? new KeyGesture(Key.Home, KeyModifiers.Control | KeyModifiers.Meta)
        : new KeyGesture(Key.Home, KeyModifiers.Control | KeyModifiers.Alt);

    public static KeyGesture Gesture => For(FullScreenShortcut.CurrentPlatform);

    public static bool Matches(Key key, KeyModifiers modifiers) => FullScreenShortcut.Matches(Gesture, key, modifiers);

    /// <summary>For people: "Ctrl+Alt+Home", "Ctrl+Cmd+Home".</summary>
    public static string Description => FullScreenShortcut.Describe(Gesture);

    /// <summary>The help a screen reader gives for a session view.</summary>
    public static string Help => $"{Description} moves to the session's tab.";
}
