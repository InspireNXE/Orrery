using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>
/// A saved connection to open, a host typed in to connect to once, or (with <see cref="NewName"/>) a new
/// connection to create.
/// </summary>
sealed record QuickConnectChoice(ConnectionEntry? Entry, string? Host, int Port, Protocol Protocol = Protocol.Rdp, string? Username = null, string? NewName = null)
{
    /// <summary>Open the saved connection's settings instead of connecting (Alt+Enter).</summary>
    public bool Edit { get; init; }

    /// <summary>Switch to this open session tab instead.</summary>
    public int? SessionIndex { get; init; }

    static readonly (string Scheme, Protocol Protocol)[] Schemes =
        [("rdp://", Protocol.Rdp), ("ssh://", Protocol.Ssh), ("vnc://", Protocol.Vnc), ("telnet://", Protocol.Telnet)];

    /// <summary>
    /// Reads "host", "host:port", "rdp://host", "ssh://host", "vnc://host", "telnet://host", "user@host" and "ssh://user@host:port".
    /// A username without a scheme means SSH, as on the command line.
    /// </summary>
    public static QuickConnectChoice? Parse(string text)
    {
        text = text.Trim();
        var protocol = Protocol.Rdp;
        bool explicitScheme = false;
        foreach (var (scheme, p) in Schemes)
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                (protocol, explicitScheme, text) = (p, true, text[scheme.Length..]);
            }
        }

        string? username = null;
        int at = text.LastIndexOf('@');
        if (at > 0)
        {
            username = text[..at];
            text = text[(at + 1)..];
            if (!explicitScheme) protocol = Protocol.Ssh;
        }

        return HostAddress.TryParse(text.TrimEnd('/'), ProtocolInfo.DefaultPort(protocol), out var host, out var port)
            ? new QuickConnectChoice(null, host, port, protocol, username)
            : null;
    }

    /// <summary>
    /// The ways to connect to what was typed: the protocol a scheme names, or without one, the likely
    /// protocol first and the other of RDP and SSH second.
    /// </summary>
    public static IReadOnlyList<QuickConnectChoice> Candidates(string text)
    {
        if (Parse(text) is not { } first)
        {
            return [];
        }

        if (Schemes.Any(s => text.TrimStart().StartsWith(s.Scheme, StringComparison.OrdinalIgnoreCase)))
        {
            return [first];
        }

        // A port that was typed applies to both; otherwise each protocol uses its own.
        var other = first.Protocol == Protocol.Ssh ? Protocol.Rdp : Protocol.Ssh;
        int port = first.Port == ProtocolInfo.DefaultPort(first.Protocol) ? ProtocolInfo.DefaultPort(other) : first.Port;
        return [first, first with { Protocol = other, Port = port }];
    }
}

/// <summary>A session tab, as quick connect lists it.</summary>
sealed record OpenSession(string Title, string Address, Protocol Protocol);

/// <summary>
/// The Ctrl+K palette: type to find an open session or a saved connection, enter <c>host[:port]</c> to connect
/// directly, or make a new connection. Enter opens the highlighted row and Alt+Enter a saved connection's
/// settings; arrows move; Esc or clicking elsewhere closes it.
/// </summary>
sealed class QuickConnectWindow : Window
{
    const int MaxResults = 8;
    const double TopOffset = 96;

    static readonly IBrush PanelBrush = Brush("#181B21");
    static readonly IBrush LineBrush = Brush("#262A33");
    static readonly IBrush OutlineBrush = Brush("#2F3540");
    static readonly IBrush HighlightBrush = Brush("#252B36");
    static IBrush AccentBrush => Palette.Accent;
    static readonly IBrush SoftBrush = Brush("#C3C9D3");

    readonly ConnectionTree _tree;
    readonly IReadOnlyList<OpenSession> _sessions;
    readonly TextBox _query = new()
    {
        Watermark = "Search connections, or type host, user@host, ssh://host:port",
        FontSize = 15,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
        Padding = new Thickness(0),
        MinHeight = 0,
    };
    readonly StackPanel _list = new() { Spacing = 2, Margin = new Thickness(8) };
    readonly List<Item> _items = [];
    int _selected = -1;
    bool _closing;
    QuickConnectChoice? _choice;

    public QuickConnectWindow(ConnectionTree tree, IReadOnlyList<OpenSession>? sessions = null)
    {
        _tree = tree;
        _sessions = sessions ?? [];

        Title = "Quick connect";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontSize = 13;

        // Hide the TextBox's own focus border and hover background.
        _query.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        _query.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        _query.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);

        _query.TextChanged += (_, _) => Refresh();
        _query.KeyDown += OnQueryKeyDown;

