using Avalonia.Controls;

namespace Orrery.App.Controls;

/// <summary>Shows a menu built at the moment it opens.</summary>
static class PopupMenu
{
    /// <summary>
    /// Opens a new menu holding <paramref name="items"/> under <paramref name="anchor"/>. A MenuFlyout whose items
    /// are swapped in its Opening event shows them empty (a 2-pixel sliver) in Avalonia 11.3 and 12.1, because its presenter
    /// was built before they were added, so each open gets its own flyout instead.
    /// </summary>
    public static MenuFlyout ShowAt(Control anchor, IEnumerable<Control> items, PlacementMode placement)
    {
        var flyout = new MenuFlyout { Placement = placement };
        foreach (var item in items)
        {
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
        return flyout;
    }
}
