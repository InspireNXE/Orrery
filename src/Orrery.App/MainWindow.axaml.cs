using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Orrery.App.Connections;
using Orrery.App.Credentials;
using Orrery.App.Import;
using Orrery.App.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Orrery.Core.Network;
using Orrery.App.Security;
using Orrery.App.Sessions;
using Orrery.App.Settings;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Orrery.Protocols.Rdp;
using Orrery.Protocols.Ssh;
using Orrery.Protocols.Ssh.Agent;
using Orrery.Protocols.Telnet;
using Orrery.Protocols.Vnc;

namespace Orrery.App;

public partial class MainWindow : Window
{
    // The session toolbar's height, which isn't laid out yet when the first session starts.
    const double SessionToolbarHeight = 45;
    const double MinTerminalFontSize = 8, MaxTerminalFontSize = 32;
    static readonly Thickness TerminalMargin = new(14, 10);
    static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("orrery-node");
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    readonly DataLocation _location;
    readonly AppSettingsStore _settingsStore;
    readonly string? _movedFrom;
    readonly ConnectionStore _connectionStore;
    readonly ConnectionTree _tree;
    readonly ICredentialStore _credentials;
    readonly ServerCertificateValidator _certificateValidator;
    readonly HostKeyValidator _hostKeyValidator;
    readonly PropertiesPanel _properties;
    readonly DispatcherTimer _saveTimer;
    readonly string? _startupWarning;

    readonly TreeView _treeView;
    readonly TextBox _filter;
    readonly TextBlock _treeEmptyState;
    readonly StackPanel _tabStrip;
    readonly Panel _sessionHost;
    readonly Control _emptyState;
    readonly Border _sessionToolbar;
    readonly StackPanel _sessionActions;
    readonly TextBlock _sessionAddress, _sessionDetail, _sessionCount;
    readonly Controls.StrokeIcon _sessionLock;
    readonly Border _sidePanel, _propertiesHost, _dimmer;
    readonly ContentControl _settingsHost;
    readonly Button _sidePanelToggle, _inspectorToggle;
    readonly Control _titleBar, _rail, _statusBar, _tabStripBar;
    readonly Border _fullScreenBar, _fullScreenHotZone;
    readonly TextBlock _fullScreenTitle, _fullScreenHint;
    KeyGesture _fullScreenShortcut;

    // Full screen hides every pane and bar; these are what to put back.
    bool _fullScreen;
    WindowState _stateBeforeFullScreen;
    (PixelPoint Position, WindowState State)? _positionBeforeFullScreen;
    (bool Side, bool Inspector) _panesBeforeFullScreen;
    readonly Button _settingsRailButton;
    readonly Dictionary<string, (Button RailButton, Control View)> _sides;
    readonly RecentConnections _recent;
    readonly RecentPanel _recentPanel;
    readonly CredentialsView _credentialsView;
    string _side = "connections";
    readonly Button _lockVault;
    readonly HostStatusController _hostStatus;
    readonly TextBlock _noticeText;
    int _noticeVersion;
    readonly MenuItem _connectMenuItem, _browseFilesMenuItem, _wakeMenuItem, _addGuestMenuItem, _moveOutOfHostMenuItem, _duplicateMenuItem, _deleteMenuItem;
    readonly List<SessionTab> _tabs = [];
    SessionTab? _currentTab;

    bool _rebuilding;
    Point? _dragStart;
    NodeViewModel? _dragRow;

    // A plain press on a row that's part of a multi-selection keeps the selection, so the rows can be dragged
    // together; released without a drag, the press selects just that row.
    NodeViewModel? _pressedInSelection;
    TreeViewItem? _dropRow;

    // For the XAML previewer only.
    public MainWindow() : this(DataLocationResolver.ForPortable(Path.GetTempPath(), PlatformFolders.ForCurrentOs(), PortableDataChoice.WithApp))
    {
    }

