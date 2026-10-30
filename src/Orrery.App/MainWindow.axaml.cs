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
using Orrery.App.Connections;
using Orrery.App.Credentials;
using Orrery.App.Import;
using Orrery.Core.Import;
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
    readonly Button _importDropZone;
    readonly Dictionary<string, (Button RailButton, Control View)> _sides;
    readonly RecentConnections _recent;
    readonly RecentPanel _recentPanel;
    readonly CredentialsView _credentialsView;
    string _side = "connections";
    readonly Button _lockVault;
    readonly MenuItem _connectMenuItem, _duplicateMenuItem, _deleteMenuItem;
    readonly List<SessionTab> _tabs = [];
    SessionTab? _currentTab;

    bool _rebuilding;
    Point? _dragStart;
    NodeViewModel? _dragRow;

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
        _importDropZone = this.FindControl<Button>("ImportDropZone")!;
        _dimmer = this.FindControl<Border>("Dimmer")!;
        _lockVault = this.FindControl<Button>("LockVaultButton")!;
        _connectMenuItem = this.FindControl<MenuItem>("ConnectMenuItem")!;
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
        if (_credentials is VaultFileStore vault)
        {
            vault.AutoLocked += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                UpdateVaultButton();
                _credentialsView?.ShowLocked();
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

        _properties = new PropertiesPanel(_tree, _credentials);
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

        WireTree();
        WireTitleBar();
        RebuildTree();
        UpdateVaultButton();
        UpdateSessionChrome();

        KeyDown += OnWindowKeyDown;
        Opened += async (_, _) =>
        {
            FitTitleBarToCaptionButtons();
            if (_startupWarning is not null)
            {
                await Dialogs.ShowMessageAsync(this, "Your connections couldn't be loaded", _startupWarning);
            }
            else if (_movedFrom is not null)
            {
                await OfferToDeleteOldCopyAsync(_movedFrom);
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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

        _treeView.DoubleTapped += async (_, e) =>
        {
            if (RowAt(e.Source)?.Node is ConnectionEntry entry)
            {
                await ConnectAsync(entry);
            }
        };

        _treeView.KeyDown += async (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Delete when SelectedNode is { } node:
                    e.Handled = true;
                    await DeleteAsync(node);
                    break;
                case Key.D when e.KeyModifiers == (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control) && SelectedNode is { } original:
                    e.Handled = true;
                    Duplicate(original);
                    break;
                case Key.F2 when SelectedNode is not null:
                    e.Handled = true;
                    _properties.FocusName();
                    break;
                case Key.Enter when SelectedNode is ConnectionEntry entry:
                    e.Handled = true;
                    await ConnectAsync(entry);
                    break;
            }
        };

        _treeView.ContextMenu!.Opening += (_, _) =>
        {
            _connectMenuItem.IsEnabled = SelectedNode is ConnectionEntry;
            _duplicateMenuItem.IsEnabled = SelectedNode is not null;
            _deleteMenuItem.IsEnabled = SelectedNode is not null;
        };

        // Drag a row onto a folder to move it in, or onto a connection to drop it before or after.
        _treeView.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(_treeView).Properties.IsLeftButtonPressed)
            {
                _dragStart = e.GetPosition(_treeView);
                _dragRow = RowAt(e.Source);
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
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DragFormat, row.Node.Id.ToString()));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _treeView.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel, handledEventsToo: true);

        _treeView.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = DroppedImportFile(e) is not null ? DragDropEffects.Copy
                : DraggedNode(e) is { } node && DropTarget(e, node) is not null ? DragDropEffects.Move
                : DragDropEffects.None;
        });

        _treeView.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (DroppedImportFile(e) is { } file)
            {
                await ImportAsync(file);
            }
            else if (DraggedNode(e) is { } node && DropTarget(e, node) is { } target)
            {
                _properties.TrySave();
                _tree.Move(node, target.Parent, target.Index);
                Select(node.Id);
            }
        });
    }

    // ---- Window chrome ----

    void WireTitleBar()
    {
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

        _importDropZone.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            bool importable = DroppedImportFile(e) is not null;
            e.DragEffects = importable ? DragDropEffects.Copy : DragDropEffects.None;
            _importDropZone.Classes.Set("dragover", importable);
        });
        _importDropZone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => _importDropZone.Classes.Set("dragover", false));
        _importDropZone.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            _importDropZone.Classes.Set("dragover", false);
            if (DroppedImportFile(e) is { } file)
            {
                await ImportAsync(file);
            }
        });
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

        if (side == _side && _sidePanel.IsVisible)
        {
            _sidePanel.IsVisible = false;
            _sides[side].RailButton.Classes.Set("active", false);
            return;
        }

        _properties.TrySave();
        _side = side;
        _sidePanel.IsVisible = true;
        foreach (var (name, (button, view)) in _sides)
        {
            button.Classes.Set("active", name == side);
            view.IsVisible = name == side;
        }

        _propertiesHost.Child = side == "credentials" ? _credentialsView.Editor : _properties;
        switch (side)
        {
            case "credentials":
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

    ConnectionNode? SelectedNode => (_treeView.SelectedItem as NodeViewModel)?.Node;

    static NodeViewModel? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as NodeViewModel;

    ConnectionNode? DraggedNode(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(DragFormat) is { } id && Guid.TryParse(id, out var guid) ? _tree.Find(guid) : null;

    (ConnectionFolder Parent, int? Index)? DropTarget(DragEventArgs e, ConnectionNode dragged)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (item?.DataContext is not NodeViewModel target)
        {
            return (_tree.Root, null);
        }

        if (target.Node == dragged)
        {
            return null;
        }

        if (target.Node is ConnectionFolder folder)
        {
            return IsInside(folder, dragged) ? null : (folder, null);
        }

        var parent = target.Node.Parent!;
        if (IsInside(parent, dragged))
        {
            return null;
        }

        bool after = e.GetPosition(item).Y > item.Bounds.Height / 2;
        return (parent, parent.Children.ToList().IndexOf(target.Node) + (after ? 1 : 0));
    }

    /// <summary>True if <paramref name="folder"/> is <paramref name="dragged"/> or inside it.</summary>
    static bool IsInside(ConnectionFolder folder, ConnectionNode dragged) =>
        dragged is ConnectionFolder draggedFolder && (folder == draggedFolder || folder.Ancestors().Contains(draggedFolder));

    void RebuildTree()
    {
        var selected = SelectedNode?.Id;
        _rebuilding = true;
        try
        {
            _treeView.ItemsSource = NodeViewModel.Build(_tree.Root, _filter.Text, ScheduleSave);
            _treeView.SelectedItem = selected is { } id ? FindRow(id) : null;
        }
        finally
        {
            _rebuilding = false;
        }

        _treeEmptyState.IsVisible = _tree.Root.Children.Count == 0;
        UpdateTreeStatus();
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

    void AddAndEdit(ConnectionNode node)
    {
        _properties.TrySave();
        var parent = TargetFolder();
        parent.Expanded = true;
        _filter.Text = string.Empty;
        _tree.Add(node, parent);
        Select(node.Id);
        Dispatcher.UIThread.Post(_properties.FocusName, DispatcherPriority.Background);
    }

    async void OnConnectMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is ConnectionEntry entry)
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

    void OnDuplicateMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is { } node)
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
        if (SelectedNode is { } node)
        {
            await DeleteAsync(node);
        }
    }

    async Task DeleteAsync(ConnectionNode node)
    {
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
            _ = OpenSettingsAsync();
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
            choice = await new QuickConnectWindow(_tree).ShowDialog<QuickConnectChoice?>(this);
        }
        finally
        {
            _dimmer.IsVisible = false;
        }

        if (choice?.NewName is { } name)
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
    async Task ConnectAsync(ConnectionEntry entry, bool skipAgent = false)
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
        var origin = new SessionOrigin(entry.Name, protocol, entry.Id, () => ConnectAsync(entry));
        // Telnet has no login of its own: the server asks in the terminal.
        if (protocol == Protocol.Telnet)
        {
            StartTelnet(origin, new ConnectionTarget(entry.Host, resolved.Port.Value, resolved.Username.Value, null));
            return;
        }

        try
        {
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
                }
            }

            // SSH with no saved credential: the agent's keys, if it has any, without asking for a password.
            if (password is null && protocol == Protocol.Ssh && resolved.UseSshAgent.Value && !skipAgent
                && !string.IsNullOrWhiteSpace(username) && await AgentHasKeysAsync())
            {
                StartSshWithAgent(origin, new ConnectionTarget(entry.Host, resolved.Port.Value, username, domain),
                    whenRefused: () => ConnectAsync(entry, skipAgent: true));
                return;
            }

            if (password is null)
            {
                var prompt = await new ConnectPromptWindow(_credentials, entry.Name, $"{entry.Host}:{resolved.Port.Value}", username, domain, offerRemember: true)
                    .ShowDialog<ConnectPromptResult?>(this);
                if (prompt is null)
                {
                    return;
                }

                if (prompt is { RememberForConnection: true, CredentialId: { } chosen })
                {
                    entry.Settings.CredentialId = chosen;
                    _tree.NotifyChanged();
                }

                (username, domain, password) = (prompt.Username, prompt.Domain, prompt.Password);
                if (prompt.CredentialId is { } chosenId && await _credentials.GetAsync(chosenId) is { } chosenCredential)
                {
                    saved = chosenCredential;
                }
            }

            var target = new ConnectionTarget(entry.Host, resolved.Port.Value, username, domain);
            await StartSessionAsync(origin, target, password, saved, resolved.ShareClipboard.Value);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't read the saved credential", e.Message);
        }
    }

    async Task ConnectDirectAsync(QuickConnectChoice choice, bool skipAgent = false)
    {
        var host = choice.Host!;
        var origin = new SessionOrigin(host, choice.Protocol, null, () => ConnectDirectAsync(choice));
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
        bool shareClipboard = true)
    {
        switch (origin.Protocol)
        {
            case Protocol.Ssh:
                return StartSshAsync(origin, target, password, saved);
            case Protocol.Vnc:
                StartVnc(origin, target, password, shareClipboard);
                return Task.CompletedTask;
            default:
                StartRdp(origin, target, password, shareClipboard);
                return Task.CompletedTask;
        }
    }

    /// <summary>VNC servers set their own desktop size; the view shrinks it to fit and zooms it like RDP.</summary>
    void StartVnc(SessionOrigin origin, ConnectionTarget target, string password, bool shareClipboard)
    {
        var session = VncSession.Start(target, password, shareClipboard);
        AddTab(new RemoteFrameView(session, _settingsStore.Load().ZoomRemoteDesktops), session, origin);
        UpdateVaultButton();
    }

    void StartRdp(SessionOrigin origin, ConnectionTarget target, string password, bool shareClipboard)
    {
        bool zoom = _settingsStore.Load().ZoomRemoteDesktops;
        var (width, height) = DesktopSizeForView(zoom);
        var session = RdpSession.Start(target, password, width, height, _certificateValidator, shareClipboard);
        AddTab(new RemoteFrameView(session, zoom), session, origin);
        UpdateVaultButton();
    }

    void StartTelnet(SessionOrigin origin, ConnectionTarget target)
    {
        var session = TelnetSession.Start(target, 80, 24);
        AddTab(new TerminalView(session) { Margin = TerminalMargin }, session, origin);
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
            System.Diagnostics.Trace.TraceWarning($"Couldn't reach the SSH agent: {e.Message}");
            return false;
        }
    });

    /// <summary>
    /// Logs in with the agent's keys alone. If the server refuses them all, the tab closes and
    /// <paramref name="whenRefused"/> asks for a login the usual way.
    /// </summary>
    void StartSshWithAgent(SessionOrigin origin, ConnectionTarget target, Func<Task> whenRefused)
    {
        var session = SshSession.Start(target, new SshLogin(target.Username!, null, UseAgent: true), _hostKeyValidator, 80, 24);
        session.StateChanged += (_, e) =>
        {
            if (e.State == SessionState.Failed && session.AuthenticationRejected)
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await CloseTabAsync(session);
                    await whenRefused();
                });
            }
        };
        AddTab(new TerminalView(session) { Margin = TerminalMargin }, session, origin);
    }

    /// <summary>SSH needs a username; a saved SSH-key credential logs in with its key instead of the password.</summary>
    async Task StartSshAsync(SessionOrigin origin, ConnectionTarget target, string password, Credential? saved)
    {
        if (string.IsNullOrWhiteSpace(target.Username))
        {
            await Dialogs.ShowMessageAsync(this, "SSH needs a username", "Set a username on the connection or its saved credential, then connect again.");
            return;
        }

        var login = saved?.Kind is CredentialKind.SshKey or CredentialKind.SshKeyWithPassphrase
            ? new SshLogin(target.Username, null, saved.Secret, saved.Passphrase)
            : new SshLogin(target.Username, password);

        var session = SshSession.Start(target, login, _hostKeyValidator, 80, 24);
        AddTab(new TerminalView(session) { Margin = TerminalMargin }, session, origin);
        UpdateVaultButton();
    }

    // ---- Session tabs ----

    void AddTab(Control view, IConnectionSession session, SessionOrigin origin)
    {
        var tab = new SessionTab(session, view, origin);
        tab.Selected += (_, _) => SelectTab(tab);
        tab.CloseRequested += async (_, _) => await CloseTabAsync(session);
        tab.StateChanged += (_, _) =>
        {
            UpdateTreeStatus();
            if (tab == _currentTab)
            {
                UpdateSessionToolbar();
            }
        };

        _recent.Add(new RecentConnection(origin.EntryId, origin.Title, origin.Protocol,
            session.Target.Host, session.Target.Port, session.Target.Username, DateTimeOffset.Now));
        _tabs.Add(tab);
        _tabStrip.Children.Add(tab.Header);
        _sessionHost.Children.Add(view);
        SelectTab(tab);
        UpdateSessionChrome();
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
            int index = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            _tabStrip.Children.Remove(tab.Header);
            _sessionHost.Children.Remove(tab.View);
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
        _sessionToolbar.IsVisible = _currentTab is not null;
        _sessionActions.Children.Clear();
        if (_currentTab is not { } tab)
        {
            return;
        }

        var session = tab.Session;
        var state = session.State;
        _sessionAddress.Text = tab.Address;
        _sessionLock.Foreground = (IBrush?)this.FindResource(state == SessionState.Connected ? "OrreryConnected" : "OrreryMuted");
        _sessionDetail.Text = $"{ProtocolInfo.Label(tab.Origin.Protocol)} · " + state switch
        {
            SessionState.Connecting => "Connecting…",
            SessionState.Disconnected => "Disconnected",
            SessionState.Failed => "Couldn't connect",
            _ when session is IFrameBufferSession frame && frame.Width > 0 => $"{frame.Width}×{frame.Height}",
            _ => "Connected",
        };

        if (state == SessionState.Connected && session is IFrameBufferSession desktop)
        {
            var ctrlAltDel = new Button { Content = "Ctrl+Alt+Del" };
            ToolTip.SetTip(ctrlAltDel, "Send Ctrl+Alt+Del to the remote computer");
            ctrlAltDel.Click += (_, _) =>
            {
                SendCtrlAltDel(desktop);
                tab.View.Focus();
            };
            _sessionActions.Children.Add(ctrlAltDel);
        }

        if (state is SessionState.Connecting or SessionState.Connected)
        {
            var disconnect = new Button { Content = "Disconnect" };
            disconnect.Classes.Add("danger");
            disconnect.Click += async (_, _) => await session.DisconnectAsync();
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

    /// <summary>Presses Ctrl, Alt and Delete together on the remote computer, then lets them go.</summary>
    static void SendCtrlAltDel(IFrameBufferSession desktop)
    {
        if (desktop is IKeysymInput keysyms)
        {
            uint[] keys = [0xFFE3, 0xFFE9, 0xFFFF]; // Control_L, Alt_L, Delete
            foreach (var key in keys) keysyms.SendKeysym(key, true);
            foreach (var key in keys.Reverse()) keysyms.SendKeysym(key, false);
        }
        else
        {
            ushort[] keys = [0x1D, 0x38, 0xE053]; // Ctrl, Alt, Delete (extended)
            foreach (var key in keys) desktop.SendKey(key, true);
            foreach (var key in keys.Reverse()) desktop.SendKey(key, false);
        }
    }

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

    // ---- mRemoteNG import ----

    static string? DroppedImportFile(DragEventArgs e) =>
        e.DataTransfer.TryGetFile()?.TryGetLocalPath() is { } path && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? path : null;

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

    async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Import from mRemoteNG",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("mRemoteNG connections") { Patterns = ["*.xml"] }],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await ImportAsync(path);
        }
    }

    async Task ImportAsync(string path)
    {
        ImportResult? result = null;
        string? masterPassword = null, passwordError = null;
        while (result is null)
        {
            try
            {
                var password = masterPassword;
                result = await Task.Run(() => MRemoteNgImporter.ImportFile(path, password));
            }
            catch (MasterPasswordRequiredException e)
            {
                passwordError = e.WrongPassword ? "That password is incorrect." : null;
                masterPassword = await Dialogs.PromptPasswordAsync(this, "Master password",
                    "This mRemoteNG file is protected with a master password. Enter it to read the connections and saved passwords.", passwordError);
                if (masterPassword is null)
                {
                    return;
                }
            }
            catch (Exception e) when (e is ImportException or IOException or UnauthorizedAccessException)
            {
                await Dialogs.ShowMessageAsync(this, "Couldn't import this file", e.Message);
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

                foreach (var credential in result.Credentials)
                {
                    await _credentials.SaveAsync(credential);
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
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

        var reportPath = WriteImportReport(result.Report);
        await Dialogs.ShowMessageAsync(this, $"Imported {result.Report.Connections} connection{(result.Report.Connections == 1 ? "" : "s")}",
            reportPath is null ? "The import is complete." : $"A summary of what was and wasn't imported is saved at:\n{reportPath}");
    }

    string? WriteImportReport(ImportReport report)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var path = Path.Combine(_location.DataDir, "import", $"mremoteng-{now:yyyyMMdd-HHmmss}.md");
            AtomicFile.WriteAllText(path, report.ToMarkdown(now));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
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

    async void OnSettingsClick(object? sender, RoutedEventArgs e) => await OpenSettingsAsync();

    async Task OpenSettingsAsync()
    {
        var window = new SettingsWindow(_location, _settingsStore, _credentials, () =>
        {
            _properties.TrySave();
            SaveNow();
            return Task.CompletedTask;
        });

        if (await window.ShowDialog<SettingsOutcome?>(this) is SettingsOutcome.Restart restart)
        {
            App.Restart(restart.Arguments);
        }
    }

    async Task OfferToDeleteOldCopyAsync(string oldDataDir)
    {
        if (!Directory.Exists(oldDataDir) || DataMover.SamePath(oldDataDir, _location.DataDir)
            || !await Dialogs.ConfirmAsync(this, "Delete the old copy of your data?",
                $"Orrery now uses\n{_location.DataDir}\n\nThe old copy is still in\n{oldDataDir}", "Delete old copy"))
        {
            return;
        }

        try
        {
            DataMover.DeleteOldCopy(oldDataDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't delete all of the old copy", $"{e.Message}\n\nWhat's left is in {oldDataDir}.");
        }
    }

    void UpdateVaultButton() => _lockVault.IsVisible = _credentials is VaultFileStore && !_credentials.IsLocked;
}
