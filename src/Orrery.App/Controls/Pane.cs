using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Orrery.App.Controls;

/// <summary>Building blocks shared by the side panels and the right-hand pane, so they line up alike.</summary>
static class Pane
{
    static readonly IBrush FooterBrush = new SolidColorBrush(Color.Parse("#101216")).ToImmutable();
    public static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#23272F")).ToImmutable();

    /// <summary>A section's card, a step up from the pane: the same raised panel and hairline as Settings' cards.</summary>
    public static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#181B21")).ToImmutable();

    /// <summary>The faint line between rows in a card.</summary>
    static readonly IBrush RowLineBrush = new SolidColorBrush(Color.Parse("#2A2F38")).ToImmutable();

    /// <summary>
    /// A titled group of fields in a card of its own, as Settings' sections are, so where one ends and the next begins
    /// is plain. Hiding the card hides the section.
    /// </summary>
    public static Control Section(string title, params Control[] children) => Card(SectionBody(title, 10, children));

    /// <summary>A titled group of one-line setting rows, which sit close together like a list.</summary>
    public static Control RowSection(string title, params Control[] rows) => Card(SectionBody(title, 4, [Rows(rows)]));

    static StackPanel SectionBody(string title, double spacing, Control[] children)
    {
        var section = new StackPanel { Spacing = spacing };
        var heading = new TextBlock { Text = title.ToUpperInvariant(), Classes = { "section" } };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(heading, 2);
        section.Children.Add(heading);
        section.Children.AddRange(children);
        return section;
    }

    static Border Card(Control content) => new()
    {
        Background = CardBrush,
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(CardPadding, 10, CardPadding, 8),
        Child = content,
    };

    /// <summary>How far a card's content sits in from its sides; a row's editor reaches out by this much to fill the card's width.</summary>
    public const double CardPadding = 12;

    /// <summary>
    /// Setting rows one under another, with no gap between them and a faint line between each shown row and the next.
    /// A row that's hidden takes its line with it; a note (a text block) belongs to the row above, so has none.
    /// </summary>
    public static Control Rows(params Control[] rows)
    {
        var list = new StackPanel();
        var lined = new List<Border>();
        void Rule()
        {
            bool first = true;
            foreach (var line in lined)
            {
                line.BorderThickness = new Thickness(0, line.IsVisible && !first && line.Child is not TextBlock ? 1 : 0, 0, 0);
                first &= !line.IsVisible;
            }
        }

        foreach (var row in rows)
        {
            var line = new Border { BorderBrush = RowLineBrush, Child = row, IsVisible = row.IsVisible };
            // The row says whether it's shown; its line follows.
            row.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty)
                {
                    line.IsVisible = row.IsVisible;
                    Rule();
                }
            };
            lined.Add(line);
            list.Children.Add(line);
        }

        Rule();
        return list;
    }

    /// <summary>The pane's actions, on a darker strip set apart from the scrolling content above.</summary>
    public static Control Footer(Control content) => new Border
    {
        Background = FooterBrush,
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Padding = new Thickness(16, 14, 16, 16),
        Child = content,
    };

    /// <summary>The pane's header, pinned over the scrolling content and set off from it by the footer's line.</summary>
    public static Control HeaderStrip(Control content) => new Border
    {
        BorderBrush = LineBrush,
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(16, 16, 16, 12),
        Child = content,
    };

    /// <summary>A scrolling body with a footer pinned under it.</summary>
    public static Control WithFooter(Control body, Control footer) => new DockPanel
    {
        Children =
        {
            Docked(Footer(footer), Dock.Bottom),
            new ScrollViewer { Content = body },
        },
    };

    /// <summary>A colored square with an icon, beside a title and a subtitle.</summary>
    /// <param name="more">Further lines under the subtitle.</param>
    public static Control Header(Border tile, TextBlock title, TextBlock subtitle, params Control[] more)
    {
        var lines = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { title, subtitle } };
        lines.Children.AddRange(more);
        // The right-hand pane's pin button sits in its top corner, beside this.
        return new DockPanel { Margin = new Thickness(0, 0, 28, 0), Children = { Docked(tile, Dock.Left), lines } };
    }

    /// <summary>Puts <paramref name="button"/> in a side panel's header row, left of the buttons it has already.</summary>
    public static void AddHeaderButton(DockPanel header, Control button) => header.Children.Insert(header.Children.Count - 1, Docked(button, Dock.Right));

    /// <summary>A 36 px tile holding an 18 px icon.</summary>
    public static Border Tile(StrokeIcon icon)
    {
        icon.Width = icon.Height = 18;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Child = icon };
    }

    /// <summary>Two controls side by side in equal columns with an 8 px gap.</summary>
    public static Control Pair(Control left, Control right)
    {
        Grid.SetColumn(right, 2);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Children = { left, right } };
    }

    public static T Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    /// <summary>
    /// A muted, wrapping note for an empty pane or list. In a side panel it lines up with the panel's
    /// heading (14 px in); in the right-hand pane, with its fields (16 px in).
    /// </summary>
    public static TextBlock EmptyText(string text, bool side = false) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.Parse("#9AA3B2")),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 19,
        Margin = side ? new Thickness(14, 6, 14, 8) : new Thickness(16),
    };
}
