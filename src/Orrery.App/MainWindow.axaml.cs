using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Credentials;
using Orrery.App.Security;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Orrery.Protocols.Rdp;

namespace Orrery.App;

public partial class MainWindow : Window
{
    const double TabHeaderHeight = 48;
    static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("orrery-node");
    static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    readonly ConnectionStore _connectionStore;
    readonly ConnectionTree _tree;
    readonly ICredentialStore _credentials;
    readonly ServerCertificateValidator _certificateValidator;
    readonly PropertiesPanel _properties;
    readonly DispatcherTimer _saveTimer;
    readonly string? _startupWarning;

    readonly TreeView _treeView;
    readonly TextBox _filter;
    readonly TextBlock _treeEmptyState;
    readonly TabControl _sessions;
    readonly TextBlock _emptyState;
    readonly Button _lockVault;
    readonly MenuItem _connectMenuItem, _deleteMenuItem;

    bool _rebuilding;
    Point? _dragStart;
    NodeViewModel? _dragRow;

    // For the XAML previewer only.
    public MainWindow() : this(DataLocationResolver.ForPortable(Path.GetTempPath(), PlatformFolders.ForCurrentOs(), PortableDataChoice.WithApp))
    {
    }

    public MainWindow(DataLocation location)
    {
        AvaloniaXamlLoader.Load(this);

        var storageText = this.FindControl<TextBlock>("StorageText")!;
        storageText.Text = $"{(location.Mode == StorageMode.Portable ? "Portable" : "Installed")} · {location.DataDir}";
        ToolTip.SetTip(storageText, location.DataDir);

        _treeView = this.FindControl<TreeView>("Tree")!;
        _filter = this.FindControl<TextBox>("FilterBox")!;
        _treeEmptyState = this.FindControl<TextBlock>("TreeEmptyState")!;
        _sessions = this.FindControl<TabControl>("Sessions")!;
        _emptyState = this.FindControl<TextBlock>("EmptyState")!;
        _lockVault = this.FindControl<Button>("LockVaultButton")!;
        _connectMenuItem = this.FindControl<MenuItem>("ConnectMenuItem")!;
        _deleteMenuItem = this.FindControl<MenuItem>("DeleteMenuItem")!;

        _certificateValidator = new ServerCertificateValidator(
            new JsonCertificateTrustStore(location.FileIn("trusted-certificates.json")),
            prompt => Dispatcher.UIThread.InvokeAsync(
                () => new CertificatePromptWindow(prompt).ShowDialog<CertificateTrustDecision>(this)));

        _credentials = CredentialStores.For(location);
        if (_credentials is VaultFileStore vault)
        {
            vault.AutoLocked += (_, _) => Dispatcher.UIThread.Post(UpdateVaultButton);
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
        this.FindControl<Border>("PropertiesHost")!.Child = _properties;

        WireTree();
        RebuildTree();
        UpdateVaultButton();

        KeyDown += OnWindowKeyDown;
        Opened += async (_, _) =>
        {
            if (_startupWarning is not null)
            {
                await Dialogs.ShowMessageAsync(this, "Your connections couldn't be loaded", _startupWarning);
            }
        };

        Closing += (_, _) =>
        {
            _properties.TrySave();
            SaveNow();
            foreach (var view in OpenViews())
            {
                _ = view.Session.DisposeAsync().AsTask();
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
            e.DragEffects = DraggedNode(e) is { } node && DropTarget(e, node) is not null ? DragDropEffects.Move : DragDropEffects.None;
        });

        _treeView.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (DraggedNode(e) is { } node && DropTarget(e, node) is { } target)
            {
                _properties.TrySave();
                _tree.Move(node, target.Parent, target.Index);
                Select(node.Id);
            }
        });
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
    }

    async void OnQuickConnectClick(object? sender, RoutedEventArgs e) => await OpenQuickConnectAsync();

    async Task OpenQuickConnectAsync()
    {
        var choice = await new QuickConnectWindow(_tree).ShowDialog<QuickConnectChoice?>(this);
        if (choice?.Entry is { } entry)
        {
            Select(entry.Id);
            await ConnectAsync(entry);
        }
        else if (choice?.Host is { } host)
        {
            await ConnectDirectAsync(host, choice.Port);
        }
    }

    async Task ConnectAsync(ConnectionEntry entry)
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
        if (resolved.Protocol.Value != Protocol.Rdp)
        {
            await Dialogs.ShowMessageAsync(this, $"{ProtocolInfo.Label(resolved.Protocol.Value)} isn't supported yet",
                "Orrery can only open RDP connections for now. SSH and VNC are on the way.");
            return;
        }

        try
        {
            string? username = resolved.Username.Value, domain = resolved.Domain.Value, password = null;
            if (resolved.CredentialId.Value is { } id)
            {
                if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
                {
                    return;
                }

                if (await _credentials.GetAsync(id) is { } credential)
                {
                    (username, domain, password) = (credential.Username, credential.Domain, credential.Secret);
                }
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
            }

            StartSession(entry.Name, new ConnectionTarget(entry.Host, resolved.Port.Value, username, domain), password);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't read the saved credential", e.Message);
        }
    }

    async Task ConnectDirectAsync(string host, int port)
    {
        var prompt = await new ConnectPromptWindow(_credentials, host, $"{host}:{port}", null, null, offerRemember: false)
            .ShowDialog<ConnectPromptResult?>(this);
        if (prompt is not null)
        {
            StartSession(host, new ConnectionTarget(host, port, prompt.Username, prompt.Domain), prompt.Password);
        }
    }

    void StartSession(string title, ConnectionTarget target, string password)
    {
        var (width, height) = DesktopSizeForView();
        var session = RdpSession.Start(target, password, width, height, _certificateValidator);
        AddTab(new RemoteFrameView(session), title);
        UpdateVaultButton();
    }

    void AddTab(RemoteFrameView view, string title)
    {
        var header = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(header, $"{view.Session.Target.Host}:{view.Session.Target.Port}");
        var close = new Button
        {
            Content = "✕",
            FontSize = 11,
            Padding = new Thickness(6, 2),
            Background = null,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(close, "Disconnect and close");

        var tab = new TabItem
        {
            Header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { header, close } },
            Content = view,
        };

        close.Click += async (_, _) =>
        {
            _sessions.Items.Remove(tab);
            UpdateEmptyState();
            await view.Session.DisposeAsync();
        };

        _sessions.Items.Add(tab);
        _sessions.SelectedItem = tab;
        UpdateEmptyState();
    }

    /// <summary>
    /// The starting desktop size, in device pixels, filling the session area. The view corrects it with
    /// a resize once the tab is laid out.
    /// </summary>
    (int Width, int Height) DesktopSizeForView()
    {
        var bounds = _sessions.Bounds;
        double scale = RenderScaling;
        int width = Math.Max(640, (int)(bounds.Width * scale));
        int height = Math.Max(480, (int)((bounds.Height - TabHeaderHeight) * scale));

        // Many RDP servers need the width to be a multiple of 4.
        return (width - width % 4, height);
    }

    IEnumerable<RemoteFrameView> OpenViews() =>
        _sessions.Items.OfType<TabItem>().Select(t => t.Content).OfType<RemoteFrameView>();

    void UpdateEmptyState() => _emptyState.IsVisible = _sessions.Items.Count == 0;

    // ---- Credentials ----

    async void OnManageCredentialsClick(object? sender, RoutedEventArgs e)
    {
        if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
        {
            return;
        }

        await new CredentialsWindow(_credentials).ShowDialog(this);
        UpdateVaultButton();
        await _properties.ShowAsync(_properties.Node);
    }

    async void OnLockVaultClick(object? sender, RoutedEventArgs e)
    {
        _properties.TrySave();
        _credentials.Lock();
        UpdateVaultButton();
        await _properties.ShowAsync(_properties.Node);
    }

    void UpdateVaultButton() => _lockVault.IsVisible = _credentials is VaultFileStore && !_credentials.IsLocked;
}
