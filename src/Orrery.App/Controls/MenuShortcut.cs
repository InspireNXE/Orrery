using Avalonia.Controls;
using Avalonia.Input;
using Orrery.App.Input;

namespace Orrery.App.Controls;

/// <summary>
/// The shortcut shown beside a menu item, written as the command palette, tooltips and Settings write it. Avalonia's
/// own text on Windows and Linux says "Ctrl+Alt+Return" for Ctrl+Alt+Enter and "Ctrl++" for Ctrl+=; macOS keeps
/// Avalonia's symbols.
/// </summary>
static class MenuShortcut
{
    /// <param name="text">What to show instead of the gesture, on every platform: "?" for a key typed with Shift.</param>
    public static void Show(MenuItem item, KeyGesture gesture, string? text = null)
    {
        // The gesture stays, for the space it keeps in the menu and for the palette, which describes it itself.
        item.InputGesture = gesture;
        if (text is null && FullScreenShortcut.CurrentPlatform == ShortcutPlatform.MacOS)
        {
            return;
        }

        text ??= FullScreenShortcut.Describe(gesture);
        item.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<TextBlock>("PART_InputGestureText") is { } shown)
            {
                shown.Text = text;
            }
        };
    }
}
