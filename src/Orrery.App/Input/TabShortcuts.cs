using Avalonia.Input;

namespace Orrery.App.Input;

/// <summary>Where the keyboard focus is, which decides which tab shortcuts Orrery takes and which go to the session.</summary>
enum FocusPlace
{
    /// <summary>The tree, the tab strip, a settings field: anything outside a session.</summary>
    App,

    /// <summary>An SSH or Telnet terminal.</summary>
    Terminal,

    /// <summary>An RDP or VNC desktop, which gets nearly every key.</summary>
    Desktop,
}

/// <summary>A tab shortcut: next, previous, close, or go to tab <see cref="Index"/> (0-based; -1 for the last).</summary>
readonly record struct TabCommand(TabCommandKind Kind, int Index = 0);

enum TabCommandKind
{
    Next,
    Previous,
    Close,
    Select,
}

/// <summary>
/// The keys that move between and close session tabs, browser style. What's taken depends on focus:
/// Ctrl+Alt+PageUp/PageDown work everywhere, even in a remote desktop; Ctrl+Tab, Ctrl+PageUp/PageDown,
/// Ctrl+1–9 and Ctrl+F4 work everywhere but a remote desktop, which gets them; Ctrl+W only outside a session,
/// since shells use it to delete a word. On macOS, Cmd takes Ctrl's place except in Ctrl+Tab.
/// </summary>
static class TabShortcuts
{
    public static TabCommand? Match(Key key, KeyModifiers modifiers, FocusPlace place, bool mac)
    {
        var command = mac ? KeyModifiers.Meta : KeyModifiers.Control;

        if (modifiers == (command | KeyModifiers.Alt) && key is Key.PageDown or Key.PageUp)
        {
            return new TabCommand(key == Key.PageDown ? TabCommandKind.Next : TabCommandKind.Previous);
        }

        if (place == FocusPlace.Desktop)
        {
            return null;
        }

        switch (key)
        {
            case Key.Tab when modifiers == KeyModifiers.Control:
                return new TabCommand(TabCommandKind.Next);
            case Key.Tab when modifiers == (KeyModifiers.Control | KeyModifiers.Shift):
                return new TabCommand(TabCommandKind.Previous);
            case Key.PageDown when modifiers == command:
                return new TabCommand(TabCommandKind.Next);
            case Key.PageUp when modifiers == command:
                return new TabCommand(TabCommandKind.Previous);
            case >= Key.D1 and <= Key.D9 when modifiers == command:
                // Ctrl+9 is the last tab, however many there are, as in browsers.
                return new TabCommand(TabCommandKind.Select, key == Key.D9 ? -1 : key - Key.D1);
            case Key.F4 when modifiers == command:
                return new TabCommand(TabCommandKind.Close);
            case Key.W when modifiers == command && place == FocusPlace.App:
                return new TabCommand(TabCommandKind.Close);
            default:
                return null;
        }
    }

    /// <summary>The index <paramref name="command"/> leads to from <paramref name="current"/>, among <paramref name="count"/> tabs; null for none.</summary>
    public static int? Target(TabCommand command, int current, int count)
    {
        if (count == 0)
        {
            return null;
        }

        return command.Kind switch
        {
            TabCommandKind.Next => (current + 1) % count,
            TabCommandKind.Previous => (current - 1 + count) % count,
            TabCommandKind.Select when command.Index == -1 => count - 1,
            TabCommandKind.Select => command.Index < count ? command.Index : null,
            _ => current,
        };
    }

    /// <summary>
    /// Where a tab whose leading edge is dragged to <paramref name="x"/> lands: the number of other tabs whose middle is left of it.
    /// <paramref name="middles"/> are every tab's middle, in order, including the dragged one at <paramref name="dragged"/>.
    /// </summary>
    public static int DropIndex(IReadOnlyList<double> middles, int dragged, double x) =>
        middles.Where((middle, i) => i != dragged && middle < x).Count();

    /// <summary>
    /// Which way the tab at <paramref name="index"/> steps aside while the tab from <paramref name="from"/> is held
    /// over <paramref name="to"/>: -1 left, +1 right, 0 not at all. The tabs it has passed move into its old place.
    /// </summary>
    public static int Shift(int index, int from, int to) =>
        from < to && index > from && index <= to ? -1
        : to < from && index >= to && index < from ? 1
        : 0;
}
