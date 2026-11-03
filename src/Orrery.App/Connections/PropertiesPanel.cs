using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Orrery.App.Controls;
using Orrery.App.Credentials;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Network;
using Orrery.Core.Sessions;
using Orrery.Protocols.Serial;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Orrery.App.Connections;

/// <summary>
/// Edits the selected connection or folder. Each inheritable setting has an "Inherit" chip: on, the
/// field shows what the parent provides and stores nothing; off, the node's own value overrides it.
/// Edits are applied with Save, or automatically when another node is selected.
/// <para>
/// A connection shows the sections for the protocol picked (RDP, VNC, SSH, serial); a folder and the defaults show
/// them all, since what they set reaches connections of every protocol. Hidden fields keep their values.
/// </para>
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
    readonly TextBox _keyFile = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = "~/.ssh/id_ed25519" };
    readonly ToggleSwitch _clipboard = Switch("ShareClipboard");
    readonly ToggleSwitch _sshAgent = Switch("UseSshAgent");
    readonly ToggleSwitch _sshLegacy = Switch("AllowLegacySsh");
    readonly ToggleSwitch _autoReconnect = Switch("AutoReconnect");
    readonly ToggleSwitch _record = Switch("RecordSessions");
    readonly ToggleSwitch _enlarge = Switch("EnlargeRemoteDesktop");
    readonly TextBox _desktopSize = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = "Fit the window" };
    readonly ComboBox _jumpHost = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox _quality = new() { Name = "DisplayQuality", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox _colorCorrection = new() { Name = "ColorCorrection", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _forwards = new() { AcceptsReturn = true, MinHeight = 56, FontFamily = Dialogs.MonoFont, PlaceholderText = "L 8080:db.internal:5432\nD 1080" };
    readonly Control _forwardsField;
    readonly TextBox _mac = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = "00:1A:2B:3C:4D:5E" };
    readonly TextBox _broadcast = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = "255.255.255.255" };
    readonly TextBox _managementUrl = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = "https://10.20.0.1:8443" };
    readonly Control _managementUrlField;
    readonly Control _wakeSection;

    readonly ComboBox _remoteAudio = Choices("RemoteAudio", (RemoteAudio.PlayHere, "Play on this computer"), (RemoteAudio.DoNotPlay, "Don't play"));
    readonly ToggleSwitch _shareAllDrives = Switch("ShareAllDrives");
    readonly TextBox _sharedFolders = new() { AcceptsReturn = true, MinHeight = 56, FontFamily = Dialogs.MonoFont, PlaceholderText = OperatingSystem.IsWindows() ? "~/Documents\nD:\\Projects" : "~/Documents\n/Volumes/Projects" };
    readonly ComboBox _fullScreenOn = Choices("FullScreenOn", (FullScreenOn.ThisDisplay, "The display the window is on"), (FullScreenOn.EveryDisplay, "Every display"),
        (FullScreenOn.ChosenDisplays, "Chosen displays"));
    readonly DisplayPicker _displayPicker;
    readonly ToggleSwitch _adminSession = Switch("AdminSession");
    readonly ToggleSwitch _viewOnly = Switch("ViewOnly");
    readonly ComboBox _scaling = Choices("Scaling", SessionSettingsView.ScalingLabels);
    readonly TextBox _keepAlive = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = SettingsResolver.DefaultKeepAliveSeconds.ToString(CultureInfo.InvariantCulture) };
    readonly TextBox _socksProxy = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = "None" };
    readonly TextBox _baudRate = new() { FontFamily = Dialogs.MonoFont, PlaceholderText = SettingsResolver.DefaultBaudRate.ToString(CultureInfo.InvariantCulture) };
    readonly ComboBox _dataBits = Choices("DataBits", (8, "8"), (7, "7"), (6, "6"), (5, "5"));
    readonly ComboBox _parity = Choices("Parity", (SerialParity.None, "None"), (SerialParity.Even, "Even"), (SerialParity.Odd, "Odd"), (SerialParity.Mark, "Mark"), (SerialParity.Space, "Space"));
    readonly ComboBox _stopBits = Choices("StopBits", (SerialStopBits.One, "1"), (SerialStopBits.OnePointFive, "1.5"), (SerialStopBits.Two, "2"));
    readonly ComboBox _flowControl = Choices("FlowControl", (SerialFlowControl.None, "None"), (SerialFlowControl.XOnXOff, "XON/XOFF (software)"), (SerialFlowControl.RtsCts, "RTS/CTS (hardware)"));
    readonly TextBlock _hostLabel = Dialogs.Label("Host");
    readonly Button _choosePort = new() { Content = "Ports…", Margin = new Thickness(6, 0, 0, 0), MinWidth = 0, IsVisible = false };
    readonly Control _credentialsSection, _sessionSection, _displaySection, _rdpSection, _vncSection, _sshSection, _serialSection, _tunnelSection;

    readonly InheritRow _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _keyFileRow, _clipboardRow, _sshAgentRow, _sshLegacyRow, _jumpHostRow, _autoReconnectRow, _recordRow, _desktopSizeRow, _enlargeRow, _qualityRow, _colorCorrectionRow;
    readonly InheritRow _remoteAudioRow, _shareAllDrivesRow, _sharedFoldersRow, _fullScreenRow, _adminSessionRow, _viewOnlyRow, _scalingRow,
        _keepAliveRow, _socksProxyRow, _baudRateRow, _dataBitsRow, _parityRow, _stopBitsRow, _flowControlRow;
    readonly Notice _error = new(NoticeKind.Error) { IsVisible = false };
    readonly Button _connect, _save;
    readonly List<InheritRow> _rows = [];
    readonly TextBlock _setHere = new() { FontSize = 11, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Control _editorView, _root;
    readonly ContentControl _body = new();
    readonly Control _tabStrip;
    readonly Button _savedTab = new() { Content = "Saved settings", Classes = { "segment" } };
    readonly Button _sessionTab = new() { Classes = { "segment" } };
    readonly Ellipse _sessionDot = new() { Width = 7, Height = 7, Fill = Palette.StateUp, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _sessionCount = new() { FontSize = 10, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#F0B455")) };
    readonly Border _sessionBadge;
    SessionTab? _liveTab;
    SessionSettingsView? _sessionView;
    (SessionTab Tab, ConnectionNode Node)? _sessionFor;
    bool _showingSession;
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
    /// <param name="displays">This computer's displays, for choosing them for full screen; by default the window's screens.</param>
    public PropertiesPanel(ConnectionTree tree, ICredentialStore credentials, Func<bool>? enlargeByDefault = null, ConnectionHistory? history = null, Func<DateTimeOffset>? now = null,
        Func<IReadOnlyList<LocalDisplay>>? displays = null)
    {
        _displayPicker = new DisplayPicker(displays) { IsVisible = false };
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
        _portRow = new InheritRow("Port", _port, () => ShowInherited(r => _port.Text = InheritedPort(r, ShownProtocol(r)).ToString()));
        _credentialRow = new InheritRow("Saved credential", _credential, () => ShowInherited(r => _ = _credential.RefreshAsync(r.CredentialId.Value)), watchEditor: false);
        _usernameRow = new InheritRow("Username (when no saved credential)", _username, () => ShowInherited(r => _username.Text = r.Username.Value));
        _domainRow = new InheritRow("Domain", _domain, () => ShowInherited(r => _domain.Text = r.Domain.Value));
        var browseKey = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), MinWidth = 0 };
        browseKey.Click += async (_, _) => await BrowseKeyFileAsync();
        AutomationProperties.SetName(browseKey, "Browse for a key file");
        AutomationProperties.SetName(_keyFile, "Key file");
        _keyFileRow = new InheritRow("Key file (SSH)", new DockPanel { Children = { Pane.Docked(browseKey, Dock.Right), _keyFile } },
            () => ShowInherited(r => _keyFile.Text = r.KeyFile.Value));
        _keyFileRow.Tip("SSH: a private key file on this computer, tried after a saved key credential and before the SSH agent and the password. "
            + "Orrery reads it when you connect and never copies it. ~ means your home folder.");
        AutomationProperties.SetHelpText(_keyFile, "A private key file on this computer, tried after a saved key credential and before the SSH agent and the password. ~ means your home folder.");
        _clipboardRow = new InheritRow("Share the clipboard", _clipboard, () => ShowInherited(r => _clipboard.IsChecked = r.ShareClipboard.Value));
        _desktopSizeRow = new InheritRow("Remote desktop size", _desktopSize, () => ShowInherited(r => _desktopSize.Text = r.DesktopSize.Value), whenEmpty: "Fit the window");
        _desktopSizeRow.Tip("RDP: leave it empty to fit the window and follow it as it resizes, or enter a fixed size such as 1920x1080, which is scaled to fit.");
        _enlargeRow = new InheritRow("Enlarge to match display scaling", _enlarge, () => ShowInherited(r => _enlarge.IsChecked = r.Enlarge(_enlargeByDefault())));
        _enlargeRow.Tip("RDP: when on, the server is told this display's scaling, so the remote desktop is as sharp as this display and its text the size of "
            + "local apps' text (a fixed desktop size is enlarged instead). VNC: when on, the remote desktop is enlarged on displays scaled to 125% or more. "
            + "When off, each of its pixels is one of this display's, at 100%. Applies to new connections.");
        foreach (var (quality, label) in QualityLabels)
        {
            _quality.Items.Add(new ComboBoxItem { Content = label, Tag = quality });
        }

        _qualityRow = new InheritRow("Display quality", _quality, () => ShowInherited(r => SelectQuality(r.DisplayQuality.Value)));
        _qualityRow.Tip("RDP: Best adds desktop composition, window contents while dragging and animations; Fastest drops wallpaper, "
            + "themes and font smoothing. VNC: Fastest asks for lower-quality JPEG, used when the server sends Tight. Applies to new connections.");
        _colorCorrection.Items.Add(new ComboBoxItem { Content = "Use app setting", Tag = true });
        _colorCorrection.Items.Add(new ComboBoxItem { Content = "Off", Tag = false });
        _colorCorrectionRow = new InheritRow("Color correction", _colorCorrection, () => ShowInherited(r => SelectColorCorrection(r.ColorCorrection.Value)));
        _colorCorrectionRow.Tip("RDP and VNC: Use app setting corrects the remote desktop's colors for the color vision chosen in Settings, if any. "
            + "Off shows its colors unchanged, for design or photo work. Applies to new connections.");
        _autoReconnectRow = new InheritRow("Reconnect if the connection drops", _autoReconnect, () => ShowInherited(r => _autoReconnect.IsChecked = r.AutoReconnect.Value));
        _autoReconnectRow.Tip("If a session drops or the network fails, Orrery tries again by itself, sooner when the network comes back. It doesn't after a refused login, an untrusted host key or certificate, a logoff or Disconnect.");
        _recordRow = new InheritRow("Record sessions", _record, () => ShowInherited(r => _record.IsChecked = r.RecordSessions.Value));
        _recordRow.Tip("Records each session from when it connects: what a terminal printed (never what's typed), or what a remote desktop showed. "
            + "Recordings are encrypted and play in Orrery; the remote side isn't told. A tab's menu can also start or stop one.");
        _sshAgentRow = new InheritRow("Use SSH agent keys", _sshAgent, () => ShowInherited(r => _sshAgent.IsChecked = r.UseSshAgent.Value));
        _sshAgentRow.Tip("SSH: log in with keys from Pageant or the OpenSSH agent when there is no saved credential.");
        _sshLegacyRow = new InheritRow("Allow older, weaker algorithms", _sshLegacy, () => ShowInherited(r => _sshLegacy.IsChecked = r.AllowLegacySsh.Value));
        _sshLegacyRow.Tip("SSH: also accept SHA-1, CBC and 3DES algorithms.");
        var legacyHint = new TextBlock { Text = "Only for old devices that can't connect otherwise", FontSize = 11, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, -4, 0, 4) };
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

        _remoteAudioRow = new InheritRow("Remote audio", _remoteAudio, () => ShowInherited(r => Choose(_remoteAudio, r.RemoteAudio.Value)));
        _remoteAudioRow.Tip("RDP: play the remote computer's sound here, or not at all. Applies to new connections.");
        _shareAllDrivesRow = new InheritRow("Share every drive", _shareAllDrives, () => ShowInherited(r => _shareAllDrives.IsChecked = r.ShareAllDrives.Value));
        _shareAllDrivesRow.Tip("RDP: every drive on this computer shows on the remote one, under This PC, and the remote computer can read and change their files. "
            + "Linux and macOS can share only one folder, so there it's your home folder.");
        var browseFolder = new Button { Content = "Add folder…", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Left };
        browseFolder.Click += async (_, _) => await AddSharedFolderAsync();
        AutomationProperties.SetName(browseFolder, "Add a shared folder");
        AutomationProperties.SetName(_sharedFolders, "Shared folders");
        _sharedFoldersRow = new InheritRow("Shared folders", new StackPanel { Spacing = 6, Children = { _sharedFolders, browseFolder } },
            () => ShowInherited(r => _sharedFolders.Text = string.Join("\n", r.SharedFolders.Value)));
        _sharedFoldersRow.Tip("RDP: folders on this computer to show on the remote one as drives, one per line; ~ means your home folder. "
            + "The remote computer can read and change their files. Leave it empty to share none. Linux and macOS share only the first.");
        _fullScreenRow = new InheritRow("Full screen on", new StackPanel { Spacing = 8, Children = { _fullScreenOn, _displayPicker } },
            () => ShowInherited(r => ShowFullScreen(r.FullScreen.Value)), describe: DescribeFullScreen);
        _fullScreenRow.Tip("RDP and VNC: the displays full screen fills. Every display, or several chosen ones, make one remote desktop with a "
            + "monitor for each (VNC fits its picture across them); chosen displays with others between sit side by side on the remote computer. "
            + "Several displays work on Windows only; elsewhere full screen fills one, the first chosen.");
        _fullScreenOn.SelectionChanged += (_, _) => _displayPicker.IsVisible = Chosen<FullScreenOn>(_fullScreenOn) == FullScreenOn.ChosenDisplays;
        _displayPicker.Changed += (_, _) =>
        {
            _fullScreenRow.Edited();
            MarkDirty();
        };
        _adminSessionRow = new InheritRow("Connect to the console session", _adminSession, () => ShowInherited(r => _adminSession.IsChecked = r.AdminSession.Value));
        _adminSessionRow.Tip("RDP: the server's console (administrative) session, as mstsc /admin, for administering a server without a Remote Desktop license.");
        _viewOnlyRow = new InheritRow("View only", _viewOnly, () => ShowInherited(r => _viewOnly.IsChecked = r.ViewOnly.Value));
        _viewOnlyRow.Tip("VNC: watch the remote desktop without sending keys, clicks or the clipboard. The session's More menu turns it off.");
        _scalingRow = new InheritRow("Scaling", _scaling, () => ShowInherited(r => Choose(_scaling, r.Scaling.Value)));
        _scalingRow.Tip("RDP and VNC, for a desktop that doesn't match the tab (a fixed desktop size, a server that won't resize, or a moment while one "
            + "resizes): Fit the tab shrinks or enlarges it to fill the tab; Shrink to fit makes a bigger one smaller and leaves a smaller one as it is; "
            + "Actual size shows it pixel for pixel, with scroll bars. Each keeps its shape.");
        _keepAliveRow = new InheritRow("Keep-alive (seconds)", _keepAlive, () => ShowInherited(r => _keepAlive.Text = r.KeepAliveSeconds.Value.ToString(CultureInfo.InvariantCulture)));
        _keepAliveRow.Tip("SSH: how often to send the server a keep-alive message, so an idle session isn't dropped by a firewall or the server. 0 sends none. "
            + "Also used for the connection as a jump host.");
        _socksProxyRow = new InheritRow("SOCKS proxy", _socksProxy, () => ShowInherited(r => _socksProxy.Text = r.SocksProxy.Value));
        _socksProxyRow.Tip("SSH: every SSH connection under this opens a SOCKS proxy on this port, like its own D port forward: a port, such as 1080, "
            + "or an address and a port, such as 127.0.0.1:1080. Only one open session can have the port; the others say so in their terminal.");
        _baudRateRow = new InheritRow("Speed (baud)", _baudRate, () => ShowInherited(r => _baudRate.Text = r.BaudRate.Value.ToString(CultureInfo.InvariantCulture)));
        _baudRateRow.Tip("Serial: bits per second, the same as the device's. Consoles mostly use 9600 or 115200.");
        _dataBitsRow = new InheritRow("Data bits", _dataBits, () => ShowInherited(r => Choose(_dataBits, r.DataBits.Value)));
        _parityRow = new InheritRow("Parity", _parity, () => ShowInherited(r => Choose(_parity, r.Parity.Value)));
        _stopBitsRow = new InheritRow("Stop bits", _stopBits, () => ShowInherited(r => Choose(_stopBits, r.StopBits.Value)));
        _flowControlRow = new InheritRow("Flow control", _flowControl, () => ShowInherited(r => Choose(_flowControl, r.FlowControl.Value)));
        _flowControlRow.Tip("Serial: how the two ends pause each other. Most consoles use none; use what the device's manual says.");
        _choosePort.Click += (_, _) => ShowSerialPorts();
        AutomationProperties.SetName(_choosePort, "Choose a serial port on this computer");
        ToolTip.SetTip(_choosePort, "The serial ports on this computer");

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

        AutomationProperties.SetName(_host, "Host");
        _hostField = new StackPanel { Spacing = 4, Children = { _hostLabel, new DockPanel { Children = { Pane.Docked(_choosePort, Dock.Right), _host } } } };
        _nameField = Dialogs.Field("Name", _name);
        _managementUrlField = Dialogs.Field("Management URL (optional)", _managementUrl);
        ToolTip.SetTip(_managementUrl, "A web page for managing this machine, such as a firewall's or a server's out-of-band console. "
            + "Open management page in the connection's right-click menu opens it in the browser. Set here only; folders don't pass it on.");
        _notesField = Pane.Section("Notes", _notes);

        _save = new Button { Content = "Save", Margin = new Thickness(8, 0, 0, 0) };
        _save.Click += (_, _) => TrySave();
        var revert = new Button { Content = "Revert" };
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
            Margin = new Thickness(16, 4, 16, 16),
            Children =
            {
                Pane.Section("Connection", _nameField, _hostField, Pane.Rows(_protocolRow.View, _portRow.View), _managementUrlField),
                (_credentialsSection = Pane.RowSection("Credentials", _credentialRow.View, _usernameRow.View, _domainRow.View, _keyFileRow.View)),
                (_sessionSection = Pane.RowSection("Session", _clipboardRow.View, _autoReconnectRow.View, _recordRow.View)),
                (_displaySection = Pane.RowSection("Display (RDP and VNC)", _desktopSizeRow.View, _enlargeRow.View, _scalingRow.View, _fullScreenRow.View, _qualityRow.View, _colorCorrectionRow.View)),
                (_rdpSection = Pane.RowSection("RDP", _remoteAudioRow.View, _shareAllDrivesRow.View, _sharedFoldersRow.View, _adminSessionRow.View)),
                (_vncSection = Pane.RowSection("VNC", _viewOnlyRow.View)),
                (_sshSection = Pane.RowSection("SSH", _sshAgentRow.View, _sshLegacyRow.View, legacyHint, _keepAliveRow.View, _socksProxyRow.View)),
                (_serialSection = Pane.RowSection("Serial line", _baudRateRow.View, _dataBitsRow.View, _parityRow.View, _stopBitsRow.View, _flowControlRow.View)),
                (_tunnelSection = Pane.RowSection("SSH tunnels", _jumpHostRow.View, _forwardsField)),
                _wakeSection,
                _notesField,
            },
        };

        _editorView = Pane.WithFooter(editor, new StackPanel
        {
            Spacing = 8,
            Children = { _error, _connect, new DockPanel { Children = { Pane.Docked(_save, Dock.Right), Pane.Docked(revert, Dock.Right), _setHere } } },
        });

        _sessionBadge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1), Background = new SolidColorBrush(Color.Parse("#4A3812")), Child = _sessionCount, VerticalAlignment = VerticalAlignment.Center };
        _sessionTab.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Children = { new TextBlock { Text = "This session", VerticalAlignment = VerticalAlignment.Center }, _sessionDot, _sessionBadge } };
        _savedTab.Click += (_, _) => ShowTab(session: false);
        _sessionTab.Click += (_, _) => ShowTab(session: true);
        Grid.SetColumn(_sessionTab, 1);
        _tabStrip = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0E1116")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(3),
            IsVisible = false,
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 3, Children = { _savedTab, _sessionTab } },
        };
        _root = new DockPanel
        {
            Children =
            {
                Pane.Docked(new StackPanel
                {
                    Spacing = 14,
                    Margin = new Thickness(16, 16, 16, 12),
                    Children = { Pane.Header(_tile, _title, _path, _lastConnected, _lastFailed), _tabStrip },
                }, Dock.Top),
                _body,
            },
        };

        foreach (var box in new[] { _name, _host, _port, _username, _domain, _keyFile, _notes, _forwards, _desktopSize, _mac, _broadcast, _managementUrl, _sharedFolders, _keepAlive, _socksProxy, _baudRate })
        {
            // TextChanged comes later, after the panel has finished filling the fields, so it took filling them for
            // an edit; the text itself is watched instead.
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    MarkDirty();
                }
            };
        }

        _protocol.SelectionChanged += (_, _) =>
        {
            MarkDirty();
            if (_node is ConnectionEntry entry && (_protocol.SelectedItem as ComboBoxItem)?.Tag is Protocol chosen)
            {
                ShowTile(entry, chosen);
                ShowSections(chosen);
            }

            // An inherited port that's only Orrery's default follows the protocol shown.
            if (!_loading && _node is not null && _portRow.Inheriting)
            {
                var inherited = SettingsResolver.ResolveInherited(_node);
                _portRow.Quietly(() => _port.Text = InheritedPort(inherited, ShownProtocol(inherited)).ToString());
            }
        };
        _credential.ChoiceChanged += (_, _) =>
        {
            _credentialRow.Edited();
            MarkDirty();
        };
        _clipboard.IsCheckedChanged += (_, _) => MarkDirty();
        _sshAgent.IsCheckedChanged += (_, _) => MarkDirty();
        _sshLegacy.IsCheckedChanged += (_, _) => MarkDirty();
        _jumpHost.SelectionChanged += (_, _) => MarkDirty();
        _autoReconnect.IsCheckedChanged += (_, _) => MarkDirty();
        _record.IsCheckedChanged += (_, _) => MarkDirty();
        _enlarge.IsCheckedChanged += (_, _) => MarkDirty();
        _quality.SelectionChanged += (_, _) => MarkDirty();
        _colorCorrection.SelectionChanged += (_, _) => MarkDirty();
        foreach (var box in new[] { _remoteAudio, _fullScreenOn, _scaling, _dataBits, _parity, _stopBits, _flowControl })
        {
            box.SelectionChanged += (_, _) => MarkDirty();
        }

        foreach (var toggle in new[] { _shareAllDrives, _adminSession, _viewOnly })
        {
            toggle.IsCheckedChanged += (_, _) => MarkDirty();
        }

        foreach (var row in new[]
                 {
                     _protocolRow, _portRow, _credentialRow, _usernameRow, _domainRow, _keyFileRow, _clipboardRow, _sshAgentRow, _sshLegacyRow, _jumpHostRow, _autoReconnectRow, _recordRow,
                     _desktopSizeRow, _enlargeRow, _qualityRow, _colorCorrectionRow, _remoteAudioRow, _shareAllDrivesRow, _sharedFoldersRow, _fullScreenRow,
                     _adminSessionRow, _viewOnlyRow, _scalingRow, _keepAliveRow, _socksProxyRow, _baudRateRow, _dataBitsRow, _parityRow, _stopBitsRow, _flowControlRow,
                 })
        {
            row.IsLoading = () => _loading;
            row.Changed += (_, _) => MarkDirty();
            _rows.Add(row);
        }

        Content = _empty;
    }

    public event EventHandler<ConnectionEntry>? ConnectRequested;

    public ConnectionNode? Node => _node;

    /// <summary>Raised when the open session's settings change it, so the session toolbar can say so.</summary>
    public event EventHandler? SessionSettingsApplied;

    /// <summary>Raised by the "This session" tab's Reconnect.</summary>
    public event EventHandler<SessionTab>? ReconnectRequested;

    /// <summary>
    /// The session tab in front, if any. While it's a remote desktop connected to the connection shown, the pane gets a
    /// "This session" tab beside the saved settings.
    /// </summary>
    public void ShowSession(SessionTab? tab)
    {
        _liveTab = tab;

        // A tab comes to the front as it opens, before anything can be changed, so this is what it started with.
        if (tab is { FixedAtStart: null, View: RemoteFrameView, Origin: { Files: false, EntryId: { } id } } && _tree.Find(id) is ConnectionEntry started)
        {
            tab.FixedAtStart = SessionSettingsView.FixedSettingsNow(started, tab.Origin.Protocol, _enlargeByDefault());
        }

        if (_node is not null && Content == _root)
        {
            UpdateTabs();
        }
    }

    void UpdateTabs()
    {
        bool live = _liveTab is { View: RemoteFrameView, Session.State: SessionState.Connected } tab
            && _node is ConnectionEntry entry && tab.Origin.EntryId == entry.Id && !tab.Origin.Files;
        _tabStrip.IsVisible = live;
        if (!live)
        {
            _sessionView = null;
            _sessionFor = null;
            _showingSession = false;
        }
        else if (_sessionFor is not { } shown || shown.Tab != _liveTab || shown.Node != _node)
        {
            var open = _liveTab!;
            var view = new SessionSettingsView(_tree, (ConnectionEntry)_node!, open.Origin.Protocol, (RemoteFrameView)open.View, _enlargeByDefault,
                open.FixedAtStart ?? SessionSettingsView.FixedSettingsNow((ConnectionEntry)_node!, open.Origin.Protocol, _enlargeByDefault()));
            view.Applied += (_, _) =>
            {
                ShowSessionBadge();
                SessionSettingsApplied?.Invoke(this, EventArgs.Empty);
            };
            // Edits not applied yet on the saved tab go in first, so applying them later doesn't undo what's kept.
            view.Keeping += (_, _) => TrySave();
            view.Kept += async (_, _) =>
            {
                // The saved tab shows what was kept.
                if (_node is not null)
                {
                    bool showing = _showingSession;
                    await ShowAsync(_node);
                    ShowTab(showing);
                }
            };
            view.ReconnectRequested += (_, _) => ReconnectRequested?.Invoke(this, open);
            _sessionView = view;
            _sessionFor = (open, _node!);
        }

        ShowTab(_showingSession && live);
    }

    void ShowTab(bool session)
    {
        _showingSession = session && _sessionView is not null;
        _body.Content = _showingSession ? _sessionView : _editorView;
        _savedTab.Classes.Set("selected", !_showingSession);
        _sessionTab.Classes.Set("selected", _showingSession);
        ShowSessionBadge();
    }

    /// <summary>A green dot for the connected session, or how many of its settings differ from the saved ones.</summary>
    void ShowSessionBadge()
    {
        int changes = _sessionView?.Changes ?? 0;
        _sessionDot.IsVisible = changes == 0;
        _sessionBadge.IsVisible = changes > 0;
        _sessionCount.Text = changes.ToString(CultureInfo.InvariantCulture);
        AutomationProperties.SetName(_sessionTab, changes switch { 0 => "This session", 1 => "This session, 1 change", _ => $"This session, {changes} changes" });
    }

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
            _port.Text = (own.Port ?? InheritedPort(inherited, own.Protocol ?? inherited.Protocol.Value)).ToString();

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

            _keyFileRow.Load(own.KeyFile is null, Describe(inherited.KeyFile));
            _keyFile.Text = own.KeyFile ?? inherited.KeyFile.Value;

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

            _colorCorrectionRow.Load(own.ColorCorrection is null, Describe(inherited.ColorCorrection));
            SelectColorCorrection(own.ColorCorrection ?? inherited.ColorCorrection.Value);

            _autoReconnectRow.Load(own.AutoReconnect is null, Describe(inherited.AutoReconnect));
            _autoReconnect.IsChecked = own.AutoReconnect ?? inherited.AutoReconnect.Value;

            _recordRow.Load(own.RecordSessions is null, Describe(inherited.RecordSessions));
            _record.IsChecked = own.RecordSessions ?? inherited.RecordSessions.Value;

            _remoteAudioRow.Load(own.RemoteAudio is null, Describe(inherited.RemoteAudio));
            Choose(_remoteAudio, own.RemoteAudio ?? inherited.RemoteAudio.Value);
            _shareAllDrivesRow.Load(own.ShareAllDrives is null, Describe(inherited.ShareAllDrives));
            _shareAllDrives.IsChecked = own.ShareAllDrives ?? inherited.ShareAllDrives.Value;
            _sharedFoldersRow.Load(own.SharedFolders is null, Describe(inherited.SharedFolders));
            _sharedFolders.Text = string.Join("\n", own.SharedFolders ?? inherited.SharedFolders.Value);
            _fullScreenRow.Load(own.ChosenDisplays is null && own.UseAllMonitors is null, Describe(inherited.FullScreen));
            ShowFullScreen(own.ChosenDisplays is { } chosen ? new FullScreenChoice(FullScreenOn.ChosenDisplays, chosen)
                : own.UseAllMonitors is { } every ? (every ? FullScreenChoice.EveryDisplay : FullScreenChoice.ThisDisplay)
                : inherited.FullScreen.Value);
            _adminSessionRow.Load(own.AdminSession is null, Describe(inherited.AdminSession));
            _adminSession.IsChecked = own.AdminSession ?? inherited.AdminSession.Value;
            _viewOnlyRow.Load(own.ViewOnly is null, Describe(inherited.ViewOnly));
            _viewOnly.IsChecked = own.ViewOnly ?? inherited.ViewOnly.Value;
            _scalingRow.Load(own.Scaling is null, Describe(inherited.Scaling));
            Choose(_scaling, own.Scaling ?? inherited.Scaling.Value);
            _keepAliveRow.Load(own.KeepAliveSeconds is null, Describe(inherited.KeepAliveSeconds));
            _keepAlive.Text = (own.KeepAliveSeconds ?? inherited.KeepAliveSeconds.Value).ToString(CultureInfo.InvariantCulture);
            _socksProxyRow.Load(own.SocksProxy is null, Describe(inherited.SocksProxy));
            _socksProxy.Text = own.SocksProxy ?? inherited.SocksProxy.Value;
            _baudRateRow.Load(own.BaudRate is null, Describe(inherited.BaudRate));
            _baudRate.Text = (own.BaudRate ?? inherited.BaudRate.Value).ToString(CultureInfo.InvariantCulture);
            _dataBitsRow.Load(own.DataBits is null, Describe(inherited.DataBits));
            Choose(_dataBits, own.DataBits ?? inherited.DataBits.Value);
            _parityRow.Load(own.Parity is null, Describe(inherited.Parity));
            Choose(_parity, own.Parity ?? inherited.Parity.Value);
            _stopBitsRow.Load(own.StopBits is null, Describe(inherited.StopBits));
            Choose(_stopBits, own.StopBits ?? inherited.StopBits.Value);
            _flowControlRow.Load(own.FlowControl is null, Describe(inherited.FlowControl));
            Choose(_flowControl, own.FlowControl ?? inherited.FlowControl.Value);

            FillJumpHosts(node);
            _jumpHostRow.Load(own.JumpHostId is null, Describe(inherited.JumpHostId));
            SelectJumpHost(own.JumpHostId switch
            {
                null => inherited.JumpHostId.Value,
                var id when id == InheritableSettings.NoJumpHost => null,
                var id => id,
            });

            _forwards.Text = PortForward.FormatAll((node as ConnectionEntry)?.PortForwards);

            _mac.Text = (node as ConnectionEntry)?.MacAddress;
            _broadcast.Text = (node as ConnectionEntry)?.WakeBroadcast;
            _managementUrl.Text = (node as ConnectionEntry)?.ManagementUrl;
            ShowSections(own.Protocol ?? inherited.Protocol.Value);

            Content = _root;
            CountSetHere();
            UpdateTabs();
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
        if (_node is null || Content != _root)
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
        if (!_portRow.Inheriting && _portRow.View.IsVisible)
        {
            if (!int.TryParse(_port.Text, out var p) || p is < 1 or > 65535)
            {
                return Fail("The port must be a number from 1 to 65535.");
            }

            port = p;
        }

        string? desktopSize = null;
        if (!_desktopSizeRow.Inheriting)
        {
            if (!DesktopResolution.TryParse(_desktopSize.Text, out var size, out var sizeError))
            {
                return Fail(sizeError!);
            }

            desktopSize = size is { } fixedSize ? $"{fixedSize.Width}x{fixedSize.Height}" : string.Empty;
        }

        string? keyFile = null;
        if (!_keyFileRow.Inheriting)
        {
            // Only a path, which can't be checked here: the file may be on another computer's disk, or not there yet.
            keyFile = _keyFile.Text?.Trim() ?? string.Empty;
            if (keyFile.Length > MaxKeyFilePathLength || keyFile.Any(char.IsControl))
            {
                return Fail("The key file path is too long or has characters a path can't have.");
            }
        }

        int? keepAlive = null;
        if (!_keepAliveRow.Inheriting)
        {
            if (!int.TryParse(_keepAlive.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds > MaxKeepAliveSeconds)
            {
                return Fail($"The keep-alive interval must be a number of seconds from 0 (none) to {MaxKeepAliveSeconds}.");
            }

            keepAlive = seconds;
        }

        string? socksProxy = null;
        if (!_socksProxyRow.Inheriting)
        {
            // Empty with Inherit off means none, overriding a folder's.
            socksProxy = _socksProxy.Text?.Trim() ?? string.Empty;
            if (socksProxy.Length > 0 && PortForward.TryParseSocks(socksProxy, out var socksError) is null)
            {
                return Fail(socksError!);
            }
        }

        int? baudRate = null;
        if (!_baudRateRow.Inheriting)
        {
            if (!int.TryParse(_baudRate.Text?.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var baud)
                || (SerialLine.Default with { BaudRate = baud }).Problem() is not null)
            {
                return Fail("The speed must be a number of bits per second from 50 to 4,000,000, such as 9600 or 115200.");
            }

            baudRate = baud;
        }

        List<string>? sharedFolders = null;
        if (!_sharedFoldersRow.Inheriting)
        {
            // Only paths, which can't be checked here: a folder may be on a drive that isn't plugged in now.
            sharedFolders = (_sharedFolders.Text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToList();
            if (sharedFolders.Any(f => f.Length > MaxKeyFilePathLength || f.Any(char.IsControl)))
            {
                return Fail("A shared folder's path is too long or has characters a path can't have.");
            }
        }

        List<ChosenDisplay>? chosenDisplays = null;
        if (!_fullScreenRow.Inheriting && Chosen<FullScreenOn>(_fullScreenOn) == FullScreenOn.ChosenDisplays)
        {
            chosenDisplays = _displayPicker.Chosen;
            if (chosenDisplays.Count == 0)
            {
                return Fail("Choose a display for full screen, or another choice under Full screen on.");
            }
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

        string? mac = null, broadcast = null, managementUrl = null;
        if (_node is ConnectionEntry)
        {
            if (!string.IsNullOrWhiteSpace(_managementUrl.Text) && !ManagementUrl.TryNormalize(_managementUrl.Text, out managementUrl))
            {
                return Fail("The management URL needs to be a web address, such as https://10.20.0.1:8443.");
            }

            if (!string.IsNullOrWhiteSpace(_mac.Text))
            {
                if (!MacAddress.TryNormalize(_mac.Text, out mac))
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
            Protocol = _protocolRow.Inheriting ? null : (_protocol.SelectedItem as ComboBoxItem)?.Tag as Protocol?,
            Port = port,
            CredentialId = _credentialRow.Inheriting ? null : _credential.SelectedId ?? InheritableSettings.NoCredential,
            Username = _usernameRow.Inheriting ? null : _username.Text?.Trim() ?? string.Empty,
            Domain = _domainRow.Inheriting ? null : _domain.Text?.Trim() ?? string.Empty,
            KeyFile = keyFile,
            ShareClipboard = _clipboardRow.Inheriting ? null : _clipboard.IsChecked == true,
            UseSshAgent = _sshAgentRow.Inheriting ? null : _sshAgent.IsChecked == true,
            AllowLegacySsh = _sshLegacyRow.Inheriting ? null : _sshLegacy.IsChecked == true,
            DesktopSize = desktopSize,
            EnlargeRemoteDesktop = _enlargeRow.Inheriting ? null : _enlarge.IsChecked == true,
            DisplayQuality = _qualityRow.Inheriting ? null : (_quality.SelectedItem as ComboBoxItem)?.Tag as DisplayQuality?,
            ColorCorrection = _colorCorrectionRow.Inheriting ? null : (_colorCorrection.SelectedItem as ComboBoxItem)?.Tag as bool?,
            AutoReconnect = _autoReconnectRow.Inheriting ? null : _autoReconnect.IsChecked == true,
            RecordSessions = _recordRow.Inheriting ? null : _record.IsChecked == true,
            JumpHostId = _jumpHostRow.Inheriting ? null : (_jumpHost.SelectedItem as ComboBoxItem)?.Tag as Guid? ?? InheritableSettings.NoJumpHost,
            RemoteAudio = _remoteAudioRow.Inheriting ? null : Chosen<RemoteAudio>(_remoteAudio),
            ShareAllDrives = _shareAllDrivesRow.Inheriting ? null : _shareAllDrives.IsChecked == true,
            SharedFolders = sharedFolders,
            UseAllMonitors = _fullScreenRow.Inheriting ? null : Chosen<FullScreenOn>(_fullScreenOn) switch
            {
                FullScreenOn.EveryDisplay => true,
                FullScreenOn.ThisDisplay => false,
                _ => null,
            },
            ChosenDisplays = chosenDisplays,
            AdminSession = _adminSessionRow.Inheriting ? null : _adminSession.IsChecked == true,
            ViewOnly = _viewOnlyRow.Inheriting ? null : _viewOnly.IsChecked == true,
            Scaling = _scalingRow.Inheriting ? null : Chosen<DesktopScaling>(_scaling),
            KeepAliveSeconds = keepAlive,
            SocksProxy = socksProxy,
            BaudRate = baudRate,
            DataBits = _dataBitsRow.Inheriting ? null : Chosen<int>(_dataBits),
            Parity = _parityRow.Inheriting ? null : Chosen<SerialParity>(_parity),
            StopBits = _stopBitsRow.Inheriting ? null : Chosen<SerialStopBits>(_stopBits),
            FlowControl = _flowControlRow.Inheriting ? null : Chosen<SerialFlowControl>(_flowControl),
        };
        _node.Name = name;
        _node.Notes = string.IsNullOrWhiteSpace(_notes.Text) ? null : _notes.Text.Trim();
        if (_node is ConnectionEntry entry)
        {
            entry.Host = host;
            entry.PortForwards = forwards;
            entry.MacAddress = mac;
            entry.WakeBroadcast = broadcast;
            entry.ManagementUrl = managementUrl;
        }

        _dirty = false;
        _error.IsVisible = false;
        _title.Text = name;
        _tree.NotifyChanged();
        return true;
    }

    const int MaxKeyFilePathLength = 1024;

    /// <summary>An hour: anything longer wouldn't keep a session from being dropped as idle.</summary>
    const int MaxKeepAliveSeconds = 3600;

    /// <summary>
    /// Shows the sections that apply to <paramref name="protocol"/>, the one picked. A folder and the defaults show every
    /// section, since what they set reaches connections of every protocol; a host also shows what it passes to its guests
    /// (the clipboard, reconnecting and the jump host).
    /// </summary>
    void ShowSections(Protocol protocol)
    {
        bool all = _node is not ConnectionEntry;
        bool host = _node is ConnectionEntry { IsHost: true };
        bool Is(Protocol p) => all || protocol == p;
        bool rdp = Is(Protocol.Rdp), vnc = Is(Protocol.Vnc), ssh = Is(Protocol.Ssh), serial = Is(Protocol.Serial);
        bool web = !all && ProtocolInfo.IsWeb(protocol), network = all || ProtocolInfo.UsesNetwork(protocol);

        _portRow.View.IsVisible = network;
        _credentialsSection.IsVisible = network && !web;
        _domainRow.View.IsVisible = rdp;
        _keyFileRow.View.IsVisible = ssh;
        _clipboardRow.View.IsVisible = rdp || vnc || host;
        _autoReconnectRow.View.IsVisible = !web || host;
        _recordRow.View.IsVisible = !web || host;
        _sessionSection.IsVisible = _clipboardRow.View.IsVisible || _autoReconnectRow.View.IsVisible || _recordRow.View.IsVisible;
        _displaySection.IsVisible = rdp || vnc;
        _desktopSizeRow.View.IsVisible = rdp;
        _rdpSection.IsVisible = rdp;
        _vncSection.IsVisible = vnc;
        _sshSection.IsVisible = ssh;
        _serialSection.IsVisible = serial;
        _jumpHostRow.View.IsVisible = network && !web || host;
        _forwardsField.IsVisible = _node is ConnectionEntry && protocol == Protocol.Ssh;
        _tunnelSection.IsVisible = _jumpHostRow.View.IsVisible || _forwardsField.IsVisible;
        _wakeSection.IsVisible = _node is ConnectionEntry && network;
        _managementUrlField.IsVisible = _node is ConnectionEntry;

        // A serial port is named where a host would be; a web page's host can have a path after it.
        _hostLabel.Text = protocol == Protocol.Serial ? "Serial port" : ProtocolInfo.IsWeb(protocol) ? "Host (and path)" : "Host";
        AutomationProperties.SetName(_host, _hostLabel.Text);
        _host.PlaceholderText = protocol == Protocol.Serial ? (OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyUSB0") : ProtocolInfo.IsWeb(protocol) ? "router.lan/admin" : null;
        _choosePort.IsVisible = protocol == Protocol.Serial && _node is ConnectionEntry;
    }

    /// <summary>The serial ports on this computer, to fill the host with.</summary>
    void ShowSerialPorts()
    {
        var ports = SerialSession.PortNames();
        List<Control> items = ports.Count == 0
            ? [new MenuItem { Header = "No serial ports found", IsEnabled = false }]
            : [.. ports.Select(name =>
            {
                var item = new MenuItem { Header = name };
                item.Click += (_, _) => _host.Text = name;
                return item;
            })];
        PopupMenu.ShowAt(_choosePort, items, PlacementMode.BottomEdgeAlignedRight);
    }

    /// <summary>Adds a folder from the folder picker to the shared folders, written with ~ when it's under the home folder.</summary>
    async Task AddSharedFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder to share", AllowMultiple = false });
        if (folders.Count == 0)
        {
            return;
        }

        if (folders[0].TryGetLocalPath() is not { } path)
        {
            Fail("That folder isn't on this computer, so it can't be shared.");
            return;
        }

        var text = _sharedFolders.Text?.TrimEnd() ?? string.Empty;
        _sharedFolders.Text = (text.Length > 0 ? text + "\n" : string.Empty) + WithHome(path.TrimEnd('/', '\\') is { Length: > 0 } trimmed ? trimmed : path);
    }

    /// <summary>
    /// Fills the key file box from the file picker. A file under the home folder is kept as ~/..., which also
    /// works on another computer after an export and import.
    /// </summary>
    async Task BrowseKeyFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var options = new FilePickerOpenOptions { Title = "Choose a private key file", AllowMultiple = false };
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(ssh))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(ssh);
        }

        var files = await storage.OpenFilePickerAsync(options);
        if (files.Count == 0)
        {
            return;
        }

        if (files[0].TryGetLocalPath() is not { } path)
        {
            Fail("That file isn't on this computer, so it can't be used as a key file.");
            return;
        }

        _keyFile.Text = WithHome(path);
    }

    /// <summary>The path with the home folder written as ~, when it's under it.</summary>
    internal static string WithHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0 && path.StartsWith(home.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return "~/" + path[(home.TrimEnd('/', '\\').Length + 1)..].Replace('\\', '/');
        }

        return path;
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
            CountSetHere();
        }
    }

    /// <summary>"4 set on this connection": the rows shown that don't inherit.</summary>
    void CountSetHere()
    {
        int count = _rows.Count(r => !r.Inheriting && r.View.IsEffectivelyVisible);
        string where = _node switch { ConnectionEntry => "on this connection", { Parent: null } => "in the defaults", _ => "on this folder" };
        _setHere.Text = count == 0 ? $"Nothing set {where}" : $"{count} set {where}";
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
    /// its connections. Runs again every so often so the wording keeps up. Neither shows while history is off.
    /// </summary>
    public void ShowHistory()
    {
        if (_node is null || _history is null)
        {
            return;
        }

        // With remembering turned off there's nothing to say, and "Never connected" would be wrong.
        _lastConnected.IsVisible = _history.Enabled;
        if (!_history.Enabled)
        {
            _lastFailed.IsVisible = false;
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

    /// <summary>The colored square beside the name: the protocol for a connection, a folder otherwise.</summary>
    void ShowTile(ConnectionNode node, Protocol protocol)
    {
        bool folder = node is not ConnectionEntry;
        _tile.Background = folder ? FolderTileBrush : ProtocolStyle.Background(protocol);
        _tileIcon.Foreground = folder ? FolderBrush : ProtocolStyle.Foreground(protocol);
        _tileIcon.Data = Geometry.Parse(folder ? Icons.Folder : ProtocolStyle.Icon(protocol));
    }

    static readonly IBrush FolderTileBrush = new SolidColorBrush(Color.Parse("#33291A")).ToImmutable();
    static readonly IBrush FolderBrush = new SolidColorBrush(Color.Parse("#E0B45A")).ToImmutable();

    /// <summary>A drop-down of values, each shown by its label.</summary>
    static ComboBox Choices<T>(string name, params (T Value, string Label)[] choices) where T : notnull
    {
        var box = new ComboBox { Name = name, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (value, label) in choices)
        {
            box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        return box;
    }

    static void Choose(ComboBox box, object value) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value));

    static T? Chosen<T>(ComboBox box) where T : struct => (box.SelectedItem as ComboBoxItem)?.Tag as T?;

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

    /// <summary>
    /// The port a node gets when it doesn't set one: an ancestor's, or else the default for the protocol it uses. The
    /// resolver works out that default from the ancestors' protocol, which is wrong when the node sets its own.
    /// </summary>
    static int InheritedPort(ResolvedSettings inherited, Protocol protocol) =>
        inherited.Port.IsDefault ? ProtocolInfo.DefaultPort(protocol) : inherited.Port.Value;

    /// <summary>The protocol picked in the panel, or the inherited one while nothing is picked.</summary>
    Protocol ShownProtocol(ResolvedSettings inherited) =>
        (_protocol.SelectedItem as ComboBoxItem)?.Tag is Protocol chosen ? chosen : inherited.Protocol.Value;

    void SelectProtocol(Protocol protocol) =>
        _protocol.SelectedItem = _protocol.Items.OfType<ComboBoxItem>().First(i => (Protocol)i.Tag! == protocol);

    internal static readonly (DisplayQuality Quality, string Label)[] QualityLabels =
    [
        (DisplayQuality.Balanced, "Balanced"),
        (DisplayQuality.Best, "Best look"),
        (DisplayQuality.Fastest, "Fastest, for slow links"),
    ];

    void SelectQuality(DisplayQuality quality) =>
        _quality.SelectedItem = _quality.Items.OfType<ComboBoxItem>().First(i => (DisplayQuality)i.Tag! == quality);

    void SelectColorCorrection(bool useAppSetting) =>
        _colorCorrection.SelectedItem = _colorCorrection.Items.OfType<ComboBoxItem>().First(i => (bool)i.Tag! == useAppSetting);

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

    /// <summary>Shows a full-screen choice: the option, and the chosen displays under it.</summary>
    void ShowFullScreen(FullScreenChoice choice)
    {
        Choose(_fullScreenOn, choice.On);
        _displayPicker.Chosen = [.. choice.Displays];
        _displayPicker.IsVisible = choice.On == FullScreenOn.ChosenDisplays;
    }

    /// <summary>The row's value: "This display", "Every display", or the chosen ones ("Displays 1 and 3").</summary>
    string DescribeFullScreen() => Chosen<FullScreenOn>(_fullScreenOn) switch
    {
        FullScreenOn.EveryDisplay => "Every display",
        FullScreenOn.ChosenDisplays => _displayPicker.Describe(),
        _ => "This display",
    };

    static string Describe<T>(Resolved<T> resolved) =>
        resolved.Source is { } source ? source.Parent is null ? "From defaults for all connections" : $"From {source.Name}" : "Orrery's default";

    /// <summary>
    /// One saved setting as a quiet row: a dot when it's set on this node, its name, and its value, muted while it comes
    /// from above. A click opens the editor under it, with where the inherited value comes from and, once it's set here,
    /// a way back to that. Changing the value is what sets it here; there's no separate switch for that.
    /// </summary>
    sealed class InheritRow
    {
        static readonly IBrush DotBrush = new SolidColorBrush(Color.Parse("#4C8DFF")).ToImmutable();
        static readonly IBrush LabelBrush = new SolidColorBrush(Color.Parse("#C3C9D3")).ToImmutable();
        static readonly IBrush OwnBrush = new SolidColorBrush(Color.Parse("#E6E9EE")).ToImmutable();
        static readonly IBrush InheritedBrush = new SolidColorBrush(Color.Parse("#7D8693")).ToImmutable();
        static readonly IBrush CardBrush = new SolidColorBrush(Color.Parse("#1A1F27")).ToImmutable();
        static readonly IBrush CardBorderBrush = new SolidColorBrush(Color.Parse("#2A3240")).ToImmutable();

        readonly string _label;
        readonly Control _editor;
        readonly Action _showInherited;
        readonly string _whenEmpty;
        readonly bool _canPreview;
        readonly Func<string>? _describe;
        readonly Ellipse _dot = new() { Width = 6, Height = 6, Fill = DotBrush, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        readonly TextBlock _value = new() { FontSize = 13, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _hint = new() { FontSize = 11, Foreground = InheritedBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        readonly Button _useInherited = new() { Content = "Use that instead", Classes = { "link" } };
        readonly Button _header;
        readonly Border _card;
        string _source = string.Empty, _inheritedValue = string.Empty;
        int _quiet;

        /// <param name="watchEditor">
        /// False where the editor changes itself (the credential picker, while it lists the vault): the panel calls
        /// <see cref="Edited"/> for the user's own changes instead.
        /// </param>
        /// <param name="whenEmpty">The value shown for an empty text box.</param>
        /// <param name="describe">The value to show, where the editor's own isn't it.</param>
        public InheritRow(string label, Control editor, Action showInherited, bool watchEditor = true, string whenEmpty = "None", Func<string>? describe = null)
        {
            _describe = describe;
            _label = label;
            _editor = editor;
            _showInherited = showInherited;
            _whenEmpty = whenEmpty;
            _canPreview = watchEditor;

            // The label is beside the editor only on screen; a screen reader needs it on the editor.
            AutomationProperties.SetName(editor, label);

            var name = new TextBlock { Text = label, FontSize = 13, Foreground = LabelBrush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(_value, 2);
            _header = new Button
            {
                Classes = { "setting" },
                Content = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*,*"), Children = { _dot, name, _value } },
            };
            _header.Click += (_, _) => Expanded = !Expanded;

            _useInherited.Click += (_, _) =>
            {
                Inheriting = true;
                Quietly(_showInherited);
                ShowState();
                Changed?.Invoke(this, EventArgs.Empty);
            };
            AutomationProperties.SetName(_useInherited, $"{label}: use the inherited value");

            _card = new Border
            {
                IsVisible = false,
                Background = CardBrush,
                BorderBrush = CardBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(-10, 2),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children = { editor, new DockPanel { Children = { Pane.Docked(_useInherited, Dock.Right), _hint } } },
                },
            };

            // Every change shows in the row; one made by the user, not by the panel, sets the value here.
            void OnEditorChanged(object? sender, EventArgs e)
            {
                ShowValue();

                // A display picker says itself when the user changes it, not when it's filled in.
                if (watchEditor && !((e as RoutedEventArgs)?.Source is StyledElement source && source.FindLogicalAncestorOfType<DisplayPicker>() is not null))
                {
                    Edited();
                }
            }

            // TextChanged comes later, after the panel has finished filling the fields, so the text itself is watched.
            foreach (var box in new[] { editor }.Concat(editor.GetLogicalDescendants()).OfType<TextBox>())
            {
                box.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                    {
                        OnEditorChanged(box, e);
                    }
                };
            }

            editor.AddHandler(SelectingItemsControl.SelectionChangedEvent, OnEditorChanged, handledEventsToo: true);
            editor.AddHandler(ToggleButton.IsCheckedChangedEvent, OnEditorChanged, handledEventsToo: true);

            View = new StackPanel { Children = { _header, _card } };
        }

        /// <summary>Raised when the row goes from inherited to set here or back.</summary>
        public event EventHandler? Changed;

        /// <summary>True while the panel fills its fields, so its changes aren't taken for the user's.</summary>
        public Func<bool> IsLoading { get; set; } = () => false;

        /// <summary>The value comes from above, and nothing is stored on this node.</summary>
        public bool Inheriting { get; private set; }

        public Control View { get; }

        public bool Expanded
        {
            get => _card.IsVisible;
            set => _card.IsVisible = value;
        }

        /// <summary>The user changed the value: it's set here from now on.</summary>
        public void Edited()
        {
            if (_quiet > 0 || IsLoading() || !Inheriting)
            {
                return;
            }

            Inheriting = false;
            ShowState();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Changes the editor without that counting as the user's change.</summary>
        public void Quietly(Action change)
        {
            _quiet++;
            try
            {
                change();
            }
            finally
            {
                _quiet--;
            }

            ShowValue();
        }

        /// <param name="source">Where the inherited value comes from, such as "From Lab servers".</param>
        public void Load(bool inheriting, string source)
        {
            Inheriting = inheriting;
            _source = source;

            // What the parent would give, for the line under the editor. The panel puts the node's own value in next.
            _inheritedValue = string.Empty;
            if (_canPreview)
            {
                Quietly(_showInherited);
                _inheritedValue = Summary();
            }

            ShowState();
        }

        /// <summary>Explains the setting: a tooltip over the row, and the editor's description for a screen reader.</summary>
        public void Tip(string text)
        {
            ToolTip.SetTip(View, text);
            AutomationProperties.SetHelpText(_editor, text);
        }

        void ShowState()
        {
            _dot.Opacity = Inheriting ? 0 : 1;
            _value.Foreground = Inheriting ? InheritedBrush : OwnBrush;
            _useInherited.IsVisible = !Inheriting;
            _hint.Text = Inheriting || _inheritedValue.Length == 0 ? _source : $"{_source}: {_inheritedValue}";
            ShowValue();
        }

        void ShowValue()
        {
            _value.Text = Summary();
            AutomationProperties.SetName(_header, $"{_label}: {_value.Text}{(Inheriting ? $", {_source}" : ", set here")}");
        }

        /// <summary>The editor's value in a few words: On or Off, the choice picked, or the text.</summary>
        string Summary()
        {
            if (_describe is not null)
            {
                return _describe();
            }

            var input = _editor is TextBox or ComboBox or ToggleSwitch
                ? _editor
                : _editor.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c is TextBox or ComboBox or ToggleSwitch);
            return input switch
            {
                ToggleSwitch toggle => toggle.IsChecked == true ? "On" : "Off",
                ComboBox box => (box.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty,
                TextBox box when string.IsNullOrWhiteSpace(box.Text) => _whenEmpty,
                TextBox box => string.Join(", ", box.Text!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                _ => string.Empty,
            };
        }
    }
}
