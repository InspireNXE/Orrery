using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Orrery.App.Input;

/// <summary>
/// Arrow keys in a group of radio buttons, as Windows' own have them, in every window: Down or Right chooses the next
/// choice, Up or Left the one before, wrapping around at the ends. A group is the buttons sharing a GroupName in the
/// window, or without one, those in the same panel, in the order they're drawn.
/// </summary>
static class RadioKeys
{
    static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        InputElement.KeyDownEvent.AddClassHandler<RadioButton>(OnKeyDown);
    }

    static void OnKeyDown(RadioButton radio, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right))
        {
            return;
        }

        var group = Group(radio);
        int at = group.IndexOf(radio);
        if (at < 0 || group.Count < 2)
        {
            return;
        }

        int step = e.Key is Key.Down or Key.Right ? 1 : -1;
        var next = group[(at + step + group.Count) % group.Count];
        e.Handled = true;
        next.IsChecked = true;
        next.Focus(NavigationMethod.Directional);
    }

    /// <summary>The buttons <paramref name="radio"/> is one of that can be chosen now, top to bottom, then left to right.</summary>
    internal static List<RadioButton> Group(RadioButton radio)
    {
        var root = TopLevel.GetTopLevel(radio);
        IEnumerable<RadioButton> members = radio.GroupName is { Length: > 0 } name
            ? root?.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == name) ?? [radio]
            : (radio.Parent as Panel)?.Children.OfType<RadioButton>() ?? [radio];
        return members.Where(r => r.IsEffectivelyVisible && r.IsEffectivelyEnabled)
            .Select(r => (Radio: r, At: root is null ? default : r.TranslatePoint(default, root) ?? default))
            .OrderBy(p => p.At.Y).ThenBy(p => p.At.X)
            .Select(p => p.Radio).ToList();
    }
}
