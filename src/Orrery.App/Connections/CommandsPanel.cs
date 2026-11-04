using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Orrery.App.Controls;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;

namespace Orrery.App.Connections;

/// <summary>
/// A saved command and where it's saved: one copy on a connection, a folder or the defaults (the tree's top), or the
/// linked copies of one saved on several places.
/// </summary>
sealed record CommandAt(IReadOnlyList<CommandCopy> Copies)
{
    public CommandAt(ConnectionNode owner, SavedCommand command) : this([new CommandCopy(owner, command)]) { }

    public SavedCommand Command => Copies[0].Command;

    public ConnectionNode Owner => Copies[0].Owner;

    public IReadOnlyList<ConnectionNode> Owners => [.. Copies.Select(c => c.Owner)];

    /// <summary>Whether <paramref name="command"/> is one of its copies.</summary>
    public bool Has(SavedCommand command) => Copies.Any(c => c.Command == command);
}

/// <summary>
/// The side panel's Commands page: every saved command in the tree, grouped by where it's saved (the defaults first,
/// then those on several places, then folders and connections in tree order), with a filter. Choosing one opens it in
/// <see cref="CommandEditorPane"/>; right-clicking one, a group or the space under them offers what can be done there.
/// </summary>
sealed class CommandsPanel : DockPanel
{
    readonly ConnectionTree _tree;
    readonly TextBox _filter = new() { PlaceholderText = "Filter", Margin = new Thickness(12, 0, 12, 8), FontSize = 12, MinHeight = 30 };
    readonly ListBox _list = new() { Background = Brushes.Transparent, Padding = new Thickness(12, 0), Classes = { "side" } };
    readonly TextBlock _empty = Pane.EmptyText(EmptyNote, side: true);
    const string EmptyNote = "Commands you save show up here. Add one with +, or from a terminal's menu.";

    /// <summary>The group of commands saved on more than one place, which has no single place to add to.</summary>
    static readonly object SeveralPlaces = new();
    bool _filling;

    // What the menu opened on: a command, a group's place, or nothing (the space under them).
    object? _menuFor;
    readonly MenuItem _sendItem = new();
    readonly MenuItem _editItem = new() { Header = "Edit" };
    readonly MenuItem _duplicateItem = new() { Header = "Duplicate" };
    readonly MenuItem _copyItem = new() { Header = "Copy command" };
    readonly MenuItem _upItem = new() { Header = "Move up" };
    readonly MenuItem _downItem = new() { Header = "Move down" };
    readonly MenuItem _newItem = new();
    readonly MenuItem _deleteItem = new() { Header = "Delete…", InputGesture = new KeyGesture(Key.Delete) };
    readonly Separator _commandSeparator = new();
    readonly Separator _deleteSeparator = new();

    public CommandsPanel(ConnectionTree tree)
    {
        _tree = tree;
        AutomationProperties.SetName(_filter, "Filter commands");
        AutomationProperties.SetName(_list, "Saved commands");
        var add = new Button { Content = StrokeIcon.Create(Icons.Plus, 15), Width = 28, Height = 28 };
        add.Classes.Add("icon");
        ToolTip.SetTip(add, "New command");
        AutomationProperties.SetName(add, "New command");
        add.Click += (_, _) => NewRequested?.Invoke(this, null);

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
        WireMenu();
        Refresh();
    }

    /// <summary>The header row, which the main window adds its pin button to.</summary>
    public DockPanel Header { get; }

    /// <summary>A command was chosen in the list.</summary>
    public event EventHandler<CommandAt>? Chosen;

    /// <summary>The + button or the menu: a new command, on the given place, or the tree's selection when null.</summary>
    public event EventHandler<ConnectionNode?>? NewRequested;

    /// <summary>The right-hand pane the menu's Send, Duplicate, Move and Delete act through.</summary>
    public CommandEditorPane? Editor { get; set; }

    public CommandAt? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as CommandAt;

    internal TextBox FilterBox => _filter;

    /// <summary>The commands the list shows now, without the group headers.</summary>
    internal IEnumerable<CommandAt> Listed => _list.Items.OfType<ListBoxItem>().Select(i => i.Tag).OfType<CommandAt>();

    /// <summary>Every command, in tree order with the defaults' first, the copies of one on several places together.</summary>
    public IEnumerable<CommandAt> All() => SavedCommands.All(_tree.Root).Select(copies => new CommandAt(copies));

