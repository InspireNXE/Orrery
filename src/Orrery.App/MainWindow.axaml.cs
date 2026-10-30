using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
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

namespace Orrery.App;

public partial class MainWindow : Window
{
    const double TabHeaderHeight = 48;
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

    /// <param name="movedFrom">The previous data folder, when Orrery restarted after Move data.</param>
    public MainWindow(DataLocation location, string? movedFrom = null)
    {
        AvaloniaXamlLoader.Load(this);
        _location = location;
        _movedFrom = movedFrom;
        _settingsStore = AppSettingsStore.In(location);

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

        _hostKeyValidator = new HostKeyValidator(
            new JsonCertificateTrustStore(location.FileIn("known-hosts.json")),
            prompt => Dispatcher.UIThread.InvokeAsync(
                () => new HostKeyPromptWindow(prompt).ShowDialog<CertificateTrustDecision>(this)));

        _credentials = CredentialStores.For(location, _settingsStore.Load());
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
        else if (e.Key == Key.OemComma && e.KeyModifiers == modifier)
        {
            e.Handled = true;
            _ = OpenSettingsAsync();
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
        else if (choice?.Host is not null)
        {
            await ConnectDirectAsync(choice);
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
        var protocol = resolved.Protocol.Value;
        if (protocol is not (Protocol.Rdp or Protocol.Ssh))
        {
            await Dialogs.ShowMessageAsync(this, $"{ProtocolInfo.Label(protocol)} isn't supported yet",
                "Orrery can open RDP and SSH connections for now. VNC and Telnet are on the way.");
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
            if (protocol == Protocol.Ssh)
            {
                await StartSshAsync(entry.Name, target, password, saved);
            }
            else
            {
                StartRdp(entry.Name, target, password);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't read the saved credential", e.Message);
        }
    }

    async Task ConnectDirectAsync(QuickConnectChoice choice)
    {
        var host = choice.Host!;
        var prompt = await new ConnectPromptWindow(_credentials, host, $"{host}:{choice.Port}", choice.Username, null, offerRemember: false)
            .ShowDialog<ConnectPromptResult?>(this);
        if (prompt is null)
        {
            return;
        }

        var target = new ConnectionTarget(host, choice.Port, prompt.Username, prompt.Domain);
        if (choice.Protocol == Protocol.Ssh)
        {
            var saved = prompt.CredentialId is { } id ? await _credentials.GetAsync(id) : null;
            await StartSshAsync(host, target, prompt.Password, saved);
        }
        else
        {
            StartRdp(host, target, prompt.Password);
        }
    }

    void StartRdp(string title, ConnectionTarget target, string password)
    {
        bool zoom = _settingsStore.Load().ZoomRemoteDesktops;
        var (width, height) = DesktopSizeForView(zoom);
        var session = RdpSession.Start(target, password, width, height, _certificateValidator);
        AddTab(new RemoteFrameView(session, zoom), session, title);
        UpdateVaultButton();
    }

    /// <summary>SSH needs a username; a saved SSH-key credential logs in with its key instead of the password.</summary>
    async Task StartSshAsync(string title, ConnectionTarget target, string password, Credential? saved)
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
        AddTab(new TerminalView(session), session, title);
        UpdateVaultButton();
    }

    void AddTab(Control view, IConnectionSession session, string title)
    {
        var header = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(header, $"{session.Target.Username}{(session.Target.Username is null ? "" : "@")}{session.Target.Host}:{session.Target.Port}");
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
            Tag = session,
        };

        close.Click += async (_, _) =>
        {
            _sessions.Items.Remove(tab);
            UpdateEmptyState();
            await session.DisposeAsync();
        };

        _sessions.Items.Add(tab);
        _sessions.SelectedItem = tab;
        UpdateEmptyState();
    }

    /// <summary>
    /// The starting desktop size, in remote pixels, filling the session area. The view corrects it with
    /// a resize once the tab is laid out.
    /// </summary>
    (int Width, int Height) DesktopSizeForView(bool zoom)
    {
        var bounds = _sessions.Bounds;
        var (fullWidth, fullHeight) = RemoteFrameView.DesktopSize(new Size(bounds.Width, bounds.Height - TabHeaderHeight), RenderScaling, zoom);
        int width = Math.Max(640, fullWidth);
        int height = Math.Max(480, fullHeight);

        // Many RDP servers need the width to be a multiple of 4.
        return (width - width % 4, height);
    }

    IEnumerable<IConnectionSession> OpenSessions() =>
        _sessions.Items.OfType<TabItem>().Select(t => t.Tag).OfType<IConnectionSession>();

    void UpdateEmptyState() => _emptyState.IsVisible = _sessions.Items.Count == 0;

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
        var storeName = _credentials is VaultFileStore ? "your vault" : "Windows Credential Manager";
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
