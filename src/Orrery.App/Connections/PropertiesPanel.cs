using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Orrery.App.Controls;
using Orrery.App.Credentials;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Network;
using Orrery.Core.Sessions;

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
    readonly ToggleSwitch _enlarge = Switch("EnlargeRemoteDesktop");
    readonly TextBox _desktopSize = new() { FontFamily = Dialogs.MonoFont, Watermark = "Fit the window" };
    readonly ComboBox _jumpHost = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox _quality = new() { Name = "DisplayQuality", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _forwards = new() { AcceptsReturn = true, MinHeight = 56, FontFamily = Dialogs.MonoFont, Watermark = "L 8080:db.internal:5432\nD 1080" };
    readonly Control _forwardsField;
    readonly TextBox _mac = new() { FontFamily = Dialogs.MonoFont, Watermark = "00:1A:2B:3C:4D:5E" };
    readonly TextBox _broadcast = new() { FontFamily = Dialogs.MonoFont, Watermark = "255.255.255.255" };
    readonly Control _wakeSection;
    readonly InheritRow _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _clipboardRow, _sshAgentRow, _sshLegacyRow, _jumpHostRow, _autoReconnectRow, _desktopSizeRow, _enlargeRow, _qualityRow;
    readonly Notice _error = new(NoticeKind.Error) { IsVisible = false };
    readonly Button _connect, _save;
    readonly Control _editorView;
    readonly TextBlock _empty = Pane.EmptyText("Select a connection or folder to see its settings.");
    readonly Func<bool> _enlargeByDefault;
    readonly ConnectionHistory? _history;
    readonly Func<DateTimeOffset> _now;
    readonly TextBlock _lastConnected = new() { FontSize = 12, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _lastFailed = new() { FontSize = 12, Foreground = Dialogs.DangerBrush, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
    readonly DispatcherTimer _historyTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    ConnectionNode? _node;
    bool _loading;
    bool _dirty;
    int _showVersion;

    /// <param name="enlargeByDefault">The app's "Enlarge remote desktops" setting, shown where nothing in the tree sets it.</param>
    /// <param name="history">When connections last connected, shown under the name.</param>
    /// <param name="now">The clock, for wording like "3 days ago".</param>
    public PropertiesPanel(ConnectionTree tree, ICredentialStore credentials, Func<bool>? enlargeByDefault = null, ConnectionHistory? history = null, Func<DateTimeOffset>? now = null)
    {
        _tree = tree;
        _enlargeByDefault = enlargeByDefault ?? (() => true);
        _history = history;
        _now = now ?? (() => DateTimeOffset.Now);
        _historyTimer.Tick += (_, _) => ShowHistory();
        _lastConnected.IsVisible = history is not null;
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
        _desktopSizeRow.Tip("RDP: leave it empty to fit the window and follow it as it resizes, or enter a fixed size such as 1920x1080, which is scaled to fit.");
        _enlargeRow = new InheritRow("Enlarge to match display scaling", _enlarge, () => ShowInherited(r => _enlarge.IsChecked = r.Enlarge(_enlargeByDefault())), inline: true);
        _enlargeRow.Tip("RDP and VNC: when on, the remote desktop is enlarged on displays scaled to 125% or more. When off, it's shown pixel for pixel. Applies to new connections.");
        foreach (var (quality, label) in QualityLabels)
        {
            _quality.Items.Add(new ComboBoxItem { Content = label, Tag = quality });
        }

        _qualityRow = new InheritRow("Display quality", _quality, () => ShowInherited(r => SelectQuality(r.DisplayQuality.Value)));
        _qualityRow.Tip("RDP: Best adds desktop composition, window contents while dragging and animations; Fastest drops wallpaper, "
            + "themes and font smoothing. VNC: Fastest asks for lower-quality JPEG, used when the server sends Tight. Applies to new connections.");
        _autoReconnectRow = new InheritRow("Reconnect if the connection drops", _autoReconnect, () => ShowInherited(r => _autoReconnect.IsChecked = r.AutoReconnect.Value), inline: true);
        _autoReconnectRow.Tip("If a session drops or the network fails, Orrery tries again by itself, sooner when the network comes back. It doesn't after a refused login, an untrusted host key or certificate, a logoff or Disconnect.");
        _sshAgentRow = new InheritRow("Use SSH agent keys", _sshAgent, () => ShowInherited(r => _sshAgent.IsChecked = r.UseSshAgent.Value), inline: true);
        _sshAgentRow.Tip("SSH: log in with keys from Pageant or the OpenSSH agent when there is no saved credential.");
        _sshLegacyRow = new InheritRow("Allow older, weaker algorithms", _sshLegacy, () => ShowInherited(r => _sshLegacy.IsChecked = r.AllowLegacySsh.Value), inline: true);
        _sshLegacyRow.Tip("SSH: also accept SHA-1, CBC and 3DES algorithms.");
        var legacyHint = new TextBlock { Text = "Only for old devices that can't connect otherwise", FontSize = 11, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };
        _jumpHostRow = new InheritRow("Jump host", _jumpHost, () => ShowInherited(r => SelectJumpHost(r.JumpHostId.Value)));
        _jumpHostRow.Tip("Reach this through a saved SSH connection first, like ssh -J. RDP, VNC and Telnet go through an SSH tunnel to it.");
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

        AutomationProperties.SetName(_forwards, "Port forwards");
        AutomationProperties.SetHelpText(_forwards, "One per line. L port:host:port opens a port here, R port:host:port one on the server, D port a SOCKS proxy.");
        AutomationProperties.SetName(_notes, "Notes");
        _wakeSection = Pane.Section("Wake on LAN",
            Dialogs.Field("MAC address", _mac),
            Dialogs.Field("Broadcast address (optional)", _broadcast),
            new TextBlock
            {
                Text = "Wake in the connection's right-click menu sends a wake-up packet to this address on UDP port 9. "
                    + "Leave the broadcast address empty to use 255.255.255.255; if the machine doesn't wake, try your network's, such as 192.168.1.255.",
                FontSize = 11,
                Foreground = Dialogs.MutedBrush,
                TextWrapping = TextWrapping.Wrap,
            });

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
                Pane.Header(_tile, _title, _path, _lastConnected, _lastFailed),
                Pane.Section("Connection", _nameField, _hostField, Pane.Pair(_protocolRow.View, _portRow.View)),
                Pane.Section("Credentials", _credentialRow.View, _usernameRow.View, _domainRow.View),
                Pane.Section("Options", _desktopSizeRow.View, _enlargeRow.View, _qualityRow.View, _clipboardRow.View, _autoReconnectRow.View, _sshAgentRow.View, new StackPanel { Spacing = 2, Children = { _sshLegacyRow.View, legacyHint } }),
                Pane.Section("SSH tunnels", _jumpHostRow.View, _forwardsField),
                _wakeSection,
                _notesField,
            },
        };

        _editorView = Pane.WithFooter(editor, new StackPanel
        {
            Spacing = 8,
            Children = { _error, _connect, Pane.Pair(_save, revert) },
        });

        foreach (var box in new[] { _name, _host, _port, _username, _domain, _notes, _forwards, _desktopSize, _mac, _broadcast })
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
        _enlarge.IsCheckedChanged += (_, _) => MarkDirty();
        _quality.SelectionChanged += (_, _) => MarkDirty();
        foreach (var row in new[] { _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _clipboardRow, _sshAgentRow, _sshLegacyRow, _jumpHostRow, _autoReconnectRow, _desktopSizeRow, _enlargeRow, _qualityRow })
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
            _path.Text = defaults ? "Used by every folder and connection that doesn't set its own. Inherit means Orrery's default." : Location(node);
            _path.TextWrapping = defaults ? TextWrapping.Wrap : TextWrapping.NoWrap;
            _nameField.IsVisible = _notesField.IsVisible = !defaults;
            _name.Text = node.Name;
            _hostField.IsVisible = _connect.IsVisible = node is ConnectionEntry;
            _save.Classes.Set("accent", node is not ConnectionEntry);
            ShowTile(node, own.Protocol ?? inherited.Protocol.Value);
            ShowHistory();
            _host.Text = (node as ConnectionEntry)?.Host;
            _notes.Text = node.Notes;

            _protocolRow.Load(own.Protocol is null, Describe(inherited.Protocol));
            SelectProtocol(own.Protocol ?? inherited.Protocol.Value);

            _portRow.Load(own.Port is null, Describe(inherited.Port));
            _port.Text = (own.Port ?? inherited.Port.Value).ToString();

            _credentialRow.Load(own.CredentialId is null, Describe(inherited.CredentialId));
            await _credential.RefreshAsync(ShownCredential(own, inherited));
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

            _enlargeRow.Load(own.EnlargeRemoteDesktop is null, inherited.EnlargeRemoteDesktop.IsDefault ? "From Settings" : Describe(inherited.EnlargeRemoteDesktop));
            _enlarge.IsChecked = own.EnlargeRemoteDesktop ?? inherited.Enlarge(_enlargeByDefault());

            _qualityRow.Load(own.DisplayQuality is null, Describe(inherited.DisplayQuality));
            SelectQuality(own.DisplayQuality ?? inherited.DisplayQuality.Value);

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

            _wakeSection.IsVisible = node is ConnectionEntry;
            _mac.Text = (node as ConnectionEntry)?.MacAddress;
            _broadcast.Text = (node as ConnectionEntry)?.WakeBroadcast;

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

    static Guid? ShownCredential(InheritableSettings own, ResolvedSettings inherited) => own.CredentialId switch
    {
        null => inherited.CredentialId.Value,
        var id when id == InheritableSettings.NoCredential => null,
        var id => id,
    };

    /// <summary>
    /// Catches up with a credential saved, or chosen for <paramref name="changed"/>, outside the panel (while
    /// connecting): the credential row shows the node's choice and the list has the new one. Other edits not
    /// yet applied are kept. Without this, applying them would put the old credential back.
    /// </summary>
    public async Task CredentialsChangedAsync(ConnectionNode changed)
    {
        if (_node is null || Content != _editorView)
        {
            return;
        }

        int version = _showVersion;
        bool dirty = _dirty;
        _loading = true;
        try
        {
            if (_node == changed)
            {
                var inherited = SettingsResolver.ResolveInherited(_node);
                _credentialRow.Load(_node.Settings.CredentialId is null, Describe(inherited.CredentialId));
                await _credential.RefreshAsync(ShownCredential(_node.Settings, inherited));
            }
            else
            {
                await _credential.RefreshAsync(_credential.SelectedId);
            }
        }
        finally
        {
            // Another node shown meanwhile owns the panel's state now.
            if (version == _showVersion)
            {
                _loading = false;
                _dirty = dirty;
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

        string? mac = null, broadcast = null;
        if (_node is ConnectionEntry)
        {
            if (!string.IsNullOrWhiteSpace(_mac.Text))
            {
                if (!MacAddress.TryNormalise(_mac.Text, out mac))
                {
                    return Fail("The MAC address needs six pairs of hex digits, such as 00:1A:2B:3C:4D:5E or 00-1A-2B-3C-4D-5E.");
                }
            }

            if (!string.IsNullOrWhiteSpace(_broadcast.Text))
            {
                if (!WakeOnLan.TryParseBroadcast(_broadcast.Text, out var address))
                {
                    return Fail("The broadcast address must be an IPv4 address such as 192.168.1.255.");
                }

                broadcast = address.Equals(System.Net.IPAddress.Broadcast) ? null : address.ToString();
            }
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
            EnlargeRemoteDesktop = _enlargeRow.Inherit.IsChecked == true ? null : _enlarge.IsChecked == true,
            DisplayQuality = _qualityRow.Inherit.IsChecked == true ? null : (_quality.SelectedItem as ComboBoxItem)?.Tag as DisplayQuality?,
            AutoReconnect = _autoReconnectRow.Inherit.IsChecked == true ? null : _autoReconnect.IsChecked == true,
            JumpHostId = _jumpHostRow.Inherit.IsChecked == true ? null : (_jumpHost.SelectedItem as ComboBoxItem)?.Tag as Guid? ?? InheritableSettings.NoJumpHost,
        };
        _node.Name = name;
        _node.Notes = string.IsNullOrWhiteSpace(_notes.Text) ? null : _notes.Text.Trim();
        if (_node is ConnectionEntry entry)
        {
            entry.Host = host;
            entry.PortForwards = forwards;
            entry.MacAddress = mac;
            entry.WakeBroadcast = broadcast;
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

        // Shown under the fields, away from the focus, so it's also read out.
        ScreenReader.Announce(this, message, "Orrery.Properties", ScreenReader.MostRecent);
        return false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_history is not null)
        {
            _history.Changed += OnHistoryChanged;
            _historyTimer.Start();
            ShowHistory();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_history is not null)
        {
            _history.Changed -= OnHistoryChanged;
            _historyTimer.Stop();
        }
    }

    void OnHistoryChanged(object? sender, EventArgs e) => ShowHistory();

    /// <summary>
    /// "Last connected 3 days ago" (the exact time in the tooltip) or "Never connected", and under it, only while
    /// it's more recent than the last success, "Last attempt failed 2 hours ago". A folder shows the latest of
    /// its connections. Runs again every so often so the wording keeps up.
    /// </summary>
    public void ShowHistory()
    {
        if (_node is null || _history is null)
        {
            return;
        }

        var use = _history.Summary(_node);
        var now = _now();
        _lastConnected.Text = use.Connected is { } connected ? $"Last connected {RelativeTime.Describe(connected, now)}" : "Never connected";
        ToolTip.SetTip(_lastConnected, use.Connected is { } at ? RelativeTime.Exact(at) : null);
        if (use.FailedSince is { } failed)
        {
            _lastFailed.Text = $"Last attempt failed {RelativeTime.Describe(failed, now)}";
            ToolTip.SetTip(_lastFailed, RelativeTime.Exact(failed));
        }

        _lastFailed.IsVisible = use.FailedSince is not null;
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

    static readonly (DisplayQuality Quality, string Label)[] QualityLabels =
    [
        (DisplayQuality.Balanced, "Balanced"),
        (DisplayQuality.Best, "Best look"),
        (DisplayQuality.Fastest, "Fastest, for slow links"),
    ];

    void SelectQuality(DisplayQuality quality) =>
        _quality.SelectedItem = _quality.Items.OfType<ComboBoxItem>().First(i => (DisplayQuality)i.Tag! == quality);

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

    /// <summary>The line under the name: the folder, with "Guest of" for a guest and the guest count for a host.</summary>
    static string Location(ConnectionNode node)
    {
        var folder = node.Parent is { } parent && parent.Parent is not null
            ? (parent.Path is { Length: > 0 } above ? $"{above} / {parent.Name}" : parent.Name)
            : "Top level";
        return node switch
        {
            ConnectionEntry { GuestOf: { } host } => $"Guest of {host.Name} · {folder}",
            ConnectionEntry { IsHost: true } entry => $"{folder} · {entry.Guests.Count} guest{(entry.Guests.Count == 1 ? "" : "s")}",
            _ => folder,
        };
    }

    static string Describe<T>(Resolved<T> resolved) =>
        resolved.Source is { } source ? source.Parent is null ? "From defaults for all connections" : $"From {source.Name}" : "Orrery's default";

    /// <summary>
    /// A label with an "Inherit" chip, the editor, and where the inherited value comes from. Inline rows
    /// (switches) put the editor on the label's line.
    /// </summary>
    sealed class InheritRow
    {
        const string InheritTip = "On: use the value from the folder above. Off: set this one's own.";

        readonly TextBlock _hint = new() { FontSize = 11, Foreground = Dialogs.MutedBrush };
        readonly Control _editor;

        public InheritRow(string label, Control editor, Action showInherited, bool inline = false)
        {
            _editor = editor;

            // The label is beside the editor only on screen; a screen reader needs it on the editor and the chip.
            AutomationProperties.SetName(editor, label);
            AutomationProperties.SetName(Inherit, $"{label}: inherit");
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
            ToolTip.SetTip(Inherit, InheritTip);

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
            AutomationProperties.SetHelpText(Inherit, $"{hint}. {InheritTip}");
        }

        /// <summary>Explains the setting: a tooltip over the row, and the editor's description for a screen reader.</summary>
        public void Tip(string text)
        {
            ToolTip.SetTip(View, text);
            AutomationProperties.SetHelpText(_editor, text);
        }
    }
}