    /// <param name="movedFrom">The previous data folder, when Orrery restarted after Move data.</param>
    public MainWindow(DataLocation location, string? movedFrom = null)
    {
        AvaloniaXamlLoader.Load(this);
        _location = location;
        _movedFrom = movedFrom;
        _settingsStore = AppSettingsStore.In(location);

        var storageText = this.FindControl<TextBlock>("StorageText")!;
        storageText.Text = $"Data: {location.DataDir}";
        ToolTip.SetTip(storageText, location.DataDir);
        if (FolderAccess.IsOpenToOthers(location.DataDir))
        {
            // Said once, at start-up; it doesn't stop anything.
            var accessText = this.FindControl<TextBlock>("DataAccessText")!;
            accessText.Text = "Other people who use this computer can change the data folder";
            accessText.Foreground = Dialogs.WarningBrush;
            accessText.IsVisible = true;
            ToolTip.SetTip(accessText, $"{location.DataDir}\n\nOrrery's saved hosts and trusted keys live here, so someone else could change them. "
                + "Settings › Move data can move them to a folder only you can use.");
        }
        this.FindControl<TextBlock>("ModeText")!.Text = location.Mode == StorageMode.Portable ? "Portable" : "Installed";
        if (OperatingSystem.IsMacOS())
        {
            this.FindControl<TextBlock>("QuickConnectShortcut")!.Text = "⌘K";
        }

        _treeView = this.FindControl<TreeView>("Tree")!;
        _filter = this.FindControl<TextBox>("FilterBox")!;
        _treeEmptyState = this.FindControl<TextBlock>("TreeEmptyState")!;
        _tabStrip = this.FindControl<StackPanel>("TabStrip")!;
        _sessionHost = this.FindControl<Panel>("SessionHost")!;
        _emptyState = this.FindControl<Control>("EmptyState")!;
        _sessionToolbar = this.FindControl<Border>("SessionToolbar")!;
        _sessionActions = this.FindControl<StackPanel>("SessionActions")!;
        _sessionAddress = this.FindControl<TextBlock>("SessionAddress")!;
        _sessionDetail = this.FindControl<TextBlock>("SessionDetail")!;
        _sessionLock = this.FindControl<Controls.StrokeIcon>("SessionLock")!;
        _sessionCount = this.FindControl<TextBlock>("SessionCountText")!;
        _sidePanel = this.FindControl<Border>("SidePanel")!;
        _propertiesHost = this.FindControl<Border>("PropertiesHost")!;
        _sides = new()
        {
            ["connections"] = (this.FindControl<Button>("ConnectionsRailButton")!, this.FindControl<Control>("ConnectionsSide")!),
            ["credentials"] = (this.FindControl<Button>("CredentialsRailButton")!, this.FindControl<Control>("CredentialsSide")!),
            ["recent"] = (this.FindControl<Button>("RecentRailButton")!, this.FindControl<Control>("RecentSide")!),
        };
        _settingsHost = this.FindControl<ContentControl>("SettingsHost")!;
        _settingsRailButton = this.FindControl<Button>("SettingsRailButton")!;
        _sidePanelToggle = this.FindControl<Button>("SidePanelToggle")!;
        _inspectorToggle = this.FindControl<Button>("InspectorToggle")!;
        _titleBar = this.FindControl<Control>("TitleBar")!;
        _rail = this.FindControl<Control>("Rail")!;
        _statusBar = this.FindControl<Control>("StatusBar")!;
        _tabStripBar = this.FindControl<Control>("TabStripBar")!;
        _fullScreenBar = this.FindControl<Border>("FullScreenBar")!;
        _fullScreenHotZone = this.FindControl<Border>("FullScreenHotZone")!;
        _fullScreenTitle = this.FindControl<TextBlock>("FullScreenTitle")!;
        _fullScreenHint = this.FindControl<TextBlock>("FullScreenHint")!;
        _fullScreenShortcut = Input.FullScreenShortcut.FromSettings(_settingsStore.Load().FullScreenShortcut);
        _dimmer = this.FindControl<Border>("Dimmer")!;
        _lockVault = this.FindControl<Button>("LockVaultButton")!;
        _connectMenuItem = this.FindControl<MenuItem>("ConnectMenuItem")!;
        _browseFilesMenuItem = this.FindControl<MenuItem>("BrowseFilesMenuItem")!;
        _wakeMenuItem = this.FindControl<MenuItem>("WakeMenuItem")!;
        _noticeText = this.FindControl<TextBlock>("NoticeText")!;
        _addGuestMenuItem = this.FindControl<MenuItem>("AddGuestMenuItem")!;
        _moveOutOfHostMenuItem = this.FindControl<MenuItem>("MoveOutOfHostMenuItem")!;
        _duplicateMenuItem = this.FindControl<MenuItem>("DuplicateMenuItem")!;
        _deleteMenuItem = this.FindControl<MenuItem>("DeleteMenuItem")!;

        _certificateValidator = new ServerCertificateValidator(
            new JsonCertificateTrustStore(location.FileIn("trusted-certificates.json")),
            prompt => Dispatcher.UIThread.InvokeAsync(
                () => new CertificatePromptWindow(prompt).ShowDialog<CertificateTrustDecision>(this)));

        _hostKeyValidator = new HostKeyValidator(
            new JsonCertificateTrustStore(location.FileIn("known-hosts.json")),
            prompt => Dispatcher.UIThread.InvokeAsync(
                () => new HostKeyPromptWindow(prompt).ShowDialog<CertificateTrustDecision>(this)));

        _credentials = CredentialStores.For(location, _settingsStore.Load());
        StorageLog.LogInformation("Credentials are kept in {Store}", (_credentials as KeychainCredentialStore)?.BackendName ?? "the vault file");
        if (_credentials is VaultFileStore vault)
        {
            vault.AutoLocked += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    UpdateVaultButton();
                    _credentialsView?.ShowLocked();

                    // As the manual lock does: keep the inspector's edits, then rebuild it so the credential picker stops listing names.
                    _properties?.TrySave();
                    if (_properties is not null)
                    {
                        await _properties.ShowAsync(_properties.Node);
                    }
                }
                catch (Exception)
                {
                    // The lock has happened; a failure redrawing the inspector mustn't take the app down.
                }
            });
        }

        _connectionStore = ConnectionStore.In(location);
        (_tree, _startupWarning) = LoadTree(_connectionStore, location);
        _tree.Changed += (_, _) =>
        {
            RebuildTree();
            ScheduleSave();
        };

        _saveTimer = new DispatcherTimer { Interval = SaveDelay };
        _saveTimer.Tick += (_, _) => SaveNow();

        _properties = new PropertiesPanel(_tree, _credentials, () => _settingsStore.Load().ZoomRemoteDesktops);
        _properties.ConnectRequested += async (_, entry) => await ConnectAsync(entry);
        _propertiesHost.Child = _properties;

        _credentialsView = new CredentialsView(_credentials, async () =>
        {
            bool unlocked = await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this);
            UpdateVaultButton();
            return unlocked;
        });
        _credentialsView.Changed += async (_, _) => await _properties.ShowAsync(_properties.Node);
        this.FindControl<ContentControl>("CredentialsSide")!.Content = _credentialsView.List;

        _recent = RecentConnections.In(location);
        _recentPanel = new RecentPanel(_recent);
        _recentPanel.OpenRequested += async (_, recent) => await OpenRecentAsync(recent);
        this.FindControl<ContentControl>("RecentSide")!.Content = _recentPanel;

        _hostStatus = new HostStatusController(() => _treeView.ItemsSource as IEnumerable<NodeViewModel> ?? [],
            () => _sidePanel.IsVisible && _side == "connections" && WindowState != WindowState.Minimized);
        _hostStatus.Enabled = _settingsStore.Load().ChecksHostStatus;

        WireTree();
        WireTitleBar();
        WireFullScreen();
        WireTabStrip();
        var layout = _settingsStore.Load();
        ShowSidePanel(layout.SidePanelOpen ?? true, save: false);
        ShowInspector(layout.InspectorOpen ?? true, save: false);
        RebuildTree();
        UpdateVaultButton();
        UpdateSessionChrome();

        KeyDown += OnWindowKeyDown;
        Opened += async (_, _) =>
        {
            FitTitleBarToCaptionButtons();

            // Move data leaves this in the new folder; it's only good for the first start there.
            var pendingCleanup = PendingCleanup.TryLoad(_location.DataDir);
            PendingCleanup.Clear(_location.DataDir);

            if (_startupWarning is not null)
            {
                await Dialogs.ShowMessageAsync(this, "Your connections couldn't be loaded", _startupWarning);
            }
            else if (_movedFrom is not null)
            {
                await OfferToDeleteOldCopyAsync(_movedFrom, pendingCleanup);
            }
            else
            {
                await OfferMRemoteNgImportAsync();
            }
        };

        Closing += (_, _) =>
        {
            _properties.TrySave();
            SaveNow();
            _hostStatus.Dispose();
            SessionLogger.CloseAll();
            foreach (var session in OpenSessions())
            {
                _ = session.DisposeAsync().AsTask();
            }

            (_credentials as IDisposable)?.Dispose();
        };
    }

    /// <summary>
    /// A damaged connections.json is moved aside, never overwritten, and Orrery starts with an empty tree.
    /// </summary>
    static (ConnectionTree Tree, string? Warning) LoadTree(ConnectionStore store, DataLocation location)
    {
        try
        {
            return (store.Load(), null);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or IOException or NotSupportedException)
        {
            var path = location.FileIn("connections.json");
            var aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(path, aside);
            }
            catch (IOException)
            {
                aside = path;
            }

            StorageLog.LogError("Couldn't read connections.json ({Message}); kept it as {Aside} and started empty", e.Message, aside);
            return (ConnectionTree.Empty(),
                $"{e.Message}\n\nThe file was kept as:\n{aside}\n\nRecent copies are in the backups folder next to it. Orrery is starting with an empty list.");
        }
    }

    void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            _connectionStore.Save(_tree);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _ = Dialogs.ShowMessageAsync(this, "Couldn't save your connections", e.Message);
        }
    }

    // ---- Tree ----

    void WireTree()
    {
        _filter.TextChanged += (_, _) => RebuildTree();

        _treeView.SelectionChanged += async (_, _) =>
        {
            if (!_rebuilding)
            {
                await _properties.ShowAsync(SelectedNode);
            }
        };

        // A host's row also expands on double-click (the tree item does that and marks the event handled), so
        // the expansion is put back: double-click connects, the arrow expands. Double-clicks on the arrow are left alone.
        _treeView.AddHandler(DoubleTappedEvent, async (_, e) =>
        {
            if (e.Source is not Visual source || source.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) is not null
                || RowAt(source)?.Node is not ConnectionEntry entry)
            {
                return;
            }

            if (e.Handled && source.FindAncestorOfType<TreeViewItem>(includeSelf: true) is { ItemCount: > 0 } item)
            {
                item.IsExpanded = !item.IsExpanded;
            }

            await ConnectAsync(entry);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        _treeView.KeyDown += async (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Delete when SelectedNodes() is { Count: > 0 } nodes:
                    e.Handled = true;
                    await DeleteAsync(nodes);
                    break;
                case Key.D when e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) && SingleSelectedNode is { } original:
                    e.Handled = true;
                    Duplicate(original);
                    break;
                case Key.F2 when SelectedNode is not null:
                    e.Handled = true;
                    _properties.FocusName();
                    break;
                case Key.Enter when SelectedNodes().OfType<ConnectionEntry>().Any():
                    e.Handled = true;
                    await ConnectSelectedAsync();
                    break;
            }
        };

        // With several rows selected, Connect and Delete take them all; the rest need a single row.
        _treeView.ContextMenu!.Opening += (_, _) =>
        {
            var nodes = SelectedNodes();
            int connections = nodes.OfType<ConnectionEntry>().Count();
            _connectMenuItem.IsEnabled = connections > 0;
            _connectMenuItem.Header = connections > 1 ? $"Connect {connections}" : "Connect";
            _browseFilesMenuItem.IsVisible = SingleSelectedNode is ConnectionEntry ssh && SettingsResolver.Resolve(ssh).Protocol.Value == Protocol.Ssh;
            int wakeable = nodes.OfType<ConnectionEntry>().Count(e => !string.IsNullOrEmpty(e.MacAddress));
            _wakeMenuItem.IsVisible = nodes.OfType<ConnectionEntry>().Any();
            _wakeMenuItem.IsEnabled = wakeable > 0;
            _wakeMenuItem.Header = wakeable > 1 ? $"Wake {wakeable}" : "Wake";
            ToolTip.SetTip(_wakeMenuItem, wakeable == 0 ? "Set a MAC address in the connection's settings to wake it." : null);
            _addGuestMenuItem.IsVisible = SingleSelectedNode is ConnectionEntry { GuestOf: null };
            _moveOutOfHostMenuItem.IsVisible = SingleSelectedNode is ConnectionEntry { GuestOf: not null };
            _duplicateMenuItem.IsEnabled = SingleSelectedNode is not null;
            _deleteMenuItem.IsEnabled = nodes.Count > 0;
            _deleteMenuItem.Header = nodes.Count > 1 ? $"Delete {nodes.Count} items" : "Delete";
        };

        // Drag a row onto a folder to move it in, or onto a connection to drop it before, after, or in as a guest (TreeDrop).
        _treeView.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(_treeView).Properties.IsLeftButtonPressed)
            {
                _dragStart = e.GetPosition(_treeView);
                _dragRow = RowAt(e.Source);
                _pressedInSelection = null;
                if (_dragRow is { } row && e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1
                    && _treeView.SelectedItems.Count > 1 && _treeView.SelectedItems.Contains(row)
                    && (e.Source as Visual)?.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) is null)
                {
                    e.Handled = true;
                    _pressedInSelection = row;
                    _treeView.Focus();
                }
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _treeView.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (_dragStart is not { } start || _dragRow is not { } row || !e.GetCurrentPoint(_treeView).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var delta = e.GetPosition(_treeView) - start;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8)
            {
                return;
            }

            _dragStart = null;
            _pressedInSelection = null;

            // The whole selection when the dragged row is part of it, else just that row.
            var rows = _treeView.SelectedItems.Contains(row) ? SelectedNodes() : [row.Node];
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DragFormat, string.Join(",", TreeDrop.Normalize(_tree.Root, rows).Select(n => n.Id))));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _treeView.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _dragStart = null;
            if (_pressedInSelection is { } pressed)
            {
                _pressedInSelection = null;
                _treeView.SelectedItem = pressed;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Moving onto another element raises DragEnter there in place of DragOver, so both mark the drop.
        _treeView.AddHandler(DragDrop.DragEnterEvent, OnTreeDragOver);
        _treeView.AddHandler(DragDrop.DragOverEvent, OnTreeDragOver);

        // Leaving one element inside the tree for another isn't leaving the tree; clearing then would flicker.
        _treeView.AddHandler(DragDrop.DragLeaveEvent, (_, e) =>
        {
            if (!new Rect(_treeView.Bounds.Size).Contains(e.GetPosition(_treeView)))
            {
                ShowDrop(null);
            }
        });

        _treeView.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            ShowDrop(null);
            if (DroppedImportFile(e) is { } file)
            {
                await ImportAsync(file);
            }
            else if (DraggedNodes(e) is { Count: > 0 } nodes && PlanDrop(e, nodes) is { } plan)
            {
                if (plan.Host is null && !nodes.All(n => ConnectionTree.CanPlace(n, plan.Folder)))
                {
                    _ = Dialogs.ShowMessageAsync(this, "Can't move this folder there", TooDeepMessage);
                    return;
                }

                _properties.TrySave();
                if (plan.Host is { } host)
                {
                    host.GuestsCollapsed = false;
                }

                TreeDrop.Apply(_tree, nodes, plan);
                Select(nodes.Select(n => n.Id));
            }
        });
    }

    // ---- Window chrome ----

    void WireTitleBar()
    {
        // Maximized, Windows pushes a window that draws its own title bar a few pixels past each screen edge;
        // Avalonia reports how far, so the content is kept inside by that much.
        var root = this.FindControl<Grid>("Root")!;
        root.Margin = OffScreenMargin;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == OffScreenMarginProperty || e.Property == WindowStateProperty)
            {
                root.Margin = OffScreenMargin;
            }
        };

        // The title bar is drawn by Orrery, so its empty parts move and maximise the window.
        var titleBar = this.FindControl<Border>("TitleBar")!;
        titleBar.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2)
                {
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                }
                else
                {
                    BeginMoveDrag(e);
                }
            }
        };
    }

    /// <summary>
    /// Leaves room for the system's window buttons: on the right on Windows, on the left on macOS. Where the
    /// window keeps its own title bar (most Linux desktops), none is needed. Both side columns stay equal
    /// so quick connect is centred on the window.
    /// </summary>
    void FitTitleBarToCaptionButtons()
    {
        var columns = this.FindControl<Grid>("TitleBarGrid")!.ColumnDefinitions;
        double side = 160;
        if (!IsExtendedIntoWindowDecorations)
        {
            side = 120;
        }
        else if (OperatingSystem.IsMacOS())
        {
            side = 180;
            this.FindControl<Control>("Brand")!.Margin = new Thickness(80, 0, 16, 0);
        }

        columns[0].Width = columns[2].Width = new GridLength(side);
    }

    /// <summary>
    /// The rail switches the side panel between connections, credentials and recent. Clicking the open
    /// one hides the panel, as in VS Code. Credentials edit in the right-hand pane, like connections.
    /// </summary>
    async void OnRailClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not string side)
        {
            return;
        }

        // With Settings open, any other rail button leaves it, and shows its panel rather than hiding it.
        bool leavingSettings = _settingsHost.IsVisible;
        if (leavingSettings)
        {
            CloseSettings();
        }

        if (side == _side && _sidePanel.IsVisible && !leavingSettings)
        {
            ShowSidePanel(false);
            return;
        }

        _properties.TrySave();
        _side = side;
        foreach (var (name, (_, view)) in _sides)
        {
            view.IsVisible = name == side;
        }

        ShowSidePanel(true);

        _propertiesHost.Child = side == "credentials" ? _credentialsView.Editor : _properties;
        switch (side)
        {
            case "credentials":
                ShowInspector(true);
                await _credentialsView.ShowAsync();
                break;
            case "recent":
                _recentPanel.Refresh();
                break;
            default:
                await _properties.ShowAsync(SelectedNode);
                break;
        }
    }

    // ---- Panes ----

    void OnToggleSidePanelClick(object? sender, RoutedEventArgs e) => ShowSidePanel(!_sidePanel.IsVisible);

    void OnToggleInspectorClick(object? sender, RoutedEventArgs e) => ShowInspector(!_propertiesHost.IsVisible);

    /// <summary>Shows or hides the side panel (Ctrl+B); the rail stays, and its button for the panel lights up.</summary>
    void ShowSidePanel(bool open, bool save = true)
    {
        _sidePanel.IsVisible = open;
        foreach (var (name, (button, _)) in _sides)
        {
            button.Classes.Set("active", open && name == _side && !_settingsHost.IsVisible);
        }

        ToolTip.SetTip(_sidePanelToggle, open ? "Hide the side panel (Ctrl+B)" : "Show the side panel (Ctrl+B)");
        if (save)
        {
            SaveLayout();
        }
    }

    /// <summary>Shows or hides the right-hand pane (Ctrl+Alt+B): connection settings or the credential editor.</summary>
    void ShowInspector(bool open, bool save = true)
    {
        if (!open)
        {
            _properties.TrySave();
        }

        _propertiesHost.IsVisible = open;
        ToolTip.SetTip(_inspectorToggle, open ? "Hide the settings pane (Ctrl+Alt+B)" : "Show the settings pane (Ctrl+Alt+B)");
        if (save)
        {
            SaveLayout();
        }
    }

    /// <summary>Remembers which panes are open for the next start.</summary>
    void SaveLayout()
    {
        try
        {
            _settingsStore.Save(_settingsStore.Load() with { SidePanelOpen = _sidePanel.IsVisible, InspectorOpen = _propertiesHost.IsVisible });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("App").LogWarning("Couldn't save the layout: {Message}", e.Message);
        }
    }

    // ---- Full screen ----

    void WireFullScreen()
    {
        // The full-screen shortcut (Ctrl+Alt+Enter unless changed in Settings), caught on the way down before
        // a session swallows it. Not while Settings is recording a new one.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (Input.FullScreenShortcut.Matches(_fullScreenShortcut, e.Key, e.KeyModifiers)
                && (e.Source as Visual)?.FindAncestorOfType<ShortcutRecorder>(includeSelf: true) is not { IsRecording: true })
            {
                e.Handled = true;
                if (_fullScreen)
                {
                    ExitFullScreen();
                }
                else
                {
                    EnterFullScreen();
                }
            }
        }, RoutingStrategies.Tunnel);

        _fullScreenHotZone.PointerEntered += (_, _) =>
        {
            _fullScreenTitle.Text = _currentTab?.Origin.Title;
            _fullScreenHint.Text = $"{Input.FullScreenShortcut.Describe(_fullScreenShortcut)} to leave";
            _fullScreenBar.IsVisible = true;
        };
        _fullScreenBar.PointerExited += (_, _) => _fullScreenBar.IsVisible = false;
    }

    void OnExitFullScreenClick(object? sender, RoutedEventArgs e) => ExitFullScreen();

    // ---- Tab strip keys and dragging ----

    SessionTab? _draggedTab;
    Point? _tabDragStart;

    void WireTabStrip()
    {
        // Caught on the way down, as the full-screen shortcut is; TabShortcuts decides what a focused session keeps.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || _tabs.Count == 0 || _settingsHost.IsVisible
                || (e.Source as Visual)?.FindAncestorOfType<ShortcutRecorder>(includeSelf: true) is { IsRecording: true }
                || Input.TabShortcuts.Match(e.Key, e.KeyModifiers, FocusPlaceOf(e.Source), OperatingSystem.IsMacOS()) is not { } command)
            {
                return;
            }

            e.Handled = true;
            if (command.Kind == Input.TabCommandKind.Close)
            {
                if (_currentTab is { } tab)
                {
                    _ = CloseTabAsync(tab.Session);
                }
            }
            else if (Input.TabShortcuts.Target(command, _currentTab is null ? 0 : _tabs.IndexOf(_currentTab), _tabs.Count) is { } index)
            {
                SelectTab(_tabs[index]);
            }
        }, RoutingStrategies.Tunnel);

        // Drag a tab sideways to move it; it lands where it's let go. The tab's own click still selects it.
        _tabStrip.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(_tabStrip).Properties.IsLeftButtonPressed && TabAt(e.Source) is { } tab)
            {
                (_draggedTab, _tabDragStart) = (tab, e.GetPosition(_tabStrip));
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _tabStrip.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_draggedTab is not { } tab || _tabDragStart is not { } start)
            {
                return;
            }

            double dx = e.GetPosition(_tabStrip).X - start.X;
            if (!_tabDragging && Math.Abs(dx) > 6)
            {
                _tabDragging = true;

                // The button theme animates RenderTransform (a pressed "shrink"), and an animated value outranks the
                // drag's own, so its transitions are off for the drag; the tabs slide with the drag's own instead.
                foreach (var each in _tabs)
                {
                    each.Header.Transitions = [];
                }

                tab.Header.ZIndex = 1;
                tab.Header.Opacity = 0.85;
            }

            if (_tabDragging)
            {
                ShowTabDrag(tab, dx);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _tabStrip.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (_draggedTab is { } tab && _tabDragging)
            {
                int target = _tabDropIndex;
                EndTabDrag();
                MoveTab(tab, target);
                return;
            }

            EndTabDrag();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _tabStrip.AddHandler(PointerCaptureLostEvent, (_, _) => EndTabDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    bool _tabDragging;
    int _tabDropIndex;

    /// <summary>
    /// Draws the drag: the held tab follows the pointer, kept within the strip, and the tabs it passes slide into its
    /// old place, so the gap shows where it will land. Only transforms move, so the held tab keeps the pointer.
    /// </summary>
    void ShowTabDrag(SessionTab tab, double dx)
    {
        int from = _tabs.IndexOf(tab);
        var bounds = tab.Header.Bounds;
        double left = _tabs[0].Header.Bounds.Left, right = _tabs[^1].Header.Bounds.Right;
        dx = Math.Clamp(dx, left - bounds.Left, right - bounds.Right);

        // Judged by the leading edge, not the centre: a tab wider than its neighbour could never get its centre
        // past the neighbour's middle at the end of the strip.
        double edge = dx < 0 ? bounds.Left + dx : bounds.Right + dx;
        _tabDropIndex = Input.TabShortcuts.DropIndex([.. _tabs.Select(t => t.Header.Bounds.Center.X)], from, edge);
        double step = bounds.Width + _tabStrip.Spacing;
        for (int i = 0; i < _tabs.Count; i++)
        {
            double x = i == from ? dx : Input.TabShortcuts.Shift(i, from, _tabDropIndex) * step;
            SlideTo(_tabs[i].Header, x, animate: i != from);
        }
    }

    static readonly TimeSpan TabSlideTime = TimeSpan.FromMilliseconds(120);

    static void SlideTo(Control header, double x, bool animate)
    {
        if (header.RenderTransform is not TranslateTransform shift)
        {
            shift = new TranslateTransform();
            header.RenderTransform = shift;
        }

        // The held tab tracks the pointer exactly; the others glide. Set once, so a glide under way isn't restarted.
        if (animate && shift.Transitions is null)
        {
            shift.Transitions = [new Avalonia.Animation.DoubleTransition { Property = TranslateTransform.XProperty, Duration = TabSlideTime }];
        }
        else if (!animate)
        {
            shift.Transitions = null;
        }

        shift.X = x;
    }

    void EndTabDrag()
    {
        foreach (var each in _tabs)
        {
            each.Header.ClearValue(RenderTransformProperty);
            each.Header.ClearValue(TransitionsProperty);
            each.Header.ZIndex = 0;
            each.Header.Opacity = 1;
        }

        (_draggedTab, _tabDragStart, _tabDragging) = (null, null, false);
    }

    SessionTab? TabAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } button ? _tabs.FirstOrDefault(t => t.Header == button) : null;

    void MoveTab(SessionTab tab, int index)
    {
        int from = _tabs.IndexOf(tab);
        if (from < 0 || index == from)
        {
            return;
        }

        _tabs.RemoveAt(from);
        _tabs.Insert(index, tab);
        _tabStrip.Children.Move(from, index);
    }

    static Input.FocusPlace FocusPlaceOf(object? source) => (source as Visual) switch
    {
        { } visual when visual.FindAncestorOfType<RemoteFrameView>(includeSelf: true) is not null => Input.FocusPlace.Desktop,
        { } visual when visual.FindAncestorOfType<TerminalView>(includeSelf: true) is not null => Input.FocusPlace.Terminal,
        _ => Input.FocusPlace.App,
    };

    /// <summary>
    /// The current session fills the screen the window is on, or <paramref name="screen"/>; everything else hides
    /// until the full-screen shortcut.
    /// </summary>
    void EnterFullScreen(Avalonia.Platform.Screen? screen = null)
    {
        if (_fullScreen || _currentTab is not { } tab)
        {
            return;
        }

        // Full screen covers the screen the window is on, so the window goes there first, and comes back after.
        _positionBeforeFullScreen = null;
        if (screen is not null && !Equals(Screens.ScreenFromWindow(this)?.Bounds, screen.Bounds))
        {
            _positionBeforeFullScreen = (Position, WindowState);
            WindowState = WindowState.Normal;
            Position = new PixelPoint(screen.WorkingArea.X + 40, screen.WorkingArea.Y + 40);
        }

        if (_settingsHost.IsVisible)
        {
            CloseSettings();
        }

        _fullScreen = true;
        _panesBeforeFullScreen = (_sidePanel.IsVisible, _propertiesHost.IsVisible);
        _stateBeforeFullScreen = WindowState;
        _titleBar.IsVisible = _rail.IsVisible = _statusBar.IsVisible = _tabStripBar.IsVisible = false;
        _sidePanel.IsVisible = _propertiesHost.IsVisible = _sessionToolbar.IsVisible = false;
        _fullScreenHotZone.IsVisible = true;

        // Avalonia keeps a title bar strip at the top of a full-screen window that draws into its client
        // area; Orrery has its own bar for leaving, so the window goes back to plain decorations meanwhile.
        ExtendClientAreaToDecorationsHint = false;
        WindowState = WindowState.FullScreen;
        Dispatcher.UIThread.Post(() => tab.View.Focus(), DispatcherPriority.Background);
    }

    void ExitFullScreen()
    {
        if (!_fullScreen)
        {
            return;
        }

        _fullScreen = false;
        _fullScreenHotZone.IsVisible = _fullScreenBar.IsVisible = false;
        _titleBar.IsVisible = _rail.IsVisible = _statusBar.IsVisible = _tabStripBar.IsVisible = true;
        _sidePanel.IsVisible = _panesBeforeFullScreen.Side;
        _propertiesHost.IsVisible = _panesBeforeFullScreen.Inspector;
        if (_positionBeforeFullScreen is { } before)
        {
            WindowState = WindowState.Normal;
            Position = before.Position;
            _stateBeforeFullScreen = before.State;
            _positionBeforeFullScreen = null;
        }

        WindowState = _stateBeforeFullScreen;
        ExtendClientAreaToDecorationsHint = true;
        UpdateSessionToolbar();
        if (_currentTab is { } tab)
        {
            Dispatcher.UIThread.Post(() => tab.View.Focus(), DispatcherPriority.Background);
        }
    }

    ConnectionNode? SelectedNode => (_treeView.SelectedItem as NodeViewModel)?.Node;

    /// <summary>The selected node when it's the only one selected.</summary>
    ConnectionNode? SingleSelectedNode => _treeView.SelectedItems.Count == 1 ? SelectedNode : null;

    /// <summary>Every selected row's node, the current one (shown in Properties) first.</summary>
    List<ConnectionNode> SelectedNodes() =>
        _treeView.SelectedItems.OfType<NodeViewModel>().Select(r => r.Node).OrderBy(n => n == SelectedNode ? 0 : 1).ToList();

    static NodeViewModel? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as NodeViewModel;

    /// <summary>The rows being dragged, from their ids; empty for a drag from somewhere else.</summary>
    List<ConnectionNode> DraggedNodes(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(DragFormat) is { } ids
            ? ids.Split(',').Select(id => Guid.TryParse(id, out var guid) ? _tree.Find(guid) : null).OfType<ConnectionNode>().ToList()
            : [];

    void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        if (DroppedImportFile(e) is not null)
        {
            ShowDrop(null);
            e.DragEffects = DragDropEffects.Copy;
            return;
        }

        var plan = DraggedNodes(e) is { Count: > 0 } nodes ? PlanDrop(e, nodes) : null;
        ShowDrop(plan);
        e.DragEffects = plan is not null ? DragDropEffects.Move : DragDropEffects.None;
    }

    /// <summary>Where <paramref name="dragged"/> would go if dropped now.</summary>
    DropPlan? PlanDrop(DragEventArgs e, IReadOnlyList<ConnectionNode> dragged)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is not NodeViewModel target)
        {
            return TreeDrop.Plan(_tree.Root, dragged, null, 0);
        }

        // The item's own row, not the item, which grows to hold an expanded folder's or host's children.
        var row = item.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_LayoutRoot") as Visual ?? item;
        double fraction = row.Bounds.Height > 0 ? Math.Clamp(e.GetPosition(row).Y / row.Bounds.Height, 0, 1) : 0.5;
        return TreeDrop.Plan(_tree.Root, dragged, target.Node, fraction);
    }

    /// <summary>Marks the row the plan names (TreeDrop decides which); null clears the mark.</summary>
    void ShowDrop(DropPlan? plan)
    {
        var item = plan?.Mark is { } mark && FindRow(mark.Id) is { } markRow ? _treeView.TreeContainerFromItem(markRow) as TreeViewItem : null;
        var position = plan?.Position;
        if (_dropRow is not null && _dropRow != item)
        {
            _dropRow.Classes.Remove("drop-before");
            _dropRow.Classes.Remove("drop-after");
            _dropRow.Classes.Remove("drop-into");
        }

        _dropRow = item;
        if (item is not null)
        {
            item.Classes.Set("drop-before", position == DropPosition.Before);
            item.Classes.Set("drop-after", position == DropPosition.After);
            item.Classes.Set("drop-into", position == DropPosition.Into);
        }
    }

    void RebuildTree()
    {
        var selected = SelectedNodes().Select(n => n.Id).ToList();
        _rebuilding = true;
        try
        {
            _treeView.ItemsSource = NodeViewModel.Build(_tree.Root, _filter.Text, () =>
            {
                ScheduleSave();
                _hostStatus.Refresh();
            });
            Select(selected);
        }
        finally
        {
            _rebuilding = false;
        }

        _treeEmptyState.IsVisible = _tree.Root.Children.Count == 0;
        UpdateTreeStatus();
        _hostStatus.Refresh();
        if (SelectedNode != _properties.Node)
        {
            _ = _properties.ShowAsync(SelectedNode);
        }
    }

    NodeViewModel? FindRow(Guid id)
    {
        var stack = new Stack<NodeViewModel>((_treeView.ItemsSource as IEnumerable<NodeViewModel>) ?? []);
        while (stack.TryPop(out var row))
        {
            if (row.Node.Id == id)
            {
                return row;
            }

            foreach (var child in row.Children)
            {
                stack.Push(child);
            }
        }

        return null;
    }

    void Select(Guid id)
    {
        if (FindRow(id) is { } row)
        {
            _treeView.SelectedItem = row;
        }
    }

    /// <summary>Selects these rows, the first becoming the current one; rows no longer shown are skipped.</summary>
    void Select(IEnumerable<Guid> ids)
    {
        _treeView.SelectedItems.Clear();
        foreach (var row in ids.Select(FindRow).OfType<NodeViewModel>())
        {
            _treeView.SelectedItems.Add(row);
        }
    }

    /// <summary>New items go in the selected folder, or beside the selected connection.</summary>
    ConnectionFolder TargetFolder() => SelectedNode switch
    {
        ConnectionFolder folder => folder,
        ConnectionEntry entry => entry.Parent ?? _tree.Root,
        _ => _tree.Root,
    };

    void OnNewConnectionClick(object? sender, RoutedEventArgs e) =>
        AddAndEdit(new ConnectionEntry { Name = "New connection" });

    void OnNewFolderClick(object? sender, RoutedEventArgs e) =>
        AddAndEdit(new ConnectionFolder { Name = "New folder" });

    static readonly string TooDeepMessage = $"Folders can be nested at most {ConnectionTree.MaxFolderDepth} levels deep.";

    void AddAndEdit(ConnectionNode node)
    {
        var parent = TargetFolder();
        if (!ConnectionTree.CanPlace(node, parent))
        {
            _ = Dialogs.ShowMessageAsync(this, "Can't add a folder here", TooDeepMessage);
            return;
        }

        _properties.TrySave();
        parent.Expanded = true;
        _filter.Text = string.Empty;
        _tree.Add(node, parent);
        Select(node.Id);
        Dispatcher.UIThread.Post(_properties.FocusName, DispatcherPriority.Background);
    }

    async void OnConnectMenuClick(object? sender, RoutedEventArgs e) => await ConnectSelectedAsync();

    async void OnBrowseFilesMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SingleSelectedNode is ConnectionEntry entry)
        {
            await ConnectAsync(entry, files: true);
        }
    }

    async void OnWakeMenuClick(object? sender, RoutedEventArgs e) => await WakeSelectedAsync();

    /// <summary>Sends a wake-up packet for each selected connection that has a MAC address.</summary>
    async Task WakeSelectedAsync()
    {
        var problems = new List<string>();
        var woken = new List<string>();
        foreach (var entry in SelectedNodes().OfType<ConnectionEntry>().Where(en => !string.IsNullOrEmpty(en.MacAddress)))
        {
            if (WakeOnLan.PlanFor(entry, out var whyNot) is not { } plan)
            {
                problems.Add($"{entry.Name}: {whyNot}");
                continue;
            }

            try
            {
                await WakeOnLan.SendAsync(plan.Mac, plan.Destination);
                woken.Add(entry.Name);
                WakeLog.LogInformation("Sent a wake-up packet for \"{Name}\" ({Mac}) to {Destination}", entry.Name, entry.MacAddress, plan.Destination);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                problems.Add($"{entry.Name}: {ex.Message}");
                WakeLog.LogWarning("Couldn't send a wake-up packet for \"{Name}\" to {Destination}: {Message}", entry.Name, plan.Destination, ex.Message);
            }
        }

        if (woken.Count > 0)
        {
            ShowNotice(woken.Count == 1 ? $"Sent a wake-up packet to {woken[0]}" : $"Sent wake-up packets to {woken.Count} connections");
        }

        if (problems.Count > 0)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't send the wake-up packet", string.Join("\n", problems));
        }
    }

    /// <summary>A line in the status bar for a few seconds.</summary>
    async void ShowNotice(string text)
    {
        int version = ++_noticeVersion;
        _noticeText.Text = text;
        _noticeText.IsVisible = true;
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (version == _noticeVersion)
        {
            _noticeText.IsVisible = false;
        }
    }

    /// <summary>Opens every selected connection, in list order; folders in the selection are skipped.</summary>
    async Task ConnectSelectedAsync()
    {
        var selected = SelectedNodes().ToHashSet();
        foreach (var entry in _tree.Root.Descendants().OfType<ConnectionEntry>().Where(selected.Contains).ToList())
        {
            await ConnectAsync(entry);
        }
    }

    async void OnEditDefaultsClick(object? sender, RoutedEventArgs e)
    {
        _properties.TrySave();
        _treeView.SelectedItem = null;
        await _properties.ShowAsync(_tree.Root);
    }

    /// <summary>Adds a new connection under the selected one, which becomes (or stays) a host.</summary>
    void OnAddGuestMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is not ConnectionEntry { GuestOf: null } host)
        {
            return;
        }

        _properties.TrySave();
        _filter.Text = string.Empty;
        host.GuestsCollapsed = false;
        var guest = new ConnectionEntry { Name = "New guest" };
        _tree.AddGuest(guest, host);
        Select(guest.Id);
        Dispatcher.UIThread.Post(_properties.FocusName, DispatcherPriority.Background);
    }

    void OnMoveOutOfHostMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is ConnectionEntry { GuestOf: not null } guest)
        {
            _properties.TrySave();
            _tree.MoveOutOfHost(guest);
            Select(guest.Id);
        }
    }

    void OnDuplicateMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SingleSelectedNode is { } node)
        {
            Duplicate(node);
        }
    }

    /// <summary>Copies the node beside the original and selects the copy, ready to rename.</summary>
    void Duplicate(ConnectionNode node)
    {
        _properties.TrySave();
        _filter.Text = string.Empty;
        var copy = _tree.Duplicate(node);
        Select(copy.Id);
        Dispatcher.UIThread.Post(_properties.FocusName, DispatcherPriority.Background);
    }

    async void OnDeleteMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedNodes() is { Count: > 0 } nodes)
        {
            await DeleteAsync(nodes);
        }
    }

    /// <summary>
    /// One row goes through <see cref="DeleteAsync(ConnectionNode)"/>, which offers to keep a host's guests.
    /// Several are confirmed once and go with everything in them, a host's guests included.
    /// </summary>
    async Task DeleteAsync(IReadOnlyList<ConnectionNode> selected)
    {
        var nodes = TreeDrop.Normalize(_tree.Root, selected);
        if (nodes.Count == 1)
        {
            await DeleteAsync(nodes[0]);
            return;
        }

        int inside = nodes.Sum(n => n switch
        {
            ConnectionFolder folder => folder.Descendants().Count(),
            ConnectionEntry entry => entry.Guests.Count(),
            _ => 0,
        });
        string body = $"{nodes.Count} items will be deleted" + (inside > 0 ? $", with the {inside} items in them." : ".");
        if (await Dialogs.ConfirmAsync(this, $"Delete {nodes.Count} items?", body, "Delete"))
        {
            await _properties.ShowAsync(null);
            foreach (var node in nodes)
            {
                _tree.Remove(node);
            }
        }
    }

    async Task DeleteAsync(ConnectionNode node)
    {
        if (node is ConnectionEntry { IsHost: true } host)
        {
            var choice = await new HostDeleteWindow(host, _tree).ShowDialog<HostDeleteChoice?>(this);
            if (choice is null)
            {
                return;
            }

            await _properties.ShowAsync(null);
            if (choice.KeepGuestsIn is { } destination)
            {
                _tree.RemoveKeepingGuests(host, destination);
            }
            else
            {
                _tree.Remove(host);
            }

            return;
        }

        string body = node is ConnectionFolder folder && folder.Children.Count > 0
            ? $"\"{node.Name}\" and the {folder.Descendants().Count()} items in it will be deleted."
            : $"\"{node.Name}\" will be deleted.";
        if (await Dialogs.ConfirmAsync(this, $"Delete {(node is ConnectionFolder ? "folder" : "connection")}?", body, "Delete"))
        {
            await _properties.ShowAsync(null);
            _tree.Remove(node);
        }
    }

    // ---- Connecting ----

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // A focused session swallows keys first, so Ctrl+K inside a remote desktop goes to the remote machine.
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.K && e.KeyModifiers == modifier)
        {
            e.Handled = true;
            _ = OpenQuickConnectAsync();
        }
        else if (e.Key == Key.OemComma && e.KeyModifiers == modifier)
        {
            e.Handled = true;
            OpenSettings();
        }
        else if (e.Key == Key.B && e.KeyModifiers == modifier)
        {
            e.Handled = true;
            ShowSidePanel(!_sidePanel.IsVisible);
        }
        else if (e.Key == Key.B && e.KeyModifiers == (modifier | KeyModifiers.Alt))
        {
            e.Handled = true;
            ShowInspector(!_propertiesHost.IsVisible);
        }
    }

    async void OnQuickConnectClick(object? sender, RoutedEventArgs e) => await OpenQuickConnectAsync();

    async Task OpenQuickConnectAsync()
    {
        if (_dimmer.IsVisible)
        {
            return;
        }

        QuickConnectChoice? choice;
        _dimmer.IsVisible = true;
        try
        {
            var open = _tabs.Select(t => new OpenSession(t.Origin.Title, t.Address, t.Origin.Protocol)).ToList();
            choice = await new QuickConnectWindow(_tree, open).ShowOver(this);
        }
        finally
        {
            _dimmer.IsVisible = false;
        }

        if (choice?.SessionIndex is { } index)
        {
            if (index < _tabs.Count)
            {
                SelectTab(_tabs[index]);
            }
        }
        else if (choice is { Edit: true, Entry: { } toEdit })
        {
            // Its settings, ready to change, without connecting.
            _filter.Text = string.Empty;
            if (_side != "connections")
            {
                OnRailClick(_sides["connections"].RailButton, new RoutedEventArgs());
            }

            ShowSidePanel(true);
            ShowInspector(true);
            foreach (var folder in toEdit.Ancestors())
            {
                folder.Expanded = true;
            }

            RebuildTree();
            Select(toEdit.Id);
            Dispatcher.UIThread.Post(_properties.FocusName, DispatcherPriority.Background);
        }
        else if (choice?.NewName is { } name)
        {
            AddAndEdit(new ConnectionEntry { Name = name, Host = choice.Host ?? string.Empty });
        }
        else if (choice?.Entry is { } entry)
        {
            Select(entry.Id);
            await ConnectAsync(entry);
        }
        else if (choice?.Host is not null)
        {
            await ConnectDirectAsync(choice);
        }
    }

    /// <param name="skipAgent">Ask for a login even if an SSH agent is running: its keys were just refused.</param>
    /// <param name="files">SSH: open a file browser (SFTP) instead of a terminal.</param>
    async Task ConnectAsync(ConnectionEntry entry, bool skipAgent = false, bool files = false)
    {
        if (_properties.Node == entry && !_properties.TrySave())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Host))
        {
            await Dialogs.ShowMessageAsync(this, $"\"{entry.Name}\" has no host", "Enter a host name or IP address in its settings first.");
            return;
        }

        var resolved = SettingsResolver.Resolve(entry);
        var protocol = resolved.Protocol.Value;
        files &= protocol == Protocol.Ssh;
        var origin = new SessionOrigin(files ? $"{entry.Name} · files" : entry.Name, protocol, entry.Id, () => ConnectAsync(entry, files: files), resolved.AutoReconnect.Value)
        {
            Files = files,
        };
        try
        {
            // SSH goes through its jump hosts itself; RDP, VNC and Telnet through a tunnel the jump hosts make.
            if (await JumpHopsAsync(entry) is not { } jumps)
            {
                return;
            }

            ConnectLog.LogInformation("Connecting \"{Name}\": {Protocol}{Files} to {Host}:{Port}{Via}", entry.Name, protocol, files ? " files" : string.Empty,
                entry.Host, resolved.Port.Value, jumps.Count == 0 ? string.Empty : " through " + string.Join(", ", jumps.Select(j => $"{j.Target.Host}:{j.Target.Port}")));
            var route = new SshRoute(jumps, protocol == Protocol.Ssh ? entry.PortForwards ?? [] : []);

            // Telnet has no login of its own: the server asks in the terminal.
            if (protocol == Protocol.Telnet)
            {
                StartTelnet(origin, new ConnectionTarget(entry.Host, resolved.Port.Value, resolved.Username.Value, null), jumps);
                return;
            }

            string? username = resolved.Username.Value, domain = resolved.Domain.Value, password = null;
            Credential? saved = null;
            if (resolved.CredentialId.Value is { } id)
            {
                if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
                {
                    return;
                }

                if (await _credentials.GetAsync(id) is { } credential)
                {
                    saved = credential;
                    (username, domain, password) = (credential.Username, credential.Domain, credential.Secret);
                    ConnectLog.LogInformation("\"{Name}\": logging in as {User} with saved credential \"{Credential}\" ({Kind})", entry.Name,
                        Account(username, domain), credential.Name, credential.Kind);
                }
                else
                {
                    ConnectLog.LogWarning("\"{Name}\": its saved credential no longer exists, so the login is asked for", entry.Name);
                }
            }

            // SSH with no saved credential: the agent's keys, if it has any, without asking for a password.
            if (password is null && protocol == Protocol.Ssh && resolved.UseSshAgent.Value && !skipAgent
                && !string.IsNullOrWhiteSpace(username) && await AgentHasKeysAsync())
            {
                ConnectLog.LogInformation("\"{Name}\": logging in as {User} with the SSH agent's keys", entry.Name, username);
                StartSshWithAgent(origin, new ConnectionTarget(entry.Host, resolved.Port.Value, username, domain),
                    whenRefused: () => ConnectAsync(entry, skipAgent: true, files), resolved.AllowLegacySsh.Value, route);
                return;
            }

            if (password is null)
            {
                var prompt = await new ConnectPromptWindow(_credentials, entry.Name, $"{entry.Host}:{resolved.Port.Value}", username, domain, offerRemember: true)
                    .ShowDialog<ConnectPromptResult?>(this);
                if (prompt is null)
                {
                    ConnectLog.LogDebug("\"{Name}\": the login prompt was cancelled", entry.Name);
                    return;
                }

                ConnectLog.LogInformation("\"{Name}\": logging in as {User} with {Source}", entry.Name, Account(prompt.Username, prompt.Domain),
                    prompt.CredentialId is null ? "a password typed at the prompt" : "a saved credential chosen at the prompt");

                if (prompt is { RememberForConnection: true, CredentialId: { } chosen })
                {
                    entry.Settings.CredentialId = chosen;
                    _tree.NotifyChanged();
                }

                if (prompt.CredentialId is not null)
                {
                    await _properties.CredentialsChangedAsync(entry);
                }

                (username, domain, password) = (prompt.Username, prompt.Domain, prompt.Password);
                if (prompt.CredentialId is { } chosenId && await _credentials.GetAsync(chosenId) is { } chosenCredential)
                {
                    saved = chosenCredential;
                }
            }

            var target = new ConnectionTarget(entry.Host, resolved.Port.Value, username, domain);
            // Checked when saved; a hand-edited file that doesn't parse just fits the window.
            DesktopResolution.TryParse(resolved.DesktopSize.Value, out var desktopSize, out _);
            await StartSessionAsync(origin, target, password, saved, resolved.ShareClipboard.Value, resolved.AllowLegacySsh.Value, route, desktopSize,
                resolved.EnlargeRemoteDesktop.Value, resolved.DisplayQuality.Value);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't read the saved credential", e.Message);
        }
    }

    async Task ConnectDirectAsync(QuickConnectChoice choice, bool skipAgent = false)
    {
        var host = choice.Host!;
        var origin = new SessionOrigin(host, choice.Protocol, null, () => ConnectDirectAsync(choice), AutoReconnect: true);
        if (choice.Protocol == Protocol.Telnet)
        {
            StartTelnet(origin, new ConnectionTarget(host, choice.Port, choice.Username, null));
            return;
        }

        if (choice.Protocol == Protocol.Ssh && !skipAgent && !string.IsNullOrWhiteSpace(choice.Username) && await AgentHasKeysAsync())
        {
            StartSshWithAgent(origin, new ConnectionTarget(host, choice.Port, choice.Username, null),
                whenRefused: () => ConnectDirectAsync(choice, skipAgent: true));
            return;
        }

        var prompt = await new ConnectPromptWindow(_credentials, host, $"{host}:{choice.Port}", choice.Username, null, offerRemember: false)
            .ShowDialog<ConnectPromptResult?>(this);
        if (prompt is null)
        {
            return;
        }

        var target = new ConnectionTarget(host, choice.Port, prompt.Username, prompt.Domain);
        var saved = prompt.CredentialId is { } id ? await _credentials.GetAsync(id) : null;
        await StartSessionAsync(origin, target, prompt.Password, saved);
    }

    Task StartSessionAsync(SessionOrigin origin, ConnectionTarget target, string password, Credential? saved,
        bool shareClipboard = true, bool allowLegacySsh = false, SshRoute? route = null, (int Width, int Height)? desktopSize = null, bool? enlarge = null,
        DisplayQuality quality = DisplayQuality.Balanced)
    {
        switch (origin.Protocol)
        {
            case Protocol.Ssh:
                return StartSshAsync(origin, target, password, saved, allowLegacySsh, route);
            case Protocol.Vnc:
                StartVnc(origin, target, password, shareClipboard, enlarge, route?.Jumps, quality);
                return Task.CompletedTask;
            default:
                StartRdp(origin, target, password, shareClipboard, desktopSize, enlarge, route?.Jumps, quality);
                return Task.CompletedTask;
        }
    }

    /// <summary>VNC servers set their own desktop size; the view shrinks it to fit and zooms it like RDP.</summary>
    /// <param name="enlarge">The connection's own choice; null uses the app setting.</param>
    /// <param name="jumps">SSH jump hosts to tunnel through; directly when there are none.</param>
    void StartVnc(SessionOrigin origin, ConnectionTarget target, string password, bool shareClipboard, bool? enlarge = null, IReadOnlyList<SshHop>? jumps = null,
        DisplayQuality quality = DisplayQuality.Balanced)
    {
        Open(origin, () =>
        {
            var session = VncSession.Start(target, password, shareClipboard, route: Tunnel(jumps), quality: quality);
            return (new RemoteFrameView(session, enlarge ?? _settingsStore.Load().ZoomRemoteDesktops), session);
        });
        UpdateVaultButton();
    }

    /// <param name="fixedSize">A desktop size to keep; null fits the session area and follows it.</param>
    /// <param name="enlarge">The connection's own choice; null uses the app setting.</param>
    /// <param name="jumps">SSH jump hosts to tunnel through; directly when there are none.</param>
    void StartRdp(SessionOrigin origin, ConnectionTarget target, string password, bool shareClipboard, (int Width, int Height)? fixedSize = null, bool? enlarge = null,
        IReadOnlyList<SshHop>? jumps = null, DisplayQuality quality = DisplayQuality.Balanced)
    {
        Open(origin, () =>
        {
            bool zoom = enlarge ?? _settingsStore.Load().ZoomRemoteDesktops;
            var (width, height) = fixedSize ?? DesktopSizeForView(zoom);
            var session = RdpSession.Start(target, password, width, height, _certificateValidator, shareClipboard, Tunnel(jumps), quality);
            return (new RemoteFrameView(session, zoom) { FollowsViewSize = fixedSize is null }, session);
        });
        UpdateVaultButton();
    }

    void StartTelnet(SessionOrigin origin, ConnectionTarget target, IReadOnlyList<SshHop>? jumps = null) => Open(origin, () =>
    {
        var session = TelnetSession.Start(target, 80, 24, Tunnel(jumps));
        return (NewTerminalView(session), session);
    });

    /// <summary>
    /// A new tunnel through <paramref name="jumps"/>, or none to connect directly. Made inside each session's
    /// create function, so reconnecting opens a fresh one; the session disposes it.
    /// </summary>
    SshTunnel? Tunnel(IReadOnlyList<SshHop>? jumps) => jumps is { Count: > 0 } ? new SshTunnel(jumps, _hostKeyValidator) : null;

    /// <summary>
    /// Opens a session in a new tab. When it should reconnect by itself, <paramref name="create"/> is kept so it
    /// can be opened again the same way, login included, without asking anything.
    /// </summary>
    void Open(SessionOrigin origin, Func<(Control View, IConnectionSession Session)> create)
    {
        var (view, session) = create();
        AddTab(view, session, origin.AutoReconnect ? origin with { Restart = create } : origin);
    }

    /// <summary>Whether an SSH agent is running and holds at least one key. Never throws.</summary>
    static Task<bool> AgentHasKeysAsync() => Task.Run(() =>
    {
        try
        {
            return SshAgentClient.Find()?.ListIdentities().Count > 0;
        }
        catch (Exception e)
        {
            Log.For("App").LogWarning("Couldn't reach the SSH agent: {Message}", e.Message);
            return false;
        }
    });

    /// <summary>
    /// Logs in with the agent's keys alone. If the server refuses them all, the tab closes and
    /// <paramref name="whenRefused"/> asks for a login the usual way.
    /// </summary>
    void StartSshWithAgent(SessionOrigin origin, ConnectionTarget target, Func<Task> whenRefused, bool allowLegacySsh = false, SshRoute? route = null)
    {
        Open(origin, () =>
        {
            var (view, session, rejected) = NewSshView(origin, target, new SshLogin(target.Username!, null, UseAgent: true), allowLegacySsh, route);
            session.StateChanged += (_, e) =>
            {
                if (e.State == SessionState.Failed && rejected())
                {
                    Dispatcher.UIThread.Post(async () =>
                    {
                        await CloseTabAsync(session);
                        await whenRefused();
                    });
                }
            };
            return (view, session);
        });
    }

    /// <summary>SSH needs a username; a saved SSH-key credential logs in with its key instead of the password.</summary>
    async Task StartSshAsync(SessionOrigin origin, ConnectionTarget target, string password, Credential? saved, bool allowLegacySsh, SshRoute? route)
    {
        if (string.IsNullOrWhiteSpace(target.Username))
        {
            await Dialogs.ShowMessageAsync(this, "SSH needs a username", "Set a username on the connection or its saved credential, then connect again.");
            return;
        }

        var login = SshLoginFor(target.Username, password, saved);
        Open(origin, () =>
        {
            var (view, session, _) = NewSshView(origin, target, login, allowLegacySsh, route);
            return (view, session);
        });
        UpdateVaultButton();
    }

    /// <summary>
    /// A terminal, or for <see cref="SessionOrigin.Files"/> a file browser (SFTP), on a new SSH connection.
    /// <c>Rejected</c> says whether the server turned the login down.
    /// </summary>
    (Control View, IConnectionSession Session, Func<bool> Rejected) NewSshView(SessionOrigin origin, ConnectionTarget target, SshLogin login, bool allowLegacySsh, SshRoute? route)
    {
        if (origin.Files)
        {
            var files = SftpSession.Start(target, login, _hostKeyValidator, allowLegacySsh, route?.Jumps);
            return (new FileBrowserView(files), files, () => files.AuthenticationRejected);
        }

        var shell = SshSession.Start(target, login, _hostKeyValidator, 80, 24, allowLegacySsh, route?.Jumps, route?.Forwards);
        return (NewTerminalView(shell), shell, () => shell.AuthenticationRejected);
    }

    /// <summary>A saved SSH-key credential logs in with its key instead of the password.</summary>
    static SshLogin SshLoginFor(string username, string? password, Credential? saved) =>
        saved?.Kind is CredentialKind.SshKey or CredentialKind.SshKeyWithPassphrase
            ? new SshLogin(username, null, saved.Secret, saved.Passphrase)
            : new SshLogin(username, password);

    /// <summary>What an SSH session goes through and opens besides its shell.</summary>
    sealed record SshRoute(IReadOnlyList<SshHop> Jumps, IReadOnlyList<PortForward> Forwards);

    static readonly ILogger ConnectLog = Log.For("Connect");
    static readonly ILogger StorageLog = Log.For("Storage");
    static readonly ILogger WakeLog = Log.For("Wake");

    /// <summary>A username as the log shows it, with its domain when there is one.</summary>
    static string Account(string? username, string? domain) =>
        string.IsNullOrEmpty(username) ? "(no username)" : string.IsNullOrEmpty(domain) ? username : $"{domain}\\{username}";

    /// <summary>
    /// The jump hosts on the way to <paramref name="entry"/>, each with its login: its saved credential, else the
    /// SSH agent's keys, else asked for now. Null when the chain is broken or a prompt was cancelled.
    /// </summary>
    async Task<List<SshHop>?> JumpHopsAsync(ConnectionEntry entry)
    {
        if (SettingsResolver.JumpChain(entry, _tree, out var error) is not { } chain)
        {
            ConnectLog.LogWarning("\"{Name}\": {Problem}", entry.Name, error);
            await Dialogs.ShowMessageAsync(this, $"Can't reach \"{entry.Name}\"", error!);
            return null;
        }

        var hops = new List<SshHop>();
        foreach (var jump in chain)
        {
            var resolved = SettingsResolver.Resolve(jump);
            string? username = resolved.Username.Value, password = null;
            Credential? saved = null;
            if (resolved.CredentialId.Value is { } id)
            {
                if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
                {
                    return null;
                }

                if (await _credentials.GetAsync(id) is { } credential)
                {
                    (saved, username, password) = (credential, credential.Username, credential.Secret);
                }
            }

            SshLogin login;
            if (saved is null && resolved.UseSshAgent.Value && !string.IsNullOrWhiteSpace(username) && await AgentHasKeysAsync())
            {
                login = new SshLogin(username, null, UseAgent: true);
            }
            else
            {
                if (saved is null)
                {
                    var prompt = await new ConnectPromptWindow(_credentials, $"Jump host {jump.Name}", $"{jump.Host}:{resolved.Port.Value}", username, null, offerRemember: false)
                        .ShowDialog<ConnectPromptResult?>(this);
                    if (prompt is null)
                    {
                        return null;
                    }

                    (username, password) = (prompt.Username, prompt.Password);
                    saved = prompt.CredentialId is { } chosen ? await _credentials.GetAsync(chosen) : null;
                }

                if (string.IsNullOrWhiteSpace(username))
                {
                    await Dialogs.ShowMessageAsync(this, "SSH needs a username", $"Set a username on the jump host \"{jump.Name}\" or its saved credential, then connect again.");
                    return null;
                }

                login = SshLoginFor(username, password, saved);
            }

            hops.Add(new SshHop(new ConnectionTarget(jump.Host, resolved.Port.Value, username, null), login, resolved.AllowLegacySsh.Value));
        }

        return hops;
    }

    /// <summary>A terminal at the saved text size; Ctrl+= and Ctrl+- in any terminal resize them all.</summary>
    TerminalView NewTerminalView(ITerminalSession session)
    {
        var view = new TerminalView(session)
        {
            Margin = TerminalMargin,
            TextSize = _settingsStore.Load().TerminalFontSize ?? AppSettings.DefaultTerminalFontSize,
        };
        view.TextSizeRequested += (_, step) => ChangeTerminalTextSize(step);
        return view;
    }

    /// <param name="step">+1 larger, -1 smaller, 0 back to the default.</param>
    void ChangeTerminalTextSize(int step)
    {
        double current = _settingsStore.Load().TerminalFontSize ?? AppSettings.DefaultTerminalFontSize;
        double size = step == 0 ? AppSettings.DefaultTerminalFontSize : Math.Clamp(current + step, MinTerminalFontSize, MaxTerminalFontSize);
        foreach (var view in _tabs.Select(t => t.View).OfType<TerminalView>())
        {
            view.TextSize = size;
        }

        try
        {
            _settingsStore.Save(_settingsStore.Load() with { TerminalFontSize = size == AppSettings.DefaultTerminalFontSize ? null : size });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("App").LogWarning("Couldn't save the terminal text size: {Message}", e.Message);
        }
    }

    // ---- Session tabs ----

    void AddTab(Control view, IConnectionSession session, SessionOrigin origin)
    {
        var tab = NewTab(view, session, origin);
        _recent.Add(new RecentConnection(origin.EntryId, origin.Title, origin.Protocol,
            session.Target.Host, session.Target.Port, session.Target.Username, DateTimeOffset.Now));
        _tabs.Add(tab);
        _tabStrip.Children.Add(tab.Header);
        _sessionHost.Children.Add(tab.Host);
        if (_settingsHost.IsVisible)
        {
            CloseSettings();
        }

        SelectTab(tab);
        UpdateSessionChrome();
    }

    SessionTab NewTab(Control view, IConnectionSession session, SessionOrigin origin, int reconnectAttempt = 0)
    {
        var tab = new SessionTab(session, view, origin) { ReconnectAttempt = reconnectAttempt };
        SessionLogger.Attach(tab);
        tab.Selected += (_, _) => SelectTab(tab);
        tab.CloseRequested += async (_, _) => await CloseTabAsync(session);
        tab.Header.ContextMenu = TabMenu(tab);
        tab.StateChanged += (_, _) =>
        {
            UpdateTreeStatus();
            if (ShouldReconnect(tab))
            {
                BeginReconnect(tab);
            }

            if (tab == _currentTab)
            {
                UpdateSessionToolbar();
            }
        };
        return tab;
    }

    // ---- Reconnecting by itself ----

    static readonly TimeSpan[] ReconnectDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];

    bool ShouldReconnect(SessionTab tab) => tab.WasCutOff && !tab.IsReconnecting && _tabs.Contains(tab);

    void BeginReconnect(SessionTab tab)
    {
        // A try that got connected starts the count again.
        int attempt = tab.HasConnected ? 1 : tab.ReconnectAttempt + 1;
        if (attempt > ReconnectDelays.Length)
        {
            return;
        }

        tab.ShowReconnecting(attempt, ReconnectDelays.Length, ReconnectDelays[attempt - 1], () => _ = RestartTabAsync(tab, attempt));
    }

    /// <summary>Puts a new session, opened the same way, in place of <paramref name="old"/>.</summary>
    async Task RestartTabAsync(SessionTab old, int attempt)
    {
        int index = _tabs.IndexOf(old);
        if (index < 0 || old.Origin.Restart is not { } restart)
        {
            return;
        }

        var (view, session) = restart();
        var tab = NewTab(view, session, old.Origin, attempt);
        bool current = old == _currentTab;
        _tabs[index] = tab;
        _tabStrip.Children[_tabStrip.Children.IndexOf(old.Header)] = tab.Header;
        _sessionHost.Children[_sessionHost.Children.IndexOf(old.Host)] = tab.Host;
        if (current)
        {
            SelectTab(tab);
        }
        else
        {
            tab.IsSelected = false;
        }

        UpdateSessionChrome();
        old.EndedByUser = true;
        await old.Session.DisposeAsync();
    }

    void SelectTab(SessionTab? tab)
    {
        _currentTab = tab;
        foreach (var each in _tabs)
        {
            each.IsSelected = each == tab;
        }

        UpdateSessionToolbar();
        if (tab is not null)
        {
            tab.Header.BringIntoView();
            Dispatcher.UIThread.Post(() => tab.View.Focus(), DispatcherPriority.Background);
        }
    }

    async Task CloseTabAsync(IConnectionSession session)
    {
        if (_tabs.FirstOrDefault(t => t.Session == session) is { } tab)
        {
            tab.EndedByUser = true;
            tab.CancelReconnect();
            int index = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            _tabStrip.Children.Remove(tab.Header);
            _sessionHost.Children.Remove(tab.Host);
            if (_tabs.Count == 0)
            {
                ExitFullScreen();
            }

            if (tab == _currentTab)
            {
                SelectTab(_tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)]);
            }

            UpdateSessionChrome();
        }

        await session.DisposeAsync();
    }

    /// <summary>The bar under the tabs: where the current session is connected, and what can be done with it.</summary>
    void UpdateSessionToolbar()
    {
        _sessionToolbar.IsVisible = _currentTab is not null && !_fullScreen;
        _sessionActions.Children.Clear();
        if (_currentTab is not { } tab)
        {
            return;
        }

        var session = tab.Session;
        var state = session.State;
        _sessionAddress.Text = tab.Address;
        _sessionLock.Foreground = (IBrush?)this.FindResource(state == SessionState.Connected ? "OrreryConnected" : "OrreryMuted");
        _sessionDetail.Text = $"{(tab.Origin.Files ? "SFTP" : ProtocolInfo.Label(tab.Origin.Protocol))} · " + state switch
        {
            SessionState.Connecting => "Connecting…",
            SessionState.Disconnected => "Disconnected",
            SessionState.Failed => "Couldn't connect",
            _ when session is IFrameBufferSession frame && frame.Width > 0 => $"{frame.Width}×{frame.Height}",
            _ => "Connected",
        } + (tab.View is RemoteFrameView { ViewOnly: true } ? " · View only" : string.Empty);

        if (state == SessionState.Connected && tab.View is RemoteFrameView desktop && !desktop.ViewOnly)
        {
            var ctrlAltDel = new Button { Content = "Ctrl+Alt+Del" };
            ToolTip.SetTip(ctrlAltDel, "Send Ctrl+Alt+Del to the remote computer");
            ctrlAltDel.Click += (_, _) =>
            {
                desktop.SendKeys(RemoteKeyCombo.CtrlAltDel);
                desktop.Focus();
            };
            _sessionActions.Children.Add(ctrlAltDel);
        }

        if (state == SessionState.Connected)
        {
            var fullScreen = new Button { Content = Controls.StrokeIcon.Create(Controls.Icons.FullScreen, 15) };
            fullScreen.Classes.Add("icon");
            fullScreen.Classes.Add("small");
            ToolTip.SetTip(fullScreen, $"Full screen ({Input.FullScreenShortcut.Describe(_fullScreenShortcut)})");
            Avalonia.Automation.AutomationProperties.SetName(fullScreen, "Full screen");
            fullScreen.Click += (_, _) => EnterFullScreen();
            _sessionActions.Children.Add(fullScreen);
        }

        var more = new Button { Content = Controls.StrokeIcon.Create(Controls.Icons.More, 16) };
        more.Classes.Add("icon");
        more.Classes.Add("small");
        ToolTip.SetTip(more, "More options");
        Avalonia.Automation.AutomationProperties.SetName(more, "More options");
        more.Click += (_, _) => Controls.PopupMenu.ShowAt(more, SessionMenu(tab), PlacementMode.BottomEdgeAlignedRight);
        _sessionActions.Children.Add(more);

        if (state is SessionState.Connecting or SessionState.Connected)
        {
            var disconnect = new Button { Content = "Disconnect" };
            disconnect.Classes.Add("danger");
            disconnect.Click += async (_, _) =>
            {
                tab.EndedByUser = true;
                await session.DisconnectAsync();
            };
            _sessionActions.Children.Add(disconnect);
        }
        else
        {
            var reconnect = new Button { Content = "Reconnect" };
            reconnect.Classes.Add("accent");
            reconnect.Click += async (_, _) =>
            {
                await CloseTabAsync(session);
                await tab.Origin.Reconnect();
            };
            _sessionActions.Children.Add(reconnect);
        }
    }

    /// <summary>What the session toolbar's More menu offers, for the session as it is now.</summary>
    List<Control> SessionMenu(SessionTab tab)
    {
        bool connected = tab.Session.State == SessionState.Connected;
        bool mac = OperatingSystem.IsMacOS();
        var items = new List<Control>();
        if (tab.View is RemoteFrameView desktop)
        {
            var keys = MenuItemFor("Send keys", null, enabled: connected && !desktop.ViewOnly);
            foreach (var combo in RemoteKeyCombo.Menu)
            {
                keys.Items.Add(MenuItemFor(combo.Label, () =>
                {
                    desktop.SendKeys(combo);
                    desktop.Focus();
                }));
            }

            items.Add(keys);
            var viewOnly = MenuItemFor("View only", () =>
            {
                desktop.ViewOnly = !desktop.ViewOnly;
                UpdateSessionToolbar();
            });
            viewOnly.ToggleType = MenuItemToggleType.CheckBox;
            viewOnly.IsChecked = desktop.ViewOnly;
            ToolTip.SetTip(viewOnly, "Watch without sending keys or clicks");
            items.Add(viewOnly);
            items.Add(new Separator());
            items.Add(MenuItemFor("Save screenshot…", async () => await SaveScreenshotAsync(tab, desktop), enabled: desktop.HasFrame));
        }
        else if (tab.View is TerminalView terminal)
        {
            string command = mac ? "Cmd" : "Ctrl";
            items.Add(MenuItemFor("Copy", async () => await terminal.CopyAsync(), mac ? "Cmd+C" : "Ctrl+Shift+C", terminal.HasSelection));
            items.Add(MenuItemFor("Paste", async () => await terminal.PasteAsync(), mac ? "Cmd+V" : "Ctrl+Shift+V", connected));
            items.Add(new Separator());
            items.Add(MenuItemFor("Larger text", () => ChangeTerminalTextSize(1), $"{command}+OemPlus"));
            items.Add(MenuItemFor("Smaller text", () => ChangeTerminalTextSize(-1), $"{command}+OemMinus"));
            items.Add(MenuItemFor("Reset text size", () => ChangeTerminalTextSize(0), $"{command}+D0"));
            items.Add(new Separator());
            items.Add(MenuItemFor("Save output…", async () => await SaveOutputAsync(tab, terminal)));
            items.Add(MenuItemFor("Clear scrollback", terminal.ClearScrollback));
        }

        items.Add(new Separator());
        items.AddRange(TabActions(tab));
        return items;
    }

    /// <summary>The tab's right-click menu.</summary>
    ContextMenu TabMenu(SessionTab tab)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var item in TabActions(tab))
            {
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemFor("Close", async () => await CloseTabAsync(tab.Session)));
            menu.Items.Add(MenuItemFor("Close other tabs", async () =>
            {
                foreach (var other in _tabs.Where(t => t != tab).ToList())
                {
                    await CloseTabAsync(other.Session);
                }
            }, enabled: _tabs.Count > 1));
        };
        return menu;
    }

    /// <summary>What applies to any session: fill the screen, open it again alongside, or start it over.</summary>
    IEnumerable<Control> TabActions(SessionTab tab)
    {
        yield return MenuItemFor("Full screen", () =>
        {
            SelectTab(tab);
            EnterFullScreen();
        }, Input.FullScreenShortcut.Format(_fullScreenShortcut), tab.Session.State == SessionState.Connected);

        if (Screens.All.Count > 1)
        {
            var current = Screens.ScreenFromWindow(this);
            var on = MenuItemFor("Full screen on", null, enabled: tab.Session.State == SessionState.Connected);
            foreach (var (screen, i) in Screens.All.Select((s, i) => (s, i)))
            {
                var label = $"Display {i + 1}  ({screen.Bounds.Width}×{screen.Bounds.Height})"
                    + (screen.IsPrimary ? " · main" : string.Empty)
                    + (Equals(screen.Bounds, current?.Bounds) ? " · this window" : string.Empty);
                on.Items.Add(MenuItemFor(label, () =>
                {
                    SelectTab(tab);
                    EnterFullScreen(screen);
                }));
            }

            yield return on;
        }

        if (tab.Origin is { Protocol: Protocol.Ssh, EntryId: { } id } && _tree.Find(id) is ConnectionEntry entry)
        {
            yield return tab.Origin.Files
                ? MenuItemFor("Open a terminal", async () => await ConnectAsync(entry))
                : MenuItemFor("Browse files (SFTP)", async () => await ConnectAsync(entry, files: true));
        }

        yield return MenuItemFor("Duplicate session", async () => await tab.Origin.Reconnect());
        yield return MenuItemFor("Reconnect", async () =>
        {
            await CloseTabAsync(tab.Session);
            await tab.Origin.Reconnect();
        });
    }

    /// <param name="shortcut">Shown beside the item (the key itself is handled elsewhere), in KeyGesture form.</param>
    static MenuItem MenuItemFor(string header, Action? click, string? shortcut = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (shortcut is not null)
        {
            item.InputGesture = KeyGesture.Parse(shortcut.Replace("Cmd", "Meta"));
        }

        if (click is not null)
        {
            item.Click += (_, _) => click();
        }

        return item;
    }

    async Task SaveScreenshotAsync(SessionTab tab, RemoteFrameView desktop)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save screenshot",
            SuggestedFileName = $"{SafeFileName(tab.Origin.Title)} {DateTime.Now:yyyy-MM-dd HHmmss}.png",
            DefaultExtension = "png",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            desktop.SaveScreenshot(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't save the screenshot", e.Message);
        }
    }

    async Task SaveOutputAsync(SessionTab tab, TerminalView terminal)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save session output",
            SuggestedFileName = $"{SafeFileName(tab.Origin.Title)} {DateTime.Now:yyyy-MM-dd HHmmss}.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Text") { Patterns = ["*.txt", "*.log"] }],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(terminal.AllText());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't save the output", e.Message);
        }
    }

    static string SafeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));

    /// <summary>
    /// The starting desktop size, in remote pixels, filling the session area. The view corrects it with
    /// a resize once the tab is laid out.
    /// </summary>
    (int Width, int Height) DesktopSizeForView(bool zoom)
    {
        var bounds = _sessionHost.Bounds;
        double available = bounds.Height - (_sessionToolbar.IsVisible ? 0 : SessionToolbarHeight);
        var (fullWidth, fullHeight) = RemoteFrameView.DesktopSize(new Size(bounds.Width, available), RenderScaling, zoom);
        int width = Math.Max(640, fullWidth);
        int height = Math.Max(480, fullHeight);

        // Many RDP servers need the width to be a multiple of 4.
        return (width - width % 4, height);
    }

    IEnumerable<IConnectionSession> OpenSessions() => _tabs.Select(t => t.Session);

    void UpdateSessionChrome()
    {
        _emptyState.IsVisible = _tabs.Count == 0;
        _sessionCount.Text = _tabs.Count switch { 0 => "No sessions open", 1 => "1 session open", var n => $"{n} sessions open" };
        UpdateTreeStatus();
    }

    /// <summary>Lights the dot beside each saved connection that has a session open.</summary>
    void UpdateTreeStatus()
    {
        var states = new Dictionary<Guid, SessionState>();
        foreach (var tab in _tabs)
        {
            // Connected wins when one connection is open in two tabs.
            if (tab.Origin.EntryId is { } id && tab.Session.State is SessionState.Connected or SessionState.Connecting
                && states.GetValueOrDefault(id) != SessionState.Connected)
            {
                states[id] = tab.Session.State;
            }
        }

        var stack = new Stack<NodeViewModel>((_treeView.ItemsSource as IEnumerable<NodeViewModel>) ?? []);
        while (stack.TryPop(out var row))
        {
            row.Status = states.TryGetValue(row.Node.Id, out var state) ? state : null;
            foreach (var child in row.Children)
            {
                stack.Push(child);
            }
        }
    }

    // ---- Import and export ----

    /// <summary>An mRemoteNG confCons.xml or an Orrery export.</summary>
    static string? DroppedImportFile(DragEventArgs e) =>
        e.DataTransfer.TryGetFile()?.TryGetLocalPath() is { } path && IsImportFile(path) ? path : null;

    static bool IsImportFile(string path) => ConnectionExporter.FormatFor(path) is ExportFormat.MRemoteNg or ExportFormat.Orrery;

    /// <summary>First launch with an empty list on a machine that has mRemoteNG: offer to bring its connections over.</summary>
    async Task OfferMRemoteNgImportAsync()
    {
        if (!OperatingSystem.IsWindows() || _tree.Root.Children.Count > 0)
        {
            return;
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG", "confCons.xml");
        if (File.Exists(path) && await Dialogs.ConfirmAsync(this, "Import your mRemoteNG connections?",
                $"Orrery found mRemoteNG's connections file:\n{path}\n\nYou'll see what will be imported before anything changes.", "Review import"))
        {
            await ImportAsync(path);
        }
    }

    async Task ChooseImportFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import connections",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("mRemoteNG or Orrery connections") { Patterns = ["*.xml", "*.json"] },
                new Avalonia.Platform.Storage.FilePickerFileType("mRemoteNG connections") { Patterns = ["*.xml"] },
                new Avalonia.Platform.Storage.FilePickerFileType("Orrery export") { Patterns = ["*.json"] },
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path && IsImportFile(path))
        {
            await ImportAsync(path);
        }
    }

    async Task ImportAsync(string path)
    {
        ImportResult? result = null;
        string? masterPassword = null, passwordError = null;
        bool orrery = ConnectionExporter.FormatFor(path) == ExportFormat.Orrery;
        while (result is null)
        {
            try
            {
                var password = masterPassword;
                result = await Task.Run(() => orrery ? OrreryImporter.ImportFile(path, password) : MRemoteNgImporter.ImportFile(path, password));
            }
            catch (MasterPasswordRequiredException e)
            {
                passwordError = e.WrongPassword ? "That password is incorrect." : null;
                masterPassword = orrery
                    ? await Dialogs.PromptPasswordAsync(this, "Export password",
                        "This Orrery export includes saved passwords, so it's encrypted. Enter the password it was exported with.", passwordError)
                    : await Dialogs.PromptPasswordAsync(this, "Master password",
                        "This mRemoteNG file is protected with a master password. Enter it to read the connections and saved passwords.", passwordError);
                if (masterPassword is null)
                {
                    return;
                }
            }
            catch (Exception e) when (e is ImportException or IOException or UnauthorizedAccessException)
            {
                StorageLog.LogWarning("Couldn't import {Path}: {Message}", path, e.Message);
                await Dialogs.ShowMessageAsync(this, "Couldn't import this file", e.Message);
                return;
            }
            catch (Exception e)
            {
                // The importer means to fail only as above; anything else is a bug, not a reason to exit.
                Log.For("App").LogError(e, "Importing {Path} failed", path);
                await Dialogs.ShowMessageAsync(this, "Couldn't import this file",
                    "Orrery couldn't read this file. It may be damaged, or not an mRemoteNG connections file or Orrery export.");
                return;
            }
        }

        int already = result.CountAlreadyIn(_tree);
        var storeName = (_credentials as KeychainCredentialStore)?.BackendName ?? "your vault";
        var choice = await new ImportPreviewWindow(result, already, storeName).ShowDialog<ImportChoice?>(this);
        if (choice is null)
        {
            return;
        }

        if (choice.SkipAlreadyImported && already > 0)
        {
            result = result.WithoutAlreadyIn(_tree);
        }

        try
        {
            if (result.Credentials.Count > 0)
            {
                if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
                {
                    return;
                }

                // One rewrite of the vault or index, off the UI thread; the stores finish synchronously.
                var credentials = result.Credentials;
                await Task.Run(() => _credentials.SaveAllAsync(credentials));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StorageLog.LogWarning("Couldn't save the imported credentials: {Message}", e.Message);
            await Dialogs.ShowMessageAsync(this, "Couldn't save the imported credentials", $"{e.Message}\n\nNothing was imported.");
            return;
        }

        _properties.TrySave();
        _filter.Text = string.Empty;
        var importedFolder = result.Root;
        _tree.AddFrom(importedFolder, _tree.Root, choice.IntoNewFolder);
        SaveNow();
        if (choice.IntoNewFolder)
        {
            Select(importedFolder.Id);
        }

        StorageLog.LogInformation("Imported {Connections} connections in {Folders} folders and {Credentials} credentials from {Path}",
            result.Report.Connections, result.Report.Folders, result.Credentials.Count, path);
        var reportPath = WriteImportReport(result.Report);
        await Dialogs.ShowMessageAsync(this, $"Imported {result.Report.Connections} connection{(result.Report.Connections == 1 ? "" : "s")}",
            reportPath is null ? "The import is complete." : $"A summary of what was and wasn't imported is saved at:\n{reportPath}");
    }

    string? WriteImportReport(ImportReport report)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var path = Path.Combine(_location.DataDir, "import", $"{report.From.ToLowerInvariant()}-{now:yyyyMMdd-HHmmss}.md");
            AtomicFile.WriteAllText(path, report.ToMarkdown(now));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    async void OnExportMenuClick(object? sender, RoutedEventArgs e) => await ExportAsync(SelectedNode ?? _tree.Root);

    /// <summary>Saves <paramref name="node"/> (the root for everything) as an Orrery, mRemoteNG or CSV file, chosen by its type.</summary>
    async Task ExportAsync(ConnectionNode node)
    {
        _properties.TrySave();
        bool everything = node.Parent is null;
        var name = everything ? "Orrery connections" : string.Join("_", node.Name.Split(Path.GetInvalidFileNameChars())).Trim();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = everything ? "Export all connections" : $"Export \"{node.Name}\"",
            SuggestedFileName = name.Length > 0 ? name : "Orrery connections",
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Orrery export (all settings)") { Patterns = ["*.json"] },
                new FilePickerFileType("mRemoteNG confCons.xml") { Patterns = ["*.xml"] },
                new FilePickerFileType("CSV (spreadsheet)") { Patterns = ["*.csv"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        if (ConnectionExporter.FormatFor(path) is not { } format)
        {
            await Dialogs.ShowMessageAsync(this, "Choose a file type", "Save the export as .json (Orrery), .xml (mRemoteNG) or .csv.");
            return;
        }

        // Saved credentials in play: set on what's exported, on a folder above it, or on anything inside it (a host's guests too).
        var inside = node switch
        {
            ConnectionFolder folder => folder.Descendants(),
            ConnectionEntry host => host.Guests,
            _ => [],
        };
        var used = node.Ancestors().Cast<ConnectionNode>().Append(node).Concat(inside)
            .Select(n => n.Settings.CredentialId).OfType<Guid>().Where(id => id != InheritableSettings.NoCredential).ToHashSet();

        // Passwords and keys go in only if asked for, encrypted with a password chosen here. CSV never has them.
        string? exportPassword = null;
        if (used.Count > 0 && format != ExportFormat.Csv)
        {
            if (await new ExportSecretsWindow(format).ShowDialog<ExportSecretsChoice?>(this) is not { } choice)
            {
                return;
            }

            exportPassword = choice.Password;
        }

        // Otherwise only usernames are read from saved credentials. If the vault stays locked, they're left out and the export says so.
        IReadOnlyDictionary<Guid, CredentialSummary>? credentials = null;
        ExportSecrets? secrets = null;
        bool unlocked = used.Count == 0 || await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this);
        if (exportPassword is not null && !unlocked)
        {
            return;
        }

        if (unlocked)
        {
            try
            {
                credentials = (await _credentials.ListAsync()).ToDictionary(c => c.Id);
                if (exportPassword is not null)
                {
                    var whole = new Dictionary<Guid, Credential>();
                    foreach (var id in used)
                    {
                        if (await _credentials.GetAsync(id) is { } credential)
                        {
                            whole[id] = credential;
                        }
                    }

                    secrets = new ExportSecrets(exportPassword, whole);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                if (exportPassword is not null)
                {
                    await Dialogs.ShowMessageAsync(this, "Couldn't read the saved passwords", $"{e.Message}\n\nNothing was exported.");
                    return;
                }

                credentials = null;
            }
        }

        try
        {
            // Encrypting is deliberately slow (Argon2id for Orrery, many PBKDF2 rounds for mRemoteNG); keep it off the UI thread.
            var result = await Task.Run(() =>
            {
                var exported = ConnectionExporter.Export(node, format, credentials, DateTimeOffset.Now, secrets);
                AtomicFile.WriteAllText(path, exported.Content);
                return exported;
            });
            StorageLog.LogInformation("Exported {Connections} connections as {Format} to {Path}, {Secrets} with passwords, {Warnings} warnings",
                result.Connections, format.ToString(), path, result.Secrets, result.Warnings.Count);
            var included = secrets is null ? "Passwords and keys aren't included."
                : format == ExportFormat.Orrery
                    ? $"{result.Secrets} saved credential{(result.Secrets == 1 ? " is" : "s are")} included. The whole file is encrypted with the password you chose."
                    : $"Passwords for {result.Secrets} connection{(result.Secrets == 1 ? " are" : "s are")} included, protected by the master password you chose.";
            var body = $"Saved to:\n{path}\n\n{included}";
            if (result.Warnings.Count > 0)
            {
                body += "\n\n" + string.Join("\n", result.Warnings.Take(10).Select(w => "• " + w))
                    + (result.Warnings.Count > 10 ? $"\n…and {result.Warnings.Count - 10} more" : "");
            }

            await Dialogs.ShowMessageAsync(this, $"Exported {result.Connections} connection{(result.Connections == 1 ? "" : "s")}", body);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StorageLog.LogWarning("Couldn't export to {Path}: {Message}", path, e.Message);
            await Dialogs.ShowMessageAsync(this, "Couldn't export", e.Message);
        }
    }

    // ---- Credentials ----

    async void OnLockVaultClick(object? sender, RoutedEventArgs e)
    {
        _properties.TrySave();
        _credentials.Lock();
        UpdateVaultButton();
        _credentialsView.ShowLocked();
        await _properties.ShowAsync(_properties.Node);
    }

    /// <summary>A saved connection opens with its current settings; one since deleted, or a typed host, opens directly.</summary>
    async Task OpenRecentAsync(RecentConnection recent)
    {
        if (recent.EntryId is { } id && _tree.Find(id) is ConnectionEntry entry)
        {
            await ConnectAsync(entry);
        }
        else
        {
            await ConnectDirectAsync(new QuickConnectChoice(null, recent.Host, recent.Port, recent.Protocol, recent.Username));
        }
    }

    // ---- Settings ----

    void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (_settingsHost.IsVisible)
        {
            CloseSettings();
        }
        else
        {
            OpenSettings();
        }
    }

    /// <summary>Shows the settings page over the sessions and the inspector.</summary>
    void OpenSettings()
    {
        if (!_settingsHost.IsVisible)
        {
            _properties.TrySave();
            var view = new SettingsView(_location, _settingsStore, _credentials, () =>
            {
                _properties.TrySave();
                SaveNow();
                return Task.CompletedTask;
            }, ChooseImportFileAsync, () => ExportAsync(_tree.Root));
            view.CloseRequested += (_, _) => CloseSettings();
            view.FullScreenShortcutChanged += (_, shortcut) =>
            {
                _fullScreenShortcut = shortcut;
                UpdateSessionToolbar();
            };
            view.HostStatusChecksChanged += (_, on) => _hostStatus.Enabled = on;
            view.RestartRequested += (_, arguments) => App.Restart(arguments);
            _settingsHost.Content = view;
            _settingsHost.IsVisible = true;

            // Only Settings is lit while it's open; the side panel underneath is covered.
            foreach (var (button, _) in _sides.Values)
            {
                button.Classes.Set("active", false);
            }

            _settingsRailButton.Classes.Set("active", true);
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => (_settingsHost.Content as Control)?.Focus(), Avalonia.Threading.DispatcherPriority.Background);
    }

    void CloseSettings()
    {
        _settingsHost.IsVisible = false;
        _settingsHost.Content = null;
        _settingsRailButton.Classes.Set("active", false);
        ShowSidePanel(_sidePanel.IsVisible, save: false);
        _currentTab?.View.Focus();
    }

    /// <summary>
    /// Offers to delete the old copy after Move data, but only when the new folder's pending-cleanup record names
    /// the same folder as <c>--moved-from</c> and that folder doesn't overlap the one in use.
    /// </summary>
    async Task OfferToDeleteOldCopyAsync(string oldDataDir, PendingCleanup? pending)
    {
        var problem = pending is null || !DataMover.SamePath(pending.OldDataDir, oldDataDir)
            ? "Move data didn't record it."
            : DataMover.CheckOldCopy(oldDataDir, _location.DataDir);
        if (problem is not null)
        {
            Log.For("App").LogWarning("Not offering to delete {OldDataDir}: {Problem}", oldDataDir, problem);
            return;
        }

        if (!await Dialogs.ConfirmAsync(this, "Delete the old copy of your data?",
                $"Orrery now uses\n{_location.DataDir}\n\nThe old copy is still in\n{oldDataDir}\n\nOnly Orrery's own files are deleted. Anything else in that folder stays.", "Delete old copy"))
        {
            return;
        }

        IReadOnlyList<string> left;
        try
        {
            left = DataMover.DeleteOldCopy(oldDataDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't delete all of the old copy", $"{e.Message}\n\nWhat's left is in {oldDataDir}.");
            return;
        }

        if (left.Count > 0)
        {
            await Dialogs.ShowMessageAsync(this, "Some files were left in the old folder",
                $"{left.Count} {(left.Count == 1 ? "file or folder that wasn't Orrery's was" : "files or folders that weren't Orrery's were")} left in place in\n{oldDataDir}");
        }
    }

    void UpdateVaultButton() => _lockVault.IsVisible = _credentials is VaultFileStore && !_credentials.IsLocked;
}