        var search = new DockPanel
        {
            Height = 52,
            Margin = new Thickness(16, 0),
            Children =
            {
                Docked(Chip("Esc"), Dock.Right),
                Docked(new StrokeIcon { Data = Geometry.Parse(Icons.Search), StrokeThickness = 2, Foreground = Dialogs.MutedBrush, Margin = new Thickness(0, 0, 10, 0) }, Dock.Left),
                _query,
            },
        };

        var footer = new WrapPanel
        {
            Margin = new Thickness(16, 10),
            Children =
            {
                Hint("↑↓", "move"),
                Hint("↵", "open"),
                Hint("Alt+↵", "settings"),
                Hint("Esc", "close"),
                Hint("ssh:// rdp:// vnc:// telnet://", "pick a protocol"),
            },
        };

        Content = new Border
        {
            Background = PanelBrush,
            BorderBrush = OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = new DockPanel
            {
                Children =
                {
                    Docked(new Border { BorderBrush = LineBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = search }, Dock.Top),
                    Docked(new Border { BorderBrush = LineBrush, BorderThickness = new Thickness(0, 1, 0, 0), Child = footer }, Dock.Bottom),
                    new ScrollViewer { MaxHeight = 440, Content = _list },
                },
            },
        };

        Opened += (_, _) =>
        {
            PlaceOverOwner();
            _query.Focus();
        };

        // Like a menu: clicking anywhere else dismisses it.
        Deactivated += (_, _) =>
        {
            if (!_closing)
            {
                Finish(null);
            }
        };

