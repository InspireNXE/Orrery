using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>
/// Where a saved command goes: the defaults, any folders, any connections, ticked in a list laid out like the tree.
/// A guest is listed under its host but isn't ticked with it, since it's another machine that doesn't take its host's
/// commands. Ticking a folder (or the defaults) covers everything in it, so those rows show ticked and can't be
/// changed until it's unticked; they aren't saved again on their own. A host's row has + guests to tick it with all
/// its guests (Shift+click on its box does the same) and says how many of them are ticked; with a filter typed,
/// Tick shown ticks every connection it shows.
/// </summary>
sealed class CommandPlacePicker : DockPanel
{
    sealed record Row(ConnectionNode Node, CheckBox Box, string Label, Control Line, TextBlock? GuestCount = null, Button? GuestsLink = null);

    readonly ConnectionFolder _root;
    readonly List<Row> _rows = [];
    readonly HashSet<ConnectionNode> _chosen;
    readonly TextBox _filter = new() { PlaceholderText = "Filter", FontSize = 12, MinHeight = 28, Margin = new Thickness(0, 0, 0, 6) };
    readonly TextBlock _reach = new() { FontSize = 12, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    readonly Button _tickShown = new() { Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    readonly Button _clear = new() { Content = "Clear", Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center };
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
        _filter.Margin = new Thickness(0);
        ToolTip.SetTip(_tickShown, "Ticks every connection the filter shows");
        _tickShown.Click += (_, _) => SetMany(Shown().Where(n => n is ConnectionEntry), on: true);
        ToolTip.SetTip(_clear, "Unticks everything");
        _clear.Click += (_, _) => SetMany([.. _chosen], on: false);
        Children.Add(Pane.Docked(new DockPanel
        {
            Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                Pane.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(10, 0, 0, 0), Children = { _tickShown, _clear } }, Dock.Right),
                _filter,
            },
        }, Dock.Top));
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
    internal Button TickShownButton => _tickShown;
    internal Button ClearButton => _clear;
    internal TextBox FilterBox => _filter;
    internal Button? GuestsLinkFor(ConnectionNode node) => _rows.FirstOrDefault(r => r.Node == node)?.GuestsLink;
    internal string? GuestCountFor(ConnectionNode node) => _rows.FirstOrDefault(r => r.Node == node)?.GuestCount?.Text;

    /// <summary>Ticks the host and all its guests, or unticks them all when they're all ticked already: + guests, or Shift+click.</summary>
    internal void ToggleWithGuests(ConnectionEntry host)
    {
        var group = new ConnectionNode[] { host }.Concat(host.Guests).Where(n => CoveredBy(n) is null).ToList();
        SetMany(group, on: !group.All(_chosen.Contains));
    }

    /// <summary>Ticks or unticks several at once, saying so once.</summary>
    void SetMany(IEnumerable<ConnectionNode> nodes, bool on)
    {
        foreach (var node in nodes.ToList())
        {
            if (on)
            {
                _chosen.Add(node);
            }
            else
            {
                _chosen.Remove(node);
            }
        }

        Update();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The rows the filter shows that can be ticked, the defaults left out.</summary>
    IEnumerable<ConnectionNode> Shown() => _rows.Where(r => r.Line.IsVisible && r.Box.IsEnabled && r.Node != _root).Select(r => r.Node);

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

        var box = new CheckBox { Content = text, MinHeight = 26 };
        AutomationProperties.SetName(box, muted is null ? label : $"{label}, {muted}");
        var line = new DockPanel { Margin = new Thickness(depth * 16, 0, 0, 0) };
        TextBlock? count = null;
        Button? link = null;
        if (node is ConnectionEntry { IsHost: true } host)
        {
            count = new TextBlock { FontSize = 11, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            link = new Button { Classes = { "link" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            ToolTip.SetTip(link, $"Ticks {host.Name} and its guests together (Shift+click on its box does too)");
            AutomationProperties.SetName(link, $"{host.Name} with its guests");
            link.Click += (_, _) => ToggleWithGuests(host);

            // Shift+click on the host's box takes its guests along, as + guests does.
            box.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && e.GetCurrentPoint(box).Properties.IsLeftButtonPressed && box.IsEnabled)
                {
                    e.Handled = true;
                    ToggleWithGuests(host);
                }
            }, RoutingStrategies.Tunnel);
            line.Children.Add(Pane.Docked(link, Dock.Right));
            line.Children.Add(Pane.Docked(count, Dock.Right));
        }

        line.Children.Add(box);
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
        _rows.Add(new Row(node, box, muted is null ? label : $"{label} {muted}", line, count, link));
        list.Children.Add(line);
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
            if (row.Node is ConnectionEntry host && row.GuestCount is { } count && row.GuestsLink is { } link)
            {
                int ticked = host.Guests.Count(g => _chosen.Contains(g) || CoveredBy(g) is not null);
                count.Text = $"{ticked} of {host.Guests.Count} {(host.Guests.Count == 1 ? "guest" : "guests")}";
                bool all = row.Box.IsChecked == true && ticked == host.Guests.Count;
                link.Content = all ? "− guests" : "+ guests";
                link.IsEnabled = cover is null;
            }
        }

        _updating = false;
        _clear.IsVisible = _chosen.Count > 0;
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
            row.Line.IsVisible = filter.Length == 0 || row.Node == _root
                || row.Label.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || row.Node.Path.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        }

        // Only with a filter typed: with none, it would tick every connection one by one, which the defaults do better.
        int shown = filter.Length == 0 ? 0 : Shown().Count(n => n is ConnectionEntry);
        _tickShown.IsVisible = shown > 0;
        _tickShown.Content = shown == 1 ? "Tick the 1 shown" : $"Tick the {shown} shown";
    }
}
