using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>
/// Where a saved command goes: the defaults, any folders, any connections, ticked in a list laid out like the tree.
/// A guest is listed under its host but isn't ticked with it, since it's another machine that doesn't take its host's
/// commands. Ticking a folder (or the defaults) covers everything in it, so those rows show ticked and can't be
/// changed until it's unticked; they aren't saved again on their own.
/// </summary>
sealed class CommandPlacePicker : DockPanel
{
    sealed record Row(ConnectionNode Node, CheckBox Box, string Label);

    readonly ConnectionFolder _root;
    readonly List<Row> _rows = [];
    readonly HashSet<ConnectionNode> _chosen;
    readonly TextBox _filter = new() { PlaceholderText = "Filter", FontSize = 12, MinHeight = 28, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock _reach = new() { FontSize = 12, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    bool _updating;

    public CommandPlacePicker(ConnectionTree tree, IEnumerable<ConnectionNode> chosen)
    {
        _root = tree.Root;
        _chosen = [.. chosen];
        var list = new StackPanel { Spacing = 0, Margin = new Thickness(8, 4) };
        AddRow(list, _root, 0, "Every connection, and terminals without one (the defaults)", muted: null);
        AddChildren(list, _root, 1);

        AutomationProperties.SetName(_filter, "Filter places");
        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.IsVisible = _rows.Count > 8;
        Children.Add(Pane.Docked(_filter, Dock.Top));
        Children.Add(Pane.Docked(_reach, Dock.Bottom));
        Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#2A2F38")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer { MaxHeight = 220, Content = list },
        });
        Update();
    }

    /// <summary>The ticks changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The places ticked, in tree order, leaving out those a ticked folder (or the defaults) already covers.</summary>
    public IReadOnlyList<ConnectionNode> Chosen => [.. _rows.Select(r => r.Node).Where(n => _chosen.Contains(n) && CoveredBy(n) is null)];

    /// <summary>Ticks or unticks <paramref name="node"/>, as a click on its box would.</summary>
    internal void Choose(ConnectionNode node, bool on)
    {
        if (_rows.FirstOrDefault(r => r.Node == node) is { Box.IsEnabled: true } row)
        {
            row.Box.IsChecked = on;
        }
    }

    internal CheckBox? BoxFor(ConnectionNode node) => _rows.FirstOrDefault(r => r.Node == node)?.Box;

    internal TextBlock ReachText => _reach;

    void AddChildren(StackPanel list, ConnectionFolder folder, int depth)
    {
        foreach (var child in folder.Children)
        {
            AddRow(list, child, depth, child.Name, muted: null);
            if (child is ConnectionFolder inner)
            {
                AddChildren(list, inner, depth + 1);
            }
            else if (child is ConnectionEntry entry)
            {
                foreach (var guest in entry.Guests)
                {
                    AddRow(list, guest, depth + 1, guest.Name, muted: $"guest of {entry.Name}");
                }
            }
        }
    }

    void AddRow(StackPanel list, ConnectionNode node, int depth, string label, string? muted)
    {
        var icon = StrokeIcon.Create(node is ConnectionFolder ? Icons.Folder : Icons.Terminal, 12);
        icon.Margin = new Thickness(0, 0, 6, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var text = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } } };
        if (muted is not null)
        {
            text.Children.Add(new TextBlock { Text = $"  {muted}", FontSize = 11, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center });
        }

        var box = new CheckBox { Content = text, Margin = new Thickness(depth * 16, 0, 0, 0), MinHeight = 26 };
        AutomationProperties.SetName(box, muted is null ? label : $"{label}, {muted}");
        box.IsCheckedChanged += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            if (box.IsChecked == true)
            {
                _chosen.Add(node);
            }
            else
            {
                _chosen.Remove(node);
            }

            Update();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _rows.Add(new Row(node, box, muted is null ? label : $"{label} {muted}"));
        list.Children.Add(box);
    }

    /// <summary>The ticked folder (or the defaults) around <paramref name="node"/> that already gives it the command, if any.</summary>
    ConnectionFolder? CoveredBy(ConnectionNode node) => node.Ancestors().LastOrDefault(_chosen.Contains);

    void Update()
    {
        _updating = true;
        foreach (var row in _rows)
        {
            var cover = CoveredBy(row.Node);
            row.Box.IsChecked = cover is not null || _chosen.Contains(row.Node);
            row.Box.IsEnabled = cover is null;
            ToolTip.SetTip(row.Box, cover is null ? null
                : $"It has the command from {(cover.Parent is null ? "the defaults" : cover.Name)}, which is ticked");
        }

        _updating = false;
        _reach.Text = Reach();
    }

    /// <summary>What the ticks add up to, under the list.</summary>
    string Reach()
    {
        var chosen = Chosen;
        if (chosen.Count == 0)
        {
            return "Tick where to save it.";
        }

        if (chosen.Contains(_root))
        {
            return "It reaches every terminal: every saved connection, local shells and quick connects.";
        }

        int connections = chosen.SelectMany(n => n is ConnectionFolder folder ? folder.Descendants().OfType<ConnectionEntry>() : [(ConnectionEntry)n])
            .Distinct().Count();
        return connections == 1 ? "It reaches 1 saved connection." : $"It reaches {connections} saved connections.";
    }

    void ApplyFilter()
    {
        var filter = _filter.Text?.Trim() ?? string.Empty;
        foreach (var row in _rows)
        {
            row.Box.IsVisible = filter.Length == 0 || row.Node == _root
                || row.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || row.Node.Path.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
