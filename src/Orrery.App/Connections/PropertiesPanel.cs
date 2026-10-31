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
    readonly ToggleSwitch _sshLegacy = Switch("AllowLegacySsh");
    readonly ToggleSwitch _autoReconnect = Switch("AutoReconnect");
    readonly TextBox _desktopSize = new() { FontFamily = Dialogs.MonoFont, Watermark = "Fit the window" };
    readonly ComboBox _jumpHost = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _forwards = new() { AcceptsReturn = true, MinHeight = 56, FontFamily = Dialogs.MonoFont, Watermark = "L 8080:db.internal:5432\nD 1080" };
    readonly Control _forwardsField;
    readonly InheritRow _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _clipboardRow, _sshAgentRow, _sshLegacyRow, _jumpHostRow, _autoReconnectRow, _desktopSizeRow;
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
        _desktopSizeRow = new InheritRow("Remote desktop size", _desktopSize, () => ShowInherited(r => _desktopSize.Text = r.DesktopSize.Value));
        ToolTip.SetTip(_desktopSizeRow.View, "RDP: empty fits the window and follows it as it changes; or a fixed size like 1920x1080, scaled to fit.");
        _autoReconnectRow = new InheritRow("Reconnect if the connection drops", _autoReconnect, () => ShowInherited(r => _autoReconnect.IsChecked = r.AutoReconnect.Value), inline: true);
        ToolTip.SetTip(_autoReconnectRow.View, "Try again by itself, up to 5 times, when an open session is cut off. Not after logging off or Disconnect.");
        _sshAgentRow = new InheritRow("Use SSH agent keys", _sshAgent, () => ShowInherited(r => _sshAgent.IsChecked = r.UseSshAgent.Value), inline: true);
        ToolTip.SetTip(_sshAgentRow.View, "SSH: log in with keys from Pageant or the OpenSSH agent when there is no saved credential.");
        _sshLegacyRow = new InheritRow("Allow older, weaker algorithms", _sshLegacy, () => ShowInherited(r => _sshLegacy.IsChecked = r.AllowLegacySsh.Value), inline: true);
        ToolTip.SetTip(_sshLegacyRow.View, "SSH: also accept SHA-1, CBC and 3DES algorithms.");
        var legacyHint = new TextBlock { Text = "Only for old devices that can't connect otherwise", FontSize = 11, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };
        _jumpHostRow = new InheritRow("Jump host", _jumpHost, () => ShowInherited(r => SelectJumpHost(r.JumpHostId.Value)));
        ToolTip.SetTip(_jumpHostRow.View, "SSH: reach this through another saved SSH connection first, like ssh -J.");
        _forwardsField = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                Dialogs.Label("Port forwards"),
                _forwards,
                new TextBlock
                {
                    Text = "One per line. L port:host:port opens a port here, R port:host:port one on the server, D port a SOCKS proxy. "
                        + "They listen on 127.0.0.1 unless an address comes first.",
                    FontSize = 11,
                    Foreground = Dialogs.MutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

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
                Pane.Section("Options", _desktopSizeRow.View, _clipboardRow.View, _autoReconnectRow.View, _sshAgentRow.View, new StackPanel { Spacing = 2, Children = { _sshLegacyRow.View, legacyHint } }),
                Pane.Section("SSH tunnels", _jumpHostRow.View, _forwardsField),
                _notesField,
            },
        };

        _editorView = Pane.WithFooter(editor, new StackPanel
        {
            Spacing = 8,
            Children = { _error, _connect, Pane.Pair(_save, revert) },
        });

        foreach (var box in new[] { _name, _host, _port, _username, _domain, _notes, _forwards, _desktopSize })
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
        _sshLegacy.IsCheckedChanged += (_, _) => MarkDirty();
        _jumpHost.SelectionChanged += (_, _) => MarkDirty();
        _autoReconnect.IsCheckedChanged += (_, _) => MarkDirty();
        foreach (var row in new[] { _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _clipboardRow, _sshAgentRow, _sshLegacyRow, _jumpHostRow, _autoReconnectRow, _desktopSizeRow })
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

            _sshLegacyRow.Load(own.AllowLegacySsh is null, Describe(inherited.AllowLegacySsh));
            _sshLegacy.IsChecked = own.AllowLegacySsh ?? inherited.AllowLegacySsh.Value;

            _desktopSizeRow.Load(own.DesktopSize is null, Describe(inherited.DesktopSize));
            _desktopSize.Text = own.DesktopSize ?? inherited.DesktopSize.Value;

            _autoReconnectRow.Load(own.AutoReconnect is null, Describe(inherited.AutoReconnect));
            _autoReconnect.IsChecked = own.AutoReconnect ?? inherited.AutoReconnect.Value;

            FillJumpHosts(node);
            _jumpHostRow.Load(own.JumpHostId is null, Describe(inherited.JumpHostId));
            SelectJumpHost(own.JumpHostId switch
            {
                null => inherited.JumpHostId.Value,
                var id when id == InheritableSettings.NoJumpHost => null,
                var id => id,
            });

            _forwardsField.IsVisible = node is ConnectionEntry;
            _forwards.Text = PortForward.FormatAll((node as ConnectionEntry)?.PortForwards);

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

        string? desktopSize = null;
        if (_desktopSizeRow.Inherit.IsChecked != true)
        {
            if (!DesktopResolution.TryParse(_desktopSize.Text, out var size, out var sizeError))
            {
                return Fail(sizeError!);
            }

            desktopSize = size is { } fixedSize ? $"{fixedSize.Width}x{fixedSize.Height}" : string.Empty;
        }

        List<PortForward>? forwards = null;
        if (_node is ConnectionEntry)
        {
            if (PortForward.TryParseAll(_forwards.Text, out var forwardError) is not { } parsed)
            {
                return Fail(forwardError!);
            }

            forwards = parsed.Count > 0 ? parsed : null;
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
            AllowLegacySsh = _sshLegacyRow.Inherit.IsChecked == true ? null : _sshLegacy.IsChecked == true,
            DesktopSize = desktopSize,
            AutoReconnect = _autoReconnectRow.Inherit.IsChecked == true ? null : _autoReconnect.IsChecked == true,
            JumpHostId = _jumpHostRow.Inherit.IsChecked == true ? null : (_jumpHost.SelectedItem as ComboBoxItem)?.Tag as Guid? ?? InheritableSettings.NoJumpHost,
        };
        _node.Name = name;
        _node.Notes = string.IsNullOrWhiteSpace(_notes.Text) ? null : _notes.Text.Trim();
        if (_node is ConnectionEntry entry)
        {
            entry.Host = host;
            entry.PortForwards = forwards;
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

    /// <summary>"Connect directly", then every saved SSH connection except <paramref name="node"/> itself.</summary>
    void FillJumpHosts(ConnectionNode node)
    {
        _jumpHost.Items.Clear();
        _jumpHost.Items.Add(new ComboBoxItem { Content = "Connect directly", Tag = InheritableSettings.NoJumpHost });
        foreach (var entry in _tree.Root.Descendants().OfType<ConnectionEntry>()
                     .Where(e => e != node && SettingsResolver.Resolve(e).Protocol.Value == Protocol.Ssh)
                     .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _jumpHost.Items.Add(new ComboBoxItem { Content = string.IsNullOrEmpty(entry.Host) ? entry.Name : $"{entry.Name}  ({entry.Host})", Tag = entry.Id });
        }
    }

    /// <summary>Selects the jump host by id; one that's been deleted shows as missing, so saving doesn't quietly drop it.</summary>
    void SelectJumpHost(Guid? id)
    {
        var item = _jumpHost.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (Guid)i.Tag! == (id ?? InheritableSettings.NoJumpHost));
        if (item is null)
        {
            item = new ComboBoxItem { Content = "(a connection that no longer exists)", Tag = id!.Value };
            _jumpHost.Items.Add(item);
        }

        _jumpHost.SelectedItem = item;
    }

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
