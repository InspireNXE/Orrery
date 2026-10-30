using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Orrery.App.Credentials;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;

namespace Orrery.App.Connections;

/// <summary>
/// Edits the selected connection or folder. Each inheritable setting has an "Inherit" box: ticked, the
/// field shows what the parent provides and stores nothing; unticked, the node's own value overrides it.
/// Edits are applied with Save, or automatically when another node is selected.
/// </summary>
sealed class PropertiesPanel : UserControl
{
    readonly ConnectionTree _tree;
    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _path = new() { FontSize = 12, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBox _name = new(), _host = new() { FontFamily = Dialogs.MonoFont };
    readonly TextBox _notes = new() { AcceptsReturn = true, Height = 72, TextWrapping = TextWrapping.Wrap };
    readonly Control _hostField, _nameField, _notesField;
    readonly ComboBox _protocol = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _port = new() { FontFamily = Dialogs.MonoFont };
    readonly CredentialPicker _credential;
    readonly TextBox _username = new(), _domain = new();
    readonly CheckBox _clipboard = new() { Content = "Share the clipboard" };
    readonly InheritRow _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _clipboardRow;
    readonly TextBlock _error = new() { Foreground = Dialogs.DangerBrush, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Button _connect;
    readonly ScrollViewer _editorView;
    readonly TextBlock _empty = new()
    {
        Text = "Select a connection or folder to see its settings.",
        Foreground = Dialogs.MutedBrush,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(20),
    };

    ConnectionNode? _node;
    bool _loading;
    bool _dirty;
    int _showVersion;

    public PropertiesPanel(ConnectionTree tree, ICredentialStore credentials)
    {
        _tree = tree;
        _credential = new CredentialPicker(credentials);

        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            _protocol.Items.Add(new ComboBoxItem { Content = ProtocolInfo.Label(protocol), Tag = protocol });
        }

        _protocolRow = new InheritRow("Protocol", _protocol, () => ShowInherited(r => SelectProtocol(r.Protocol.Value)));
        _portRow = new InheritRow("Port", _port, () => ShowInherited(r => _port.Text = r.Port.Value.ToString()));
        _credentialRow = new InheritRow("Saved credential", _credential, () => ShowInherited(r => _ = _credential.RefreshAsync(r.CredentialId.Value)));
        _usernameRow = new InheritRow("Username (when no saved credential)", _username, () => ShowInherited(r => _username.Text = r.Username.Value));
        _domainRow = new InheritRow("Domain", _domain, () => ShowInherited(r => _domain.Text = r.Domain.Value));
        _clipboardRow = new InheritRow("Clipboard", _clipboard, () => ShowInherited(r => _clipboard.IsChecked = r.ShareClipboard.Value));

        _hostField = Dialogs.Field("Host", _host);
        _nameField = Dialogs.Field("Name", _name);
        _notesField = Dialogs.Field("Notes", _notes);

        var save = new Button { Content = "Save", Padding = new Thickness(16, 6) };
        save.Classes.Add("accent");
        save.Click += (_, _) => TrySave();
        var revert = new Button { Content = "Revert", Padding = new Thickness(12, 6) };
        revert.Click += async (_, _) => { if (_node is not null) await ShowAsync(_node); };
        _connect = new Button { Content = "Connect", Padding = new Thickness(12, 6) };
        _connect.Click += (_, _) =>
        {
            if (TrySave() && _node is ConnectionEntry entry)
            {
                ConnectRequested?.Invoke(this, entry);
            }
        };

        var editor = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(20),
            Children =
            {
                new StackPanel { Spacing = 2, Children = { _title, _path } },
                _nameField,
                _hostField,
                _protocolRow.View,
                _portRow.View,
                _credentialRow.View,
                _usernameRow.View,
                _domainRow.View,
                _clipboardRow.View,
                _notesField,
                _error,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, revert, _connect } },
            },
        };
        _editorView = new ScrollViewer { Content = editor };

        foreach (var box in new[] { _name, _host, _port, _username, _domain, _notes })
        {
            box.TextChanged += (_, _) => MarkDirty();
        }

        _protocol.SelectionChanged += (_, _) => MarkDirty();
        _credential.ChoiceChanged += (_, _) => MarkDirty();
        _clipboard.IsCheckedChanged += (_, _) => MarkDirty();
        foreach (var row in new[] { _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _clipboardRow })
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

    /// <summary>A label, an "Inherit" box, the editor, and where the inherited value comes from.</summary>
    sealed class InheritRow
    {
        readonly TextBlock _hint = new() { FontSize = 11, Foreground = Dialogs.MutedBrush };
        readonly Control _editor;

        public InheritRow(string label, Control editor, Action showInherited)
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

            var header = new DockPanel();
            DockPanel.SetDock(Inherit, Dock.Right);
            header.Children.Add(Inherit);
            header.Children.Add(Dialogs.Label(label));
            View = new StackPanel { Spacing = 4, Children = { header, editor, _hint } };
        }

        public CheckBox Inherit { get; } = new() { Content = "Inherit", FontSize = 11, MinHeight = 0, Padding = new Thickness(4, 0, 0, 0) };

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
