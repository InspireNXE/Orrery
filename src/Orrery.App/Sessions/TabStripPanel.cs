using Avalonia;
using Avalonia.Controls;

namespace Orrery.App.Sessions;

/// <summary>
/// The tab strip, laid out as a browser's: tabs keep their own width while they all fit in <see cref="Room"/>; when
/// they don't, the widest are narrowed to an equal share, and below <see cref="CompactWidth"/> a tab shows only its
/// state (<see cref="SessionTabHeader.Compact"/>). Past <see cref="MinTabWidth"/> each, the strip is wider than the
/// room and its scroll viewer scrolls.
/// </summary>
sealed class TabStripPanel : StackPanel
{
    /// <summary>The narrowest a tab gets: its state and, on the current tab, the close button.</summary>
    public const double MinTabWidth = 52;

    /// <summary>Narrower than this, a tab's name would be a letter or two, so it shows its state alone.</summary>
    public const double CompactWidth = 96;

    /// <summary>Below this share, a squeezed tab drops its protocol tag so its name keeps the room.</summary>
    public const double TightWidth = 150;

    double _room = double.PositiveInfinity;

    public TabStripPanel() => Orientation = Avalonia.Layout.Orientation.Horizontal;

    /// <summary>The width the tabs have before the strip scrolls: the tab bar's, less its buttons.</summary>
    public double Room
    {
        get => _room;
        set
        {
            if (Math.Abs(_room - value) > 0.5)
            {
                _room = value;
                InvalidateMeasure();
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Children.Where(c => c.IsVisible).ToList();
        var tabs = shown.OfType<SessionTabHeader>().ToList();

        // A compact or tight tab keeps the full width it had, so the strip can tell when it all fits again.
        foreach (var child in shown)
        {
            if (child is not SessionTabHeader { Compact: true } and not SessionTabHeader { Tight: true })
            {
                child.Measure(Size.Infinity);
                if (child is SessionTabHeader tab)
                {
                    tab.FullWidth = tab.DesiredSize.Width;
                }
            }
        }

        // Whatever else is in the strip (the slot a dragged-in tab will land in) keeps its width.
        double others = shown.Where(c => c is not SessionTabHeader).Sum(c => c.DesiredSize.Width);
        double room = Room - others - Spacing * Math.Max(0, shown.Count - 1);
        double share = Share(tabs.Select(t => t.FullWidth).ToList(), room);
        foreach (var tab in tabs)
        {
            bool squeezed = tab.FullWidth > share;
            tab.Compact = squeezed && share < CompactWidth;
            tab.Tight = squeezed && !tab.Compact && share < TightWidth;
            tab.Measure(squeezed ? new Size(Math.Max(MinTabWidth, share), availableSize.Height) : Size.Infinity);
        }

        double width = shown.Sum(c => c.DesiredSize.Width) + Spacing * Math.Max(0, shown.Count - 1);
        double height = shown.Count == 0 ? 0 : shown.Max(c => c.DesiredSize.Height);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children.Where(c => c.IsVisible))
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }

        return finalSize;
    }

    /// <summary>
    /// The widest any tab may be so that <paramref name="widths"/> fit in <paramref name="room"/>: tabs narrower than it
    /// keep their width and the rest share what's left, as water fills a row of glasses. Infinity when they all fit.
    /// </summary>
    internal static double Share(IReadOnlyList<double> widths, double room)
    {
        if (widths.Count == 0 || widths.Sum() <= room)
        {
            return double.PositiveInfinity;
        }

        var sorted = widths.Order().ToList();
        double left = Math.Max(0, room);
        for (int i = 0; i < sorted.Count; i++)
        {
            double even = left / (sorted.Count - i);
            if (sorted[i] > even)
            {
                return even;
            }

            left -= sorted[i];
        }

        return double.PositiveInfinity;
    }
}
