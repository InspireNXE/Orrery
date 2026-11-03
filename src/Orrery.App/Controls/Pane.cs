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

    /// <summary>A titled group of fields, headed like the mockup's inspector.</summary>
    public static Control Section(string title, params Control[] children)
    {
        var section = new StackPanel { Spacing = 10 };
        var heading = new TextBlock { Text = title.ToUpperInvariant(), Classes = { "section" } };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(heading, 2);
        section.Children.Add(heading);
        section.Children.AddRange(children);
        return section;
    }

    /// <summary>A titled group of one-line setting rows, which sit close together like a list.</summary>
    public static Control RowSection(string title, params Control[] rows)
    {
        var section = (StackPanel)Section(title);
        section.Spacing = 4;
        section.Children.Add(Rows(rows));
        return section;
    }

    /// <summary>Setting rows one under another, with no gap between them.</summary>
    public static Control Rows(params Control[] rows)
    {
        var list = new StackPanel();
        list.Children.AddRange(rows);
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
