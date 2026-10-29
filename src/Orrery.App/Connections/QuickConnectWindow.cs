using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>A saved connection to open, or a host typed in to connect to once.</summary>
sealed record QuickConnectChoice(ConnectionEntry? Entry, string? Host, int Port);

/// <summary>
/// The Ctrl+K palette: type to find a saved connection by name, host or folder, or enter
/// <c>host[:port]</c> to connect directly. Enter opens the highlighted row; arrows move.
/// </summary>
sealed class QuickConnectWindow : Window
{
    const int MaxResults = 8;
    const int RdpPort = 3389;

    readonly ConnectionTree _tree;
    readonly TextBox _query = new() { Watermark = "Search connections, or type host[:port]", FontSize = 15, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    readonly ListBox _results = new() { Background = Brushes.Transparent, MaxHeight = 380 };

    public QuickConnectWindow(ConnectionTree tree)
    {
        _tree = tree;

        Title = "Quick connect";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#181B21"));

        _results.ItemTemplate = new FuncDataTemplate<Row>((row, _) => new DockPanel
        {
            Margin = new Thickness(4, 2),
            Children =
            {
                Docked(new TextBlock { Text = row?.Hint, FontSize = 11, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center }, Dock.Right),
                new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = row?.Subtitle, FontSize = 12, Foreground = Dialogs.MutedBrush, IsVisible = !string.IsNullOrEmpty(row?.Subtitle) },
                    },
                },
            },
        });

        _query.TextChanged += (_, _) => Refresh();
        _query.KeyDown += OnQueryKeyDown;
        _results.DoubleTapped += (_, _) => Accept();

        Content = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(12),
            Children =
            {
                _query,
                new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#262A33")), Margin = new Thickness(0, 4) },
                _results,
                new TextBlock
                {
                    Text = "↑↓ to move · Enter to connect · Esc to close · host:port connects directly",
                    FontSize = 11,
                    Foreground = Dialogs.MutedBrush,
                    Margin = new Thickness(4, 6, 4, 0),
                },
            },
        };

        Opened += (_, _) => _query.Focus();
        Refresh();
    }

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    void Refresh()
    {
        var query = _query.Text?.Trim() ?? string.Empty;
        var rows = new List<Row>();

        foreach (var entry in Rank(query).Take(MaxResults))
        {
            rows.Add(new Row(entry.Name, $"{entry.Host}  ·  {(entry.Path is { Length: > 0 } p ? p : "Top level")}", "Saved", new QuickConnectChoice(entry, null, 0)));
        }

        bool exactHostSaved = rows.Any(r => string.Equals(r.Choice.Entry?.Host, query, StringComparison.OrdinalIgnoreCase));
        if (!exactHostSaved && HostAddress.TryParse(query, RdpPort, out var host, out var port))
        {
            var direct = new Row($"Connect to {host}" + (port != RdpPort ? $":{port}" : string.Empty), "RDP, without saving", "Direct", new QuickConnectChoice(null, host, port));
            if (rows.Count == 0) rows.Add(direct); else rows.Insert(Math.Min(rows.Count, 1), direct);
        }

        _results.ItemsSource = rows;
        _results.SelectedIndex = rows.Count > 0 ? 0 : -1;
    }

    /// <summary>Name prefix matches first, then other name, host or folder matches.</summary>
    IEnumerable<ConnectionEntry> Rank(string query)
    {
        var all = _tree.AllConnections();
        if (query.Length == 0)
        {
            return all.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        return all
            .Select(e => (Entry: e, Score: Score(e, query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Entry);
    }

    static int Score(ConnectionEntry entry, string query)
    {
        const StringComparison ignoreCase = StringComparison.CurrentCultureIgnoreCase;
        if (entry.Name.StartsWith(query, ignoreCase)) return 4;
        if (entry.Host.StartsWith(query, ignoreCase)) return 3;
        if (entry.Name.Contains(query, ignoreCase) || entry.Host.Contains(query, ignoreCase)) return 2;
        if (entry.Path.Contains(query, ignoreCase)) return 1;
        return 0;
    }

    void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        int count = _results.ItemCount;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                _results.SelectedIndex = (_results.SelectedIndex + 1) % count;
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                _results.SelectedIndex = (_results.SelectedIndex - 1 + count) % count;
                e.Handled = true;
                break;
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                Close(null);
                e.Handled = true;
                break;
        }
    }

    void Accept()
    {
        if (_results.SelectedItem is Row row)
        {
            Close(row.Choice);
        }
    }

    sealed record Row(string Title, string Subtitle, string Hint, QuickConnectChoice Choice);
}