    /// <summary>Lists the commands again, keeping the one selected when it's still there.</summary>
    public void Refresh()
    {
        var keep = Selected;
        var filter = _filter.Text?.Trim() ?? string.Empty;
        var items = new List<ListBoxItem>();
        ListBoxItem? selected = null;
        var groups = All().Where(c => Matches(c, filter))
            .GroupBy(c => c.Copies.Count > 1 ? SeveralPlaces : c.Owner)
            .OrderBy(g => g.Key == _tree.Root ? 0 : g.Key == SeveralPlaces ? 1 : 2);
        foreach (var group in groups)
        {
            items.Add(GroupHeader(group.Key as ConnectionNode));
            foreach (var at in group)
            {
                var item = new ListBoxItem { Tag = at, Content = Row(at) };
                AutomationProperties.SetName(item, $"{at.Command.Name}, {Places(at)}");
                items.Add(item);
                if (keep is not null && at.Has(keep.Command))
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
        _list.SelectedItem = command is null ? null
            : _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is CommandAt at && at.Has(command));
        _filling = false;
    }

    static bool Matches(CommandAt at, string filter) =>
        filter.Length == 0
        || at.Command.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || at.Command.Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || at.Owners.Any(o => Where(o).Contains(filter, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Where a command is saved, as its group says.</summary>
    internal static string Where(ConnectionNode owner) => owner switch
    {
        ConnectionFolder { Parent: null } => "Every connection (the defaults)",
        ConnectionFolder folder => folder.Path is { Length: > 0 } path ? $"{path} / {folder.Name}" : folder.Name,
        _ => owner.Path is { Length: > 0 } path ? $"{owner.Name} · {path}" : owner.Name,
    };

    /// <summary>A place's name, short, as the menu and a linked command's row say it.</summary>
    internal static string ShortName(ConnectionNode node) => node.Parent is null ? "the defaults" : node.Name;

    /// <summary>Every place a command is saved on, for its row and screen readers.</summary>
    static string Places(CommandAt at) => at.Copies.Count == 1 ? Where(at.Owner) : string.Join(", ", at.Owners.Select(ShortName));

    static ListBoxItem GroupHeader(ConnectionNode? owner)
    {
        var icon = StrokeIcon.Create(owner is ConnectionFolder or null ? Icons.Folder : Icons.Terminal, 12);
        icon.Margin = new Thickness(0, 0, 6, 0);

        // Right-clicking it offers a new command there, but a click doesn't select it.
        var header = new ListBoxItem
        {
            Tag = owner,
            Focusable = false,
            Padding = new Thickness(2, 10, 2, 2),
            Content = new DockPanel
            {
                Children =
                {
                    Pane.Docked(icon, Dock.Left),
                    new TextBlock
                    {
                        Text = owner is null ? "On several places" : Where(owner),
                        FontSize = 11,
                        Foreground = Dialogs.MutedBrush,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            },
        };
        header.Classes.Add("group");
        header.AddHandler(PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        return header;
    }

    static Control Row(CommandAt at)
    {
        var command = at.Command;
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
                new TextBlock
                {
                    Text = $"On {Places(at)}",
                    FontSize = 11,
                    Foreground = Dialogs.MutedBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsVisible = at.Copies.Count > 1,
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

    // ---- The menu ----

    void WireMenu()
    {
        // What's under the pointer, or the selected row for the menu key and Shift+F10, before the menu opens.
        _list.AddHandler(ContextRequestedEvent, (sender, e) =>
        {
            var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            _menuFor = item is not null ? item.Tag : e.TryGetPosition(_list, out _) ? null : Selected;
        }, RoutingStrategies.Tunnel);

        _sendItem.Click += async (_, _) =>
        {
            if (_menuFor is CommandAt at && Editor is { } editor)
            {
                await editor.SendAsync(at);
            }
        };
        _editItem.Click += (_, _) =>
        {
            if (_menuFor is CommandAt at)
            {
                Select(at.Command);
                Chosen?.Invoke(this, at);
            }
        };
        _duplicateItem.Click += (_, _) =>
        {
            if (_menuFor is CommandAt at)
            {
                Editor?.Duplicate(at);
            }
        };
        _copyItem.Click += async (_, _) =>
        {
            if (_menuFor is CommandAt at && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(at.Command.Text);
            }
        };
        _upItem.Click += (_, _) => Move(-1);
        _downItem.Click += (_, _) => Move(1);
        _newItem.Click += (_, _) => NewRequested?.Invoke(this, PlaceOf(_menuFor));
        _deleteItem.Click += async (_, _) => await DeleteAsync(_menuFor as CommandAt);

        _list.ContextMenu = new ContextMenu
        {
            Items = { _sendItem, _editItem, _duplicateItem, _copyItem, _upItem, _downItem, _commandSeparator, _newItem, _deleteSeparator, _deleteItem },
        };
        _list.ContextMenu.Opening += (_, _) => ShowMenuFor(_menuFor);
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Delete && Selected is { } at)
            {
                e.Handled = true;
                await DeleteAsync(at);
            }
        };
    }

    /// <summary>Sets the menu up for <paramref name="target"/> (a command, a place, or null); the tests call it, as they can't right-click.</summary>
    internal ContextMenu ShowMenuFor(object? target)
    {
        _menuFor = target;
        var at = target as CommandAt;
        bool command = at is not null;
        bool single = at is { Copies.Count: 1 };
        var send = at is null ? null : Editor?.SendTargetFor(at);
        _sendItem.IsVisible = send is not null;
        _sendItem.Header = send?.Label;
        _editItem.IsVisible = _duplicateItem.IsVisible = _copyItem.IsVisible = command;
        _commandSeparator.IsVisible = _deleteSeparator.IsVisible = _deleteItem.IsVisible = command;

        // A linked command is listed in its own place on each, so there's no one order to move it in.
        _upItem.IsVisible = _downItem.IsVisible = single;
        _duplicateItem.IsEnabled = _upItem.IsEnabled = _downItem.IsEnabled = _deleteItem.IsEnabled = Editor is not null;
        if (single && at!.Owner.Commands is { } siblings)
        {
            int index = siblings.IndexOf(at.Command);
            _upItem.IsEnabled &= index > 0;
            _downItem.IsEnabled &= index >= 0 && index < siblings.Count - 1;
        }

        _newItem.Header = PlaceOf(target) is { } place ? $"New command on {ShortName(place)}…" : "New command…";
        _deleteItem.Header = at is { Copies.Count: > 1 } ? $"Delete from all {at.Copies.Count} places…" : "Delete…";
        return _list.ContextMenu!;
    }

    internal MenuItem SendItem => _sendItem;
    internal MenuItem NewItem => _newItem;
    internal MenuItem UpItem => _upItem;
    internal MenuItem DownItem => _downItem;
    internal MenuItem DeleteItem => _deleteItem;

    /// <summary>Where the menu's New command goes: the command's place when it has one, or the group's.</summary>
    static ConnectionNode? PlaceOf(object? target) => target switch
    {
        CommandAt { Copies.Count: 1 } at => at.Owner,
        ConnectionNode node => node,
        _ => null,
    };

    void Move(int by)
    {
        if (_menuFor is CommandAt at)
        {
            Editor?.Move(at, by);
        }
    }

    async Task DeleteAsync(CommandAt? at)
    {
        if (at is not null && Editor is { } editor)
        {
            await editor.DeleteAsync(at);
        }
    }
}

/// <summary>Where the Commands page's pane can send a command: the current session, as its button says.</summary>
sealed record CommandSendTarget(string Label, Func<Task> Send);

/// <summary>
/// The right-hand pane beside the Commands page: the chosen command in the editor, saved on whichever places are
/// ticked (moving it, or linking copies on several), with Save, Delete, and Send to the current session when that
/// session has the command.
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

    /// <summary>Commands were saved, moved or deleted on these nodes, so whatever shows them can catch up.</summary>
    public event EventHandler<IReadOnlyList<ConnectionNode>>? Changed;

    /// <summary>The command it's showing; null for a new one or nothing.</summary>
    public CommandAt? Shown => _shown;

    internal SavedCommandEditor? Editor => _editor;

    internal Button? SendButton => _send;

    /// <summary>Shows <paramref name="at"/> to edit, or a new command to save on <paramref name="placeFor"/> when it's null.</summary>
    public void Show(CommandAt? at, ConnectionNode? placeFor = null)
    {
        _shown = at;
        IReadOnlyList<ConnectionNode> owners = at?.Owners ?? [placeFor ?? _tree.Root];
        var editor = _editor = new SavedCommandEditor(at?.Command, CommandsPanel.ShortName(owners[0]), _credentials, owners,
            $"{(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")}+Enter saves it");
        editor.UsePlaces(_tree, owners);

        var save = new Button { Content = at is null ? "Add" : "Save" };
        save.Classes.Add("accent");
        save.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (at is not null)
        {
            var delete = new Button { Content = "Delete", Foreground = Dialogs.DangerBrush };
            delete.Click += async (_, _) => await DeleteAsync(at);
            buttons.Children.Add(delete);
            _send = new Button();
            _send.Click += async (_, _) => await SendAsync(at);
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
        Post(() => (at is null ? editor.NameBox : editor.TextBox).Focus());
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

    /// <summary>Where Send goes for <paramref name="at"/> now, or null when the current tab can't take it.</summary>
    public CommandSendTarget? SendTargetFor(CommandAt at) => _sendTarget(at);

    /// <summary>Saves the editor's command on every place ticked, in place of where it was. True when it was saved.</summary>
    internal bool Save()
    {
        if (_editor?.Build() is not { } built)
        {
            return false;
        }

        var places = _editor.ChosenPlaces.Where(p => p == _tree.Root || _tree.Find(p.Id) is not null).ToList();
        if (places.Count == 0)
        {
            return false;
        }

        var old = _shown?.Copies ?? [];
        var saved = SavedCommands.Save(old, built, places);
        _tree.NotifyChanged();

        // Shown before listeners hear, so they can select the saved one.
        Show(new CommandAt(saved));
        Changed?.Invoke(this, [.. old.Select(c => c.Owner).Union(places)]);
        return true;
    }

    /// <summary>Adds a copy of <paramref name="at"/> just under it on each of its places and opens the copy to edit.</summary>
    public void Duplicate(CommandAt at)
    {
        Guid? link = at.Copies.Count > 1 ? Guid.NewGuid() : null;
        var copies = new List<CommandCopy>();
        foreach (var (owner, command) in at.Copies)
        {
            if (owner.Commands is { } list && list.IndexOf(command) is >= 0 and var index)
            {
                var copy = command.Clone();
                copy.Name = $"{command.Name} copy";
                copy.LinkId = link;
                list.Insert(index + 1, copy);
                copies.Add(new CommandCopy(owner, copy));
            }
        }

        if (copies.Count == 0)
        {
            return;
        }

        _tree.NotifyChanged();
        Show(new CommandAt(copies));
        Changed?.Invoke(this, at.Owners);
    }

    /// <summary>Moves a command on one place up (-1) or down (1): the order menus list them and they're sent in when connected.</summary>
    public void Move(CommandAt at, int by)
    {
        if (at.Copies.Count != 1 || at.Owner.Commands is not { } commands || commands.IndexOf(at.Command) is not (>= 0 and var index)
            || index + by < 0 || index + by >= commands.Count)
        {
            return;
        }

        (commands[index], commands[index + by]) = (commands[index + by], commands[index]);
        _tree.NotifyChanged();
        Changed?.Invoke(this, [at.Owner]);
    }

    /// <summary>Sends <paramref name="at"/> to the current tab; the one shown is saved first.</summary>
    public async Task SendAsync(CommandAt at)
    {
        if (_shown is { } shown && shown.Has(at.Command))
        {
            if (!Save() || _shown is null)
            {
                return;
            }

            at = _shown;
        }

        if (_sendTarget(at) is { } target)
        {
            await target.Send();
        }
    }

    /// <summary>Deletes <paramref name="at"/> from every place it's saved on once that's confirmed, clearing the pane if it was showing it.</summary>
    public async Task DeleteAsync(CommandAt at)
    {
        string where = at.Copies.Count == 1 ? CommandsPanel.Where(at.Owner) : $"all {at.Copies.Count} places it's saved on";
        if (TopLevel.GetTopLevel(this) is not Window owner
            || !await Dialogs.ConfirmAsync(owner, "Delete command", $"Delete \"{at.Command.Name}\" from {where}?", "Delete"))
        {
            return;
        }

        if (_shown is { } shown && shown.Has(at.Command))
        {
            Clear();
        }

        SavedCommands.Delete(at.Copies);
        _tree.NotifyChanged();
        Changed?.Invoke(this, at.Owners);
    }

    static void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action, Avalonia.Threading.DispatcherPriority.Background);
}
