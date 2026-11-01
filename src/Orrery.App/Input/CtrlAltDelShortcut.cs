using Avalonia.Input;

namespace Orrery.App.Input;

/// <summary>
/// The key that sends Ctrl+Alt+Del to a remote desktop. Ctrl+Alt+Del itself never reaches an app, since Windows keeps it
/// for its own secure screen, so this is Ctrl+Alt+End, as in Remote Desktop Connection; Ctrl+Cmd+End on macOS, where
/// Ctrl+Option belongs to VoiceOver (the way <see cref="SessionEscape"/> does).
/// </summary>
public static class CtrlAltDelShortcut
{
    public static KeyGesture For(ShortcutPlatform platform) => platform == ShortcutPlatform.MacOS
        ? new KeyGesture(Key.End, KeyModifiers.Control | KeyModifiers.Meta)
        : new KeyGesture(Key.End, KeyModifiers.Control | KeyModifiers.Alt);

    public static KeyGesture Gesture => For(FullScreenShortcut.CurrentPlatform);

    public static bool Matches(Key key, KeyModifiers modifiers) => FullScreenShortcut.Matches(Gesture, key, modifiers);

    /// <summary>For people: "Ctrl+Alt+End", "Ctrl+Cmd+End".</summary>
    public static string Description => FullScreenShortcut.Describe(Gesture);

    /// <summary>For a menu item's shortcut text, in KeyGesture form.</summary>
    public static string MenuText => FullScreenShortcut.Format(Gesture);
}
