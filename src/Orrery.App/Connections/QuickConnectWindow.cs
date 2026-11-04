using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
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

    /// <summary>Run this command instead, once the palette has closed.</summary>
    public PaletteCommand? Command { get; init; }

    /// <summary>Run the command's Shift+Enter action (<see cref="PaletteCommand.AltRun"/>) instead.</summary>
    public bool Alt { get; init; }

    static readonly (string Scheme, Protocol Protocol)[] Schemes =
    [
        ("rdp://", Protocol.Rdp), ("ssh://", Protocol.Ssh), ("vnc://", Protocol.Vnc), ("telnet://", Protocol.Telnet),
        ("serial://", Protocol.Serial), ("http://", Protocol.Http), ("https://", Protocol.Https),
    ];

    /// <summary>
    /// Reads "host", "host:port", "rdp://host", "ssh://host", "vnc://host", "telnet://host", "user@host" and "ssh://user@host:port";
    /// web addresses ("https://host/path"), opened in the browser; and serial ports ("serial://COM3", or just "COM3" or
    /// "/dev/ttyUSB0"). A username without a scheme means SSH, as on the command line.
    /// </summary>
    public static QuickConnectChoice? Parse(string text)
    {
        text = text.Trim();
        if (WebAddress.TryParseUrl(text, out var web, out var webHost, out var webPort))
        {
            return new QuickConnectChoice(null, webHost, webPort, web);
        }

        // A web address that isn't one to open, such as one with a user name in it, isn't read as anything else either.
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (SerialPortName(text) is { } serialPort)
        {
            return new QuickConnectChoice(null, serialPort, 0, Protocol.Serial);
        }

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
    /// A serial port's name: after serial://, or on its own when it looks like one (COM3, /dev/ttyUSB0), since
    /// neither is a host name. Null otherwise.
    /// </summary>
    static string? SerialPortName(string text)
    {
        if (text.StartsWith("serial://", StringComparison.OrdinalIgnoreCase))
        {
            var name = text["serial://".Length..].Trim();
            return name.Length > 0 && !name.Any(char.IsWhiteSpace) ? name : null;
        }

        bool com = text.Length is > 3 and <= 6 && text.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && text[3..].All(char.IsAsciiDigit);
        bool device = text.StartsWith("/dev/", StringComparison.Ordinal) && text.Length > 5 && !text.Any(char.IsWhiteSpace);
        return com ? text.ToUpperInvariant() : device ? text : null;
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

        if (first.Protocol is Protocol.Serial or Protocol.Http or Protocol.Https
            || Schemes.Any(s => text.TrimStart().StartsWith(s.Scheme, StringComparison.OrdinalIgnoreCase)))
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
/// <param name="Label">What its badge says when that isn't the protocol's name, such as LOCAL or SFTP.</param>
sealed record OpenSession(string Title, string Address, Protocol Protocol, string? Label = null);

/// <summary>
/// The Ctrl+K palette: type to find an open session or a saved connection, enter <c>host[:port]</c> to connect
/// directly, make a new connection, or run a command (<see cref="PaletteCommand"/>); a query starting with &gt; lists
/// only commands. Enter opens the highlighted row and Alt+Enter a saved connection's settings; arrows move; Esc or
/// clicking elsewhere closes it.
/// </summary>
sealed class QuickConnectWindow : Window
{
    const int MaxResults = 8;
    const int MaxCommands = 5;
    const char CommandsOnly = '>';
    const double TopOffset = 96;

    static readonly IBrush PanelBrush = Brush("#181B21");
    static readonly IBrush LineBrush = Brush("#262A33");
    static readonly IBrush OutlineBrush = Brush("#2F3540");
    static IBrush AccentBrush => Palette.Accent;
    static readonly IBrush SoftBrush = Brush("#C3C9D3");

    readonly ConnectionTree _tree;
    readonly IReadOnlyList<OpenSession> _sessions;
    readonly IReadOnlyList<PaletteCommand> _commands;
    readonly TextBox _query = new()
    {
        PlaceholderText = "Search connections or commands, or type host, user@host, ssh://host:port, https://site",
        FontSize = 15,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
        Padding = new Thickness(0),
        MinHeight = 0,
    };
    readonly StackPanel _list = new() { Spacing = 2, Margin = new Thickness(8) };
    readonly List<Item> _items = [];

    // The highlighted result moves while the focus stays in the search box, so it's read out from here.
    readonly LiveText _spoken = new("Orrery.QuickConnect", ScreenReader.MostRecent);
    int _selected = -1;
    bool _closing;
    QuickConnectChoice? _choice;

    public QuickConnectWindow(ConnectionTree tree, IReadOnlyList<OpenSession>? sessions = null, IReadOnlyList<PaletteCommand>? commands = null)
    {
        _tree = tree;
        _sessions = sessions ?? [];
        _commands = commands ?? [];

        Title = "Quick connect";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontSize = 13;

        // Hide the TextBox's own focus border and hover background.
        _query.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
        _query.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
        _query.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);

        Avalonia.Automation.AutomationProperties.SetName(_query, "Quick connect");
        Avalonia.Automation.AutomationProperties.SetHelpText(_query,
            "Search saved connections and commands, or type a host. Start with > for commands only. Up and down arrows move through the results, Enter opens one, Alt+Enter opens its settings, Esc closes.");
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
                Hint(">", "commands"),
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
                    _spoken,
                    new ScrollViewer { MaxHeight = 440, Content = _list },
                },
            },
        };

        Opened += (_, _) =>
        {
            PlaceOverOwner();
            _query.Focus();
        };

        // Like a menu: clicking anywhere else dismisses it. Closed once the click has finished activating the window
        // it landed on: closing in the middle of that handover leaves Windows to activate another app instead, which
        // comes up over Orrery and takes the click.
        Deactivated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!_closing)
            {
                Finish(null);
            }
        });

        Refresh();
    }

    /// <summary>Near the top of the owner, centered, where the eye already is.</summary>
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

        if (query.StartsWith(CommandsOnly))
        {
            ShowCommandsOnly(query[1..].Trim());
            return;
        }

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
                    AddItem(SessionRow(session, query), new QuickConnectChoice(null, null, 0) { SessionIndex = index }, "Switch to",
                        $"{session.Title}, open {ProtocolInfo.Label(session.Protocol)} session, {session.Address}");
                }
            }
        }

        var saved = Rank(query).Take(MaxResults).ToList();
        if (saved.Count > 0)
        {
            AddHeader(query.Length == 0 ? "Saved connections" : "Matching connections");
            foreach (var entry in saved)
            {
                AddItem(SavedRow(entry, query), new QuickConnectChoice(entry, null, 0), "Connect",
                    $"{entry.Name}, {ProtocolInfo.Label(SettingsResolver.Resolve(entry).Protocol.Value)}, {(entry.Path is { Length: > 0 } path ? path : entry.Host)}");
            }
        }

        bool exactHostSaved = saved.Any(e => string.Equals(e.Host, query, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<QuickConnectChoice> direct = exactHostSaved ? [] : QuickConnectChoice.Candidates(query);
        var commands = RankCommands(query).Take(MaxCommands).ToList();

        // A bare word like "settings" is likelier a command than a host; one with a dot, colon, @ or slash is an address.
        bool looksLikeAddress = query.IndexOfAny(['.', ':', '@', '/']) >= 0;
        if (!looksLikeAddress)
        {
            AddCommands(commands);
        }

        if (direct.Count > 0)
        {
            AddHeader("Connect directly");
            foreach (var choice in direct)
            {
                AddItem(DirectRow(choice), choice, "Without saving", DirectText(choice));
            }
        }

        if (looksLikeAddress)
        {
            AddCommands(commands);
        }

        if (query.Length > 0)
        {
            AddHeader("Actions");
            var typed = direct.Count > 0 ? direct[0] : null;
            AddItem(ActionRow($"New connection named \"{query}\"…"), new QuickConnectChoice(null, typed?.Host, typed?.Port ?? 0, typed?.Protocol ?? Protocol.Rdp, NewName: query),
                "Create", $"New connection named {query}");
        }

        if (_items.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No saved connections yet. Type a host name to connect to it.",
                Foreground = Dialogs.MutedBrush,
                Margin = new Thickness(10, 8),
            });
            _spoken.Say("No saved connections yet. Type a host name to connect to it.");
        }

        Select(_items.Count > 0 ? 0 : -1);
    }

    /// <summary>After &gt;: every command under its heading, or those the rest of the query finds, best first.</summary>
    void ShowCommandsOnly(string query)
    {
        var found = query.Length == 0 ? _commands : RankCommands(query).ToList();
        foreach (var group in found.GroupBy(c => c.Group))
        {
            AddHeader(group.Key);
            foreach (var command in group)
            {
                AddCommand(command);
            }
        }

        if (_items.Count == 0)
        {
            const string none = "No command matches.";
            _list.Children.Add(new TextBlock { Text = none, Foreground = Dialogs.MutedBrush, Margin = new Thickness(10, 8) });
            _spoken.Say(none);
        }

        Select(_items.Count > 0 ? 0 : -1);
    }

    IEnumerable<PaletteCommand> RankCommands(string query) => query.Length == 0 ? [] : _commands
        .Select((command, order) => (Command: command, Score: command.Score(query), Order: order))
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Order)
        .Select(x => x.Command);

    void AddCommands(IReadOnlyList<PaletteCommand> commands)
    {
        if (commands.Count == 0)
        {
            return;
        }

        AddHeader("Commands");
        foreach (var command in commands)
        {
            AddCommand(command);
        }
    }

    void AddCommand(PaletteCommand command) =>
        AddItem(CommandRow(command), new QuickConnectChoice(null, null, 0) { Command = command },
            command.AltHint is { } alt ? $"Run  ↵   ·   {alt}  Shift+↵" : "Run  ↵",
            string.Join(", ", new[] { command.Title, command.Detail, command.Group, command.Shortcut, command.AltHint is { } hint ? $"Shift+Enter to {hint}" : null }
                .Where(s => !string.IsNullOrEmpty(s))),
            tall: command.Detail is { Length: > 0 },
            spokenHint: command.AltHint is { } said ? $"Run, or Shift+Enter to {said}" : "Run");

    static string DirectText(QuickConnectChoice choice) => $"{DirectVerb(choice)}{DirectAddress(choice)}";

    /// <summary>"SSH to ", or for a web page "Open " (in the browser).</summary>
    static string DirectVerb(QuickConnectChoice choice) => ProtocolInfo.IsWeb(choice.Protocol) ? "Open " : $"{ProtocolInfo.Label(choice.Protocol)} to ";

    /// <summary>user@host:port; a web page's whole address; a serial port's name.</summary>
    static string DirectAddress(QuickConnectChoice choice)
    {
        if (ProtocolInfo.IsWeb(choice.Protocol))
        {
            return WebAddress.Url(choice.Protocol, choice.Host, choice.Port)?.AbsoluteUri ?? choice.Host ?? string.Empty;
        }

        var who = choice.Username is null ? string.Empty : $"{choice.Username}@";
        var port = choice.Protocol != Protocol.Serial && choice.Port != ProtocolInfo.DefaultPort(choice.Protocol) ? $":{choice.Port}" : string.Empty;
        return $"{who}{choice.Host}{port}";
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

    /// <param name="hint">What Enter does, shown on the highlighted row; ↵ is added unless the hint names its keys itself.</param>
    /// <param name="spoken">The row in words, for a screen reader.</param>
    /// <param name="tall">Two lines, such as a saved command's name over its text.</param>
    /// <param name="spokenHint">The hint in words, where it's keys on screen.</param>
    void AddItem(Control content, QuickConnectChoice choice, string hint, string spoken, bool tall = false, string? spokenHint = null)
    {
        var hintText = new TextBlock
        {
            Text = hint.Contains('↵') ? hint : $"{hint}  ↵",
            FontSize = 12,
            Foreground = Dialogs.MutedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            IsVisible = false,
        };
        // The shared row from App.axaml: the selected fill and marker on the highlighted result.
        var row = new Border
        {
            Classes = { "row" },
            Height = tall ? 50 : 40,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Panel
            {
                Children =
                {
                    new DockPanel { Margin = new Thickness(14, 0, 10, 0), Children = { Docked(hintText, Dock.Right), content } },
                    new Border { Classes = { "marker" } },
                },
            },
        };

        var item = new Item(row, hintText, choice, $"{spoken}. {spokenHint ?? hint}");
        int index = _items.Count;
        row.PointerEntered += (_, _) => Select(index, speak: false);
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
                Badge(session.Protocol, session.Label),
                name,
                new TextBlock { Text = session.Address, FontSize = 12, FontFamily = Dialogs.MonoFont, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 },
            },
        };
    }

    static Control DirectRow(QuickConnectChoice choice)
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.Inlines =
        [
            new Run(DirectVerb(choice)),
            new Run(DirectAddress(choice)) { FontFamily = Dialogs.MonoFont },
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

    static Control CommandRow(PaletteCommand command)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new StrokeIcon { Data = Geometry.Parse(Icons.Forward), StrokeThickness = 2, Foreground = SoftBrush, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = command.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 },
            },
        };
        if (command.Shortcut is { } shortcut)
        {
            row.Children.Add(Chip(shortcut));
        }

        if (command.Detail is not { Length: > 0 } detail)
        {
            return row;
        }

        // A saved command's text, on one line under its name, so what will be typed is in view before it's picked.
        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                row,
                new TextBlock
                {
                    Text = detail.ReplaceLineEndings(" ⏎ "),
                    FontFamily = Dialogs.MonoFont,
                    FontSize = 12,
                    Foreground = Dialogs.MutedBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 460,
                    Margin = new Thickness(26, 0, 0, 0),
                },
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

    /// <summary>The name with the first match of the query in the accent color.</summary>
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

    static Control Badge(Protocol protocol, string? label = null) => new Border
    {
        Background = ProtocolStyle.Background(protocol),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = label ?? ProtocolInfo.Label(protocol),
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

    /// <param name="speak">Read the result out: it was reached by keys or typing, not by the pointer passing over it.</param>
    void Select(int index, bool speak = true)
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
            if (speak)
            {
                _spoken.Say($"{_items[index].Spoken}. {index + 1} of {_items.Count}");
            }
        }
    }

    /// <summary>What was last read out, for tests.</summary>
    internal string Spoken => _spoken.Text;

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
            case Key.Enter when e.KeyModifiers == KeyModifiers.Shift:
                if (_selected >= 0 && _items[_selected].Choice is { Command.AltRun: not null } alternative)
                {
                    Finish(alternative with { Alt = true });
                }

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

    sealed record Item(Border Row, TextBlock Hint, QuickConnectChoice Choice, string Spoken)
    {
        public void Show(bool selected)
        {
            Row.Classes.Set("selected", selected);
            Hint.IsVisible = selected;
        }
    }
}
