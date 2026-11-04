using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;

namespace Orrery.App.Connections;

/// <summary>A saved command and where it's saved: a connection, a folder, or the defaults (the tree's top).</summary>
sealed record CommandAt(ConnectionNode Owner, SavedCommand Command);

/// <summary>
/// The side panel's Commands page: every saved command in the tree, grouped by where it's saved (the defaults first,
/// then folders and connections in tree order), with a filter. Choosing one opens it in <see cref="CommandEditorPane"/>.
/// </summary>
sealed class CommandsPanel : DockPanel
{
    readonly ConnectionTree _tree;
    readonly TextBox _filter = new() { PlaceholderText = "Filter", Margin = new Thickness(12, 0, 12, 8), FontSize = 12, MinHeight = 30 };
    readonly ListBox _list = new() { Background = Brushes.Transparent, Padding = new Thickness(12, 0), Classes = { "side" } };
    readonly TextBlock _empty = Pane.EmptyText(EmptyNote, side: true);
    const string EmptyNote = "Commands you save show up here. Add one with +, or from a terminal's menu.";
    bool _filling;

    public CommandsPanel(ConnectionTree tree)
    {
        _tree = tree;
        AutomationProperties.SetName(_filter, "Filter commands");
        AutomationProperties.SetName(_list, "Saved commands");
        var add = new Button { Content = StrokeIcon.Create(Icons.Plus, 15), Width = 28, Height = 28 };
        add.Classes.Add("icon");
        ToolTip.SetTip(add, "New command");
        AutomationProperties.SetName(add, "New command");
        add.Click += (_, _) => NewRequested?.Invoke(this, EventArgs.Empty);

        Children.Add(Pane.Docked(Header = new DockPanel
        {
            Margin = new Thickness(14, 10, 10, 6),
            Height = 28,
            Children =
            {
                Pane.Docked(add, Dock.Right),
                new TextBlock { Text = "COMMANDS", Classes = { "section" }, VerticalAlignment = VerticalAlignment.Center },
            },
        }, Dock.Top));
        Children.Add(Pane.Docked(_filter, Dock.Top));
        Children.Add(new Panel { Children = { _list, _empty } });

        _filter.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Refresh();
            }
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (!_filling && Selected is { } chosen)
            {
                Chosen?.Invoke(this, chosen);
            }
        };
        _tree.Changed += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>The header row, which the main window adds its pin button to.</summary>
    public DockPanel Header { get; }

    /// <summary>A command was chosen in the list.</summary>
    public event EventHandler<CommandAt>? Chosen;

    /// <summary>The + button: a new command.</summary>
    public event EventHandler? NewRequested;

    public CommandAt? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as CommandAt;

    internal TextBox FilterBox => _filter;

    /// <summary>The commands the list shows now, without the group headers.</summary>
    internal IEnumerable<CommandAt> Listed => _list.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<CommandAt>();

    /// <summary>Every command, in tree order, with the defaults' first.</summary>
    public IEnumerable<CommandAt> All() =>
        _tree.Root.Descendants().Prepend(_tree.Root).SelectMany(node => (node.Commands ?? []).Select(c => new CommandAt(node, c)));

    /// <summary>Lists the commands again, keeping the one selected when it's still there.</summary>
    public void Refresh()
    {
        var keep = Selected;
        var filter = _filter.Text?.Trim() ?? string.Empty;
        var items = new List<ListBoxItem>();
        ListBoxItem? selected = null;
        foreach (var group in All().Where(c => Matches(c, filter)).GroupBy(c => c.Owner))
        {
            items.Add(GroupHeader(group.Key));
            foreach (var at in group)
            {
                var item = new ListBoxItem { Tag = at, Content = Row(at.Command) };
                AutomationProperties.SetName(item, $"{at.Command.Name}, {Where(at.Owner)}");
                items.Add(item);
                if (keep is not null && keep.Command == at.Command)
                {
                    selected = item;
                }
            }
        }

        _filling = true;
        _list.ItemsSource = items;
        _list.SelectedItem = selected;
        _filling = false;
        _list.IsVisible = items.Count > 0;
        _empty.IsVisible = items.Count == 0;
        _empty.Text = filter.Length > 0 ? "No command matches the filter." : EmptyNote;
    }

    /// <summary>Selects <paramref name="command"/> in the list, as after it's saved, without opening it again.</summary>
    public void Select(SavedCommand? command)
    {
        _filling = true;
        _list.SelectedItem = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is CommandAt at && at.Command == command);
        _filling = false;
    }

    static bool Matches(CommandAt at, string filter) =>
        filter.Length == 0
        || at.Command.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || at.Command.Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Where(at.Owner).Contains(filter, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Where a command is saved, as its group says.</summary>
    internal static string Where(ConnectionNode owner) => owner switch
    {
        ConnectionFolder { Parent: null } => "Every connection (the defaults)",
        ConnectionFolder folder => folder.Path is { Length: > 0 } path ? $"{path} / {folder.Name}" : folder.Name,
        _ => owner.Path is { Length: > 0 } path ? $"{owner.Name} · {path}" : owner.Name,
    };

    static ListBoxItem GroupHeader(ConnectionNode owner)
    {
        var icon = StrokeIcon.Create(owner is ConnectionFolder ? Icons.Folder : Icons.Terminal, 12);
        icon.Margin = new Thickness(0, 0, 6, 0);
        return new ListBoxItem
        {
            Focusable = false,
            IsHitTestVisible = false,
            Padding = new Thickness(2, 10, 2, 2),
            Content = new DockPanel
            {
                Children =
                {
                    Pane.Docked(icon, Dock.Left),
                    new TextBlock { Text = Where(owner), FontSize = 11, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
    }

    static Control Row(SavedCommand command)
    {
        var marks = Marks(command);
        var row = new StackPanel
        {
            Spacing = 1,
            Children =
            {
                new TextBlock { Text = command.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock
                {
                    Text = command.Text.ReplaceLineEndings(" ⏎ "),
                    FontFamily = Dialogs.MonoFont,
                    FontSize = 11,
                    Foreground = Dialogs.MutedBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                new TextBlock { Text = string.Join(" · ", marks), FontSize = 11, Foreground = command.Confirm ? Dialogs.WarningBrush : Dialogs.MutedBrush, IsVisible = marks.Count > 0 },
            },
        };
        ToolTip.SetTip(row, command.Text);
        return row;
    }

    /// <summary>What a command does besides typing its text, as its row says under it.</summary>
    internal static List<string> Marks(SavedCommand command)
    {
        var marks = new List<string>();
        if (command.WhenConnected) marks.Add("when connected");
        if (command.AnswersPassword) marks.Add("types a password");
        if (command.Confirm) marks.Add("asks first");
        if (!command.PressEnter) marks.Add("no Enter");
        return marks;
    }
}

/// <summary>Where the Commands page's pane can send a command: the current session, as its button says.</summary>
sealed record CommandSendTarget(string Label, Func<Task> Send);

/// <summary>
/// The right-hand pane beside the Commands page: the chosen command in the editor, saved on whichever place is picked
/// (moving it there), with Save, Delete, and Send to the current session when that session has the command.
/// </summary>
sealed class CommandEditorPane : ContentControl
{
    readonly ConnectionTree _tree;
    readonly ICredentialStore _credentials;
    readonly Func<CommandAt, CommandSendTarget?> _sendTarget;
    readonly TextBlock _nothing = Pane.EmptyText("Choose a command to see it here, or add one with +.");
    CommandAt? _shown;
    SavedCommandEditor? _editor;
    Button? _send;

    /// <param name="sendTarget">The current session, when it's a connected terminal that has the command; null otherwise.</param>
    public CommandEditorPane(ConnectionTree tree, ICredentialStore credentials, Func<CommandAt, CommandSendTarget?> sendTarget)
    {
        _tree = tree;
        _credentials = credentials;
        _sendTarget = sendTarget;
        Content = _nothing;
    }

    /// <summary>A command was saved, moved or deleted on these nodes (a move changes two), so whatever shows them can catch up.</summary>
    public event EventHandler<IReadOnlyList<ConnectionNode>>? Changed;

    /// <summary>The command it's showing; null for a new one or nothing.</summary>
    public CommandAt? Shown => _shown;

    internal SavedCommandEditor? Editor => _editor;

    internal Button? SendButton => _send;

    /// <summary>Shows <paramref name="at"/> to edit, or a new command to save on <paramref name="placeFor"/> when it's null.</summary>
    public void Show(CommandAt? at, ConnectionNode? placeFor = null)
    {
        _shown = at;
        var places = Places();
        var owner = at?.Owner ?? placeFor ?? _tree.Root;
        int selected = Math.Max(0, places.FindIndex(p => p.Node == owner));
        var editor = _editor = new SavedCommandEditor(at?.Command, places[selected].Label, _credentials, owner,
            $"{(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")}+Enter saves it");
        editor.UsePlaces(places, selected);

        var save = new Button { Content = at is null ? "Add" : "Save" };
        save.Classes.Add("accent");
        save.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (at is not null)
        {
            var delete = new Button { Content = "Delete", Foreground = Dialogs.DangerBrush };
            delete.Click += async (_, _) => await DeleteAsync();
            buttons.Children.Add(delete);
            _send = new Button();
            _send.Click += async (_, _) => await SendAsync();
            buttons.Children.Add(_send);
        }
        else
        {
            _send = null;
        }

        buttons.Children.Add(save);
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control))
            {
                e.Handled = true;
                Save();
            }
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 12, 16, 16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = at is null ? "New command" : at.Command.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    editor,
                    buttons,
                },
            },
        };
        UpdateSend();
        _ = editor.LoadAsync();
        Dispatcher(() => (at is null ? editor.NameBox : editor.TextBox).Focus());
    }

    /// <summary>Shows nothing, as when the command it showed is gone.</summary>
    public void Clear()
    {
        _shown = null;
        _editor = null;
        _send = null;
        Content = _nothing;
    }

    /// <summary>Says where Send goes now, as after another tab is chosen.</summary>
    public void UpdateSend()
    {
        if (_send is null || _shown is null)
        {
            return;
        }

        var target = _sendTarget(_shown);
        _send.IsVisible = target is not null;
        _send.Content = target?.Label;
        ToolTip.SetTip(_send, target is null ? null : "Saves any changes, then sends it, asking first if it says to");
    }

    /// <summary>Every place a command can be saved: the defaults, each folder, each connection.</summary>
    List<CommandPlace> Places()
    {
        var places = new List<CommandPlace> { new("Every connection, and terminals without one", _tree.Root) };
        foreach (var node in _tree.Root.Descendants())
        {
            places.Add(node is ConnectionFolder
                ? new CommandPlace($"Everything in {CommandsPanel.Where(node)}", node)
                : new CommandPlace($"{CommandsPanel.Where(node)} only", node));
        }

        return places;
    }

    /// <summary>Saves the editor's command where it's placed, moving it there from where it was. True when it was saved.</summary>
    internal bool Save()
    {
        if (_editor?.Build() is not { } built || _editor.ChosenPlace is not { } place || _tree.Find(place.Node.Id) is null && place.Node != _tree.Root)
        {
            return false;
        }

        var changed = new List<ConnectionNode> { place.Node };
        var into = place.Node.Commands ??= [];
        if (_shown is { } old && old.Owner.Commands is { } from && from.IndexOf(old.Command) is >= 0 and var at)
        {
            if (old.Owner == place.Node)
            {
                from[at] = built;
            }
            else
            {
                from.RemoveAt(at);
                if (from.Count == 0)
                {
                    old.Owner.Commands = null;
                }

                into.Add(built);
                changed.Add(old.Owner);
            }
        }
        else
        {
            into.Add(built);
        }

        // Shown before listeners hear, so they can select the saved one.
        _tree.NotifyChanged();
        Show(new CommandAt(place.Node, built));
        Changed?.Invoke(this, changed);
        return true;
    }

    async Task DeleteAsync()
    {
        if (_shown is not { } at || TopLevel.GetTopLevel(this) is not Window owner
            || !await Dialogs.ConfirmAsync(owner, "Delete command", $"Delete \"{at.Command.Name}\" from {CommandsPanel.Where(at.Owner)}?", "Delete"))
        {
            return;
        }

        if (at.Owner.Commands is { } commands && commands.Remove(at.Command))
        {
            if (commands.Count == 0)
            {
                at.Owner.Commands = null;
            }

            _tree.NotifyChanged();
            Changed?.Invoke(this, [at.Owner]);
        }

        Clear();
    }

    async Task SendAsync()
    {
        if (!Save() || _shown is null || _sendTarget(_shown) is not { } target)
        {
            return;
        }

        await target.Send();
    }

    static void Dispatcher(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action, Avalonia.Threading.DispatcherPriority.Background);
}