        Refresh();
    }

    /// <summary>Near the top of the owner, centred, where the eye already is.</summary>
    void PlaceOverOwner()
    {
        if (Owner is not Window owner)
        {
            return;
        }

        double scaling = owner.RenderScaling;
        Position = new PixelPoint(
            owner.Position.X + (int)((owner.Bounds.Width - Width) / 2 * scaling),
            owner.Position.Y + (int)(TopOffset * scaling));
    }

    /// <summary>
    /// Shows the box over <paramref name="owner"/> and completes when it closes. Not modal: a modal dialog
    /// disables its owner, so a click on the owner never takes focus from the box and never dismisses it.
    /// </summary>
    public Task<QuickConnectChoice?> ShowOver(Window owner)
    {
        var result = new TaskCompletionSource<QuickConnectChoice?>();
        Closed += (_, _) => result.TrySetResult(_choice);
        Show(owner);
        return result.Task;
    }

    void Finish(QuickConnectChoice? choice)
    {
        _closing = true;
        _choice = choice;
        Close(choice);
    }

    void Refresh()
    {
        var query = _query.Text?.Trim() ?? string.Empty;
        _list.Children.Clear();
        _items.Clear();

        // Already open: switching to it is likelier than opening it again.
        if (query.Length > 0)
        {
            var open = _sessions
                .Select((session, index) => (Session: session, Index: index))
                .Where(x => x.Session.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || x.Session.Address.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .Take(MaxResults)
                .ToList();
            if (open.Count > 0)
            {
                AddHeader("Open sessions");
                foreach (var (session, index) in open)
                {
                    AddItem(SessionRow(session, query), new QuickConnectChoice(null, null, 0) { SessionIndex = index }, "Switch to");
                }
            }
        }

        var saved = Rank(query).Take(MaxResults).ToList();
        if (saved.Count > 0)
        {
            AddHeader(query.Length == 0 ? "Saved connections" : "Matching connections");
            foreach (var entry in saved)
            {
                AddItem(SavedRow(entry, query), new QuickConnectChoice(entry, null, 0), "Connect");
            }
        }

        bool exactHostSaved = saved.Any(e => string.Equals(e.Host, query, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<QuickConnectChoice> direct = exactHostSaved ? [] : QuickConnectChoice.Candidates(query);
        if (direct.Count > 0)
        {
            AddHeader("Connect directly");
            foreach (var choice in direct)
            {
                AddItem(DirectRow(choice), choice, "Without saving");
            }
        }

        if (query.Length > 0)
        {
            AddHeader("Actions");
            var host = direct.Count > 0 ? direct[0].Host : null;
            AddItem(ActionRow($"New connection named \"{query}\"…"), new QuickConnectChoice(null, host, 0, NewName: query), "Create");
        }

        if (_items.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No saved connections yet. Type a host name to connect to it.",
                Foreground = Dialogs.MutedBrush,
                Margin = new Thickness(10, 8),
            });
        }

        Select(_items.Count > 0 ? 0 : -1);
    }

    /// <summary>Best matches first; see <see cref="ConnectionSearch.Score"/>.</summary>
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

    static int Score(ConnectionEntry entry, string query) => ConnectionSearch.Score(entry, query);

    // ---- Rows ----

    void AddHeader(string text) => _list.Children.Add(new TextBlock
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        LetterSpacing = 0.8,
        Foreground = Dialogs.MutedBrush,
        Margin = new Thickness(10, _list.Children.Count == 0 ? 6 : 10, 10, 4),
    });

    void AddItem(Control content, QuickConnectChoice choice, string hint)
    {
        var hintText = new TextBlock { Text = $"{hint}  ↵", FontSize = 12, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        var row = new Border
        {
            Height = 40,
            Padding = new Thickness(8, 0, 10, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel { Children = { Docked(hintText, Dock.Right), content } },
        };

        var item = new Item(row, hintText, choice);
        int index = _items.Count;
        row.PointerEntered += (_, _) => Select(index);
        row.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            Finish(choice);
        };

        _items.Add(item);
        _list.Children.Add(row);
    }

    static Control SavedRow(ConnectionEntry entry, string query)
    {
        var protocol = SettingsResolver.Resolve(entry).Protocol.Value;
        var name = new TextBlock { FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines = Highlight(entry.Name, query);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                Badge(protocol),
                name,
                new TextBlock
                {
                    Text = entry.Path is { Length: > 0 } path ? path : entry.Host,
                    FontSize = 12,
                    Foreground = Dialogs.MutedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 260,
                },
            },
        };
    }

    static Control SessionRow(OpenSession session, string query)
    {
        var name = new TextBlock { FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines = Highlight(session.Title, query);
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                Badge(session.Protocol),
                name,
                new TextBlock { Text = session.Address, FontSize = 12, FontFamily = Dialogs.MonoFont, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 },
            },
        };
    }

    static Control DirectRow(QuickConnectChoice choice)
    {
        var who = choice.Username is null ? string.Empty : $"{choice.Username}@";
        var port = choice.Port != ProtocolInfo.DefaultPort(choice.Protocol) ? $":{choice.Port}" : string.Empty;
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.Inlines =
        [
            new Run($"{ProtocolInfo.Label(choice.Protocol)} to "),
            new Run($"{who}{choice.Host}{port}") { FontFamily = Dialogs.MonoFont },
        ];

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new StrokeIcon { Data = Geometry.Parse(ProtocolStyle.Icon(choice.Protocol)), Foreground = ProtocolStyle.Foreground(choice.Protocol), VerticalAlignment = VerticalAlignment.Center },
                text,
            },
        };
    }

    static Control ActionRow(string label) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children =
        {
            new StrokeIcon { Data = Geometry.Parse(Icons.Plus), StrokeThickness = 2, Foreground = SoftBrush, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
        },
    };

    /// <summary>The name with the first match of the query in the accent colour.</summary>
    static InlineCollection Highlight(string name, string query)
    {
        int at = query.Length == 0 ? -1 : name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0)
        {
            return [new Run(name)];
        }

        return
        [
            new Run(name[..at]),
            new Run(name.Substring(at, query.Length)) { Foreground = AccentBrush },
            new Run(name[(at + query.Length)..]),
        ];
    }

    static Control Badge(Protocol protocol) => new Border
    {
        Background = ProtocolStyle.Background(protocol),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = ProtocolInfo.Label(protocol),
            FontSize = 10,
            FontWeight = FontWeight.Medium,
            FontFamily = Dialogs.MonoFont,
            Foreground = ProtocolStyle.Foreground(protocol),
        },
    };

    static Control Chip(string text) => new Border
    {
        BorderBrush = OutlineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontFamily = Dialogs.MonoFont, Foreground = Dialogs.MutedBrush },
    };

    static Control Hint(string keys, string action) => new TextBlock
    {
        FontSize = 11,
        Foreground = Dialogs.MutedBrush,
        Margin = new Thickness(0, 0, 16, 0),
        Inlines = [new Run(keys) { FontFamily = Dialogs.MonoFont }, new Run($" {action}")],
    };

    static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color)).ToImmutable();

    // ---- Keyboard ----

    void Select(int index)
    {
        if (_selected >= 0 && _selected < _items.Count)
        {
            _items[_selected].Show(false);
        }

        _selected = index;
        if (index >= 0)
        {
            _items[index].Show(true);
            _items[index].Row.BringIntoView();
        }
    }

    void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        int count = _items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                Select((_selected + 1) % count);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                Select((_selected - 1 + count) % count);
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Alt:
                if (_selected >= 0 && _items[_selected].Choice is { Entry: not null } saved)
                {
                    Finish(saved with { Edit = true });
                }

                e.Handled = true;
                break;
            case Key.Enter:
                if (_selected >= 0)
                {
                    Finish(_items[_selected].Choice);
                }

                e.Handled = true;
                break;
            case Key.Escape:
                Finish(null);
                e.Handled = true;
                break;
        }
    }

    sealed record Item(Border Row, TextBlock Hint, QuickConnectChoice Choice)
    {
        public void Show(bool selected)
        {
            Row.Background = selected ? HighlightBrush : Brushes.Transparent;
            Row.BorderBrush = selected ? AccentBrush : Brushes.Transparent;
            Hint.IsVisible = selected;
        }
    }
}
