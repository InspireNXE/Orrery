using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Controls;
using Orrery.App.Credentials;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;

namespace Orrery.App.Connections;

/// <summary>
/// Edits the selected connection or folder. Each inheritable setting has an "Inherit" chip: on, the
/// field shows what the parent provides and stores nothing; off, the node's own value overrides it.
/// Edits are applied with Save, or automatically when another node is selected.
/// </summary>
sealed class PropertiesPanel : UserControl
{
    readonly ConnectionTree _tree;
    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _path = new() { FontSize = 12, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StrokeIcon _tileIcon = new();
    readonly Border _tile;
    readonly TextBox _name = new(), _host = new() { FontFamily = Dialogs.MonoFont };
    readonly TextBox _notes = new() { AcceptsReturn = true, Height = 72, TextWrapping = TextWrapping.Wrap };
    readonly Control _hostField, _nameField, _notesField;
    readonly ComboBox _protocol = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _port = new() { FontFamily = Dialogs.MonoFont };
    readonly CredentialPicker _credential;
    readonly TextBox _username = new(), _domain = new();
    readonly ToggleSwitch _clipboard = Switch("ShareClipboard");
    readonly ToggleSwitch _sshAgent = Switch("UseSshAgent");
    readonly InheritRow _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _clipboardRow, _sshAgentRow;
    readonly TextBlock _error = new() { Foreground = Dialogs.DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Button _connect, _save;
    readonly Control _editorView;
    readonly TextBlock _empty = Pane.EmptyText("Select a connection or folder to see its settings.");

    ConnectionNode? _node;
    bool _loading;
    bool _dirty;
    int _showVersion;

    public PropertiesPanel(ConnectionTree tree, ICredentialStore credentials)
    {
        _tree = tree;
        _credential = new CredentialPicker(credentials);
        _tile = Pane.Tile(_tileIcon);

        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            _protocol.Items.Add(new ComboBoxItem { Content = ProtocolInfo.Label(protocol), Tag = protocol });
        }

        _protocolRow = new InheritRow("Protocol", _protocol, () => ShowInherited(r => SelectProtocol(r.Protocol.Value)));
        _portRow = new InheritRow("Port", _port, () => ShowInherited(r => _port.Text = r.Port.Value.ToString()));
        _credentialRow = new InheritRow("Saved credential", _credential, () => ShowInherited(r => _ = _credential.RefreshAsync(r.CredentialId.Value)));
        _usernameRow = new InheritRow("Username (when no saved credential)", _username, () => ShowInherited(r => _username.Text = r.Username.Value));
        _domainRow = new InheritRow("Domain", _domain, () => ShowInherited(r => _domain.Text = r.Domain.Value));
        _clipboardRow = new InheritRow("Share the clipboard", _clipboard, () => ShowInherited(r => _clipboard.IsChecked = r.ShareClipboard.Value), inline: true);
        _sshAgentRow = new InheritRow("Use SSH agent keys", _sshAgent, () => ShowInherited(r => _sshAgent.IsChecked = r.UseSshAgent.Value), inline: true);
        ToolTip.SetTip(_sshAgentRow.View, "SSH: log in with keys from Pageant or the OpenSSH agent when there is no saved credential.");

        _hostField = Dialogs.Field("Host", _host);
        _nameField = Dialogs.Field("Name", _name);
        _notesField = Pane.Section("Notes", _notes);

        _save = new Button { Content = "Save", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        _save.Click += (_, _) => TrySave();
        var revert = new Button { Content = "Revert", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        revert.Click += async (_, _) => { if (_node is not null) await ShowAsync(_node); };
        _connect = new Button { Content = "Connect", Height = 36, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        _connect.Classes.Add("accent");
        _connect.Click += (_, _) =>
        {
            if (TrySave() && _node is ConnectionEntry entry)
            {
                ConnectRequested?.Invoke(this, entry);
            }
        };

        var editor = new StackPanel
        {
            Spacing = 20,
            Margin = new Thickness(16),
            Children =
            {
                Pane.Header(_tile, _title, _path),
                Pane.Section("Connection", _nameField, _hostField, Pane.Pair(_protocolRow.View, _portRow.View)),
                Pane.Section("Credentials", _credentialRow.View, _usernameRow.View, _domainRow.View),
                Pane.Section("Options", _clipboardRow.View, _sshAgentRow.View),
                _notesField,
            },
        };

        _editorView = Pane.WithFooter(editor, new StackPanel
        {
            Spacing = 8,
            Children = { _error, _connect, Pane.Pair(_save, revert) },
        });

        foreach (var box in new[] { _name, _host, _port, _username, _domain, _notes })
        {
            box.TextChanged += (_, _) => MarkDirty();
        }

        _protocol.SelectionChanged += (_, _) =>
        {
            MarkDirty();
            if (_node is ConnectionEntry entry && (_protocol.SelectedItem as ComboBoxItem)?.Tag is Protocol chosen)
            {
                ShowTile(entry, chosen);
            }
        };
        _credential.ChoiceChanged += (_, _) => MarkDirty();
        _clipboard.IsCheckedChanged += (_, _) => MarkDirty();
        _sshAgent.IsCheckedChanged += (_, _) => MarkDirty();
        foreach (var row in new[] { _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _clipboardRow, _sshAgentRow })
        {
            row.Inherit.IsCheckedChanged += (_, _) => MarkDirty();
        }

        Content = _empty;
    }

    public event EventHandler<ConnectionEntry>? ConnectRequested;

    public ConnectionNode? Node => _node;

    public async Task ShowAsync(ConnectionNode? node)
    {
        if (_dirty && _node is not null && _node != node)
        {
            TrySave();
        }

        _node = node;
        int version = ++_showVersion;
        _error.IsVisible = false;
        if (node is null)
        {
            Content = _empty;
            return;
        }

        _loading = true;
        try
        {
            var own = node.Settings;
            var inherited = SettingsResolver.ResolveInherited(node);

            // The root folder isn't in the list; editing it sets the defaults everything else inherits.
            bool defaults = node.Parent is null;
            _title.Text = defaults ? "Defaults for all connections" : string.IsNullOrWhiteSpace(node.Name) ? "(unnamed)" : node.Name;
            _path.Text = defaults ? "Used by every folder and connection that doesn't set its own. Inherit means Orrery's default."
                : node.Path is { Length: > 0 } path ? path : "Top level";
            _path.TextWrapping = defaults ? TextWrapping.Wrap : TextWrapping.NoWrap;
            _nameField.IsVisible = _notesField.IsVisible = !defaults;
            _name.Text = node.Name;
            _hostField.IsVisible = _connect.IsVisible = node is ConnectionEntry;
            _save.Classes.Set("accent", node is not ConnectionEntry);
            ShowTile(node, own.Protocol ?? inherited.Protocol.Value);
            _host.Text = (node as ConnectionEntry)?.Host;
            _notes.Text = node.Notes;

            _protocolRow.Load(own.Protocol is null, Describe(inherited.Protocol));
            SelectProtocol(own.Protocol ?? inherited.Protocol.Value);

            _portRow.Load(own.Port is null, Describe(inherited.Port));
            _port.Text = (own.Port ?? inherited.Port.Value).ToString();

            _credentialRow.Load(own.CredentialId is null, Describe(inherited.CredentialId));
            await _credential.RefreshAsync(own.CredentialId switch
            {
                null => inherited.CredentialId.Value,
                var id when id == InheritableSettings.NoCredential => null,
                var id => id,
            });
            if (version != _showVersion)
            {
                // Another node was selected while the credential list loaded; that call owns the panel.
                return;
            }

            _usernameRow.Load(own.Username is null, Describe(inherited.Username));
            _username.Text = own.Username ?? inherited.Username.Value;

            _domainRow.Load(own.Domain is null, Describe(inherited.Domain));
            _domain.Text = own.Domain ?? inherited.Domain.Value;

            _clipboardRow.Load(own.ShareClipboard is null, Describe(inherited.ShareClipboard));
            _clipboard.IsChecked = own.ShareClipboard ?? inherited.ShareClipboard.Value;

            _sshAgentRow.Load(own.UseSshAgent is null, Describe(inherited.UseSshAgent));
            _sshAgent.IsChecked = own.UseSshAgent ?? inherited.UseSshAgent.Value;

            Content = _editorView;
        }
        finally
        {
            if (version == _showVersion)
            {
                _loading = false;
                _dirty = false;
            }
        }
    }

    /// <summary>Applies the edits to the node. False, with a message shown, if something is invalid.</summary>
    public bool TrySave()
    {
        if (_node is null)
        {
            return false;
        }

        if (!_dirty)
        {
            return true;
        }

        var name = _name.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return Fail("Give it a name.");
        }

        var host = _host.Text?.Trim() ?? string.Empty;
        if (_node is ConnectionEntry && host.Any(char.IsWhiteSpace))
        {
            return Fail("The host can't contain spaces.");
        }

        int? port = null;
        if (_portRow.Inherit.IsChecked != true)
        {
            if (!int.TryParse(_port.Text, out var p) || p is < 1 or > 65535)
            {
                return Fail("The port must be a number from 1 to 65535.");
            }

            port = p;
        }

        // Empty text with Inherit unticked means "none", overriding the parent.
        _node.Settings = new InheritableSettings
        {
            Protocol = _protocolRow.Inherit.IsChecked == true ? null : (_protocol.SelectedItem as ComboBoxItem)?.Tag as Protocol?,
            Port = port,
            CredentialId = _credentialRow.Inherit.IsChecked == true ? null : _credential.SelectedId ?? InheritableSettings.NoCredential,
            Username = _usernameRow.Inherit.IsChecked == true ? null : _username.Text?.Trim() ?? string.Empty,
            Domain = _domainRow.Inherit.IsChecked == true ? null : _domain.Text?.Trim() ?? string.Empty,
            ShareClipboard = _clipboardRow.Inherit.IsChecked == true ? null : _clipboard.IsChecked == true,
            UseSshAgent = _sshAgentRow.Inherit.IsChecked == true ? null : _sshAgent.IsChecked == true,
        };
        _node.Name = name;
        _node.Notes = string.IsNullOrWhiteSpace(_notes.Text) ? null : _notes.Text.Trim();
        if (_node is ConnectionEntry entry)
        {
            entry.Host = host;
        }

        _dirty = false;
        _error.IsVisible = false;
        _title.Text = name;
        _tree.NotifyChanged();
        return true;
    }

    public void FocusName()
    {
        _name.Focus();
        _name.SelectAll();
    }

    void MarkDirty()
    {
        if (!_loading)
        {
            _dirty = true;
        }
    }

    bool Fail(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
        return false;
    }

    /// <summary>The coloured square beside the name: the protocol for a connection, a folder otherwise.</summary>
    void ShowTile(ConnectionNode node, Protocol protocol)
    {
        bool folder = node is not ConnectionEntry;
        _tile.Background = folder ? FolderTileBrush : ProtocolStyle.Background(protocol);
        _tileIcon.Foreground = folder ? FolderBrush : ProtocolStyle.Foreground(protocol);
        _tileIcon.Data = Geometry.Parse(folder ? Icons.Folder : ProtocolStyle.Icon(protocol));
    }

    static readonly IBrush FolderTileBrush = new SolidColorBrush(Color.Parse("#33291A")).ToImmutable();
    static readonly IBrush FolderBrush = new SolidColorBrush(Color.Parse("#E0B45A")).ToImmutable();

    // Fluent's switch keeps a gap on its right for On/Off text; the negative margin lines the track up
    // with the right edge of the text fields.
    static ToggleSwitch Switch(string name) => new()
    {
        Name = name,
        OnContent = null,
        OffContent = null,
        MinWidth = 0,
        Margin = new Thickness(10, 0, -11, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    void SelectProtocol(Protocol protocol) =>
        _protocol.SelectedItem = _protocol.Items.OfType<ComboBoxItem>().First(i => (Protocol)i.Tag! == protocol);

    void ShowInherited(Action<ResolvedSettings> apply)
    {
        if (_node is not null)
        {
            apply(SettingsResolver.ResolveInherited(_node));
        }
    }

    static string Describe<T>(Resolved<T> resolved) =>
        resolved.Source is { } source ? source.Parent is null ? "From defaults for all connections" : $"From {source.Name}" : "Orrery's default";

    /// <summary>
    /// A label with an "Inherit" chip, the editor, and where the inherited value comes from. Inline rows
    /// (switches) put the editor on the label's line.
    /// </summary>
    sealed class InheritRow
    {
        readonly TextBlock _hint = new() { FontSize = 11, Foreground = Dialogs.MutedBrush };
        readonly Control _editor;

        public InheritRow(string label, Control editor, Action showInherited, bool inline = false)
        {
            _editor = editor;
            Inherit.IsCheckedChanged += (_, _) =>
            {
                bool inheriting = Inherit.IsChecked == true;
                editor.IsEnabled = !inheriting;
                _hint.IsVisible = inheriting;
                if (inheriting)
                {
                    showInherited();
                }
            };
            ToolTip.SetTip(Inherit, "On: use the value from the folder above. Off: set this one's own.");

            var header = new DockPanel();
            if (inline)
            {
                DockPanel.SetDock(editor, Dock.Right);
                header.Children.Add(editor);
            }

            DockPanel.SetDock(Inherit, Dock.Right);
            header.Children.Add(Inherit);
            var title = inline
                ? new TextBlock { Text = label, Foreground = SoftBrush, VerticalAlignment = VerticalAlignment.Center }
                : Dialogs.Label(label);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            header.Children.Add(title);
            View = inline
                ? new StackPanel { Children = { header, _hint } }
                : new StackPanel { Spacing = 4, Children = { header, editor, _hint } };
        }

        static readonly IBrush SoftBrush = new SolidColorBrush(Color.Parse("#C3C9D3")).ToImmutable();

        public ToggleButton Inherit { get; } = new() { Content = "Inherit", Classes = { "inherit" }, VerticalAlignment = VerticalAlignment.Center };

        public Control View { get; }

        public void Load(bool inheriting, string hint)
        {
            _hint.Text = hint;
            Inherit.IsChecked = inheriting;
            _hint.IsVisible = inheriting;
            _editor.IsEnabled = !inheriting;
        }
    }
}
