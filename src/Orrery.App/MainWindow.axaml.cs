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
using Orrery.App.Updates;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Orrery.Protocols.Local;
using Orrery.Protocols.Rdp;
using Orrery.Protocols.Serial;
using Orrery.Protocols.Ssh;
using Orrery.Protocols.Ssh.Agent;
using Orrery.Protocols.Telnet;
using Orrery.Protocols.Vnc;

namespace Orrery.App;

public partial class MainWindow : Window, IFullScreenHost
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
    readonly string? _openOnStart;
    readonly IReadOnlyList<string>? _importOnStart;

    // Setup's master password for a new vault, made as the window opens and then dropped.
    string? _newVaultPassword;
    readonly ConnectionStore _connectionStore;
    readonly ConnectionTree _tree;
    readonly ICredentialStore _credentials;
    readonly Recording.RecordingService _recordings;
    readonly ServerCertificateValidator _certificateValidator;
    readonly HostKeyValidator _hostKeyValidator;
    readonly PropertiesPanel _properties;
    readonly DispatcherTimer _saveTimer;
    readonly string? _startupWarning;

    // Other computers with Orrery open on this folder, as a sync app shares it; see DataFolderPresence.
    readonly DataFolderPresence _presence;

    // Changes to the shared files, as a sync app brings in another computer's saves; started once the window opens.
    DataFolderWatcher? _dataWatcher;

    readonly TreeView _treeView;
    readonly TextBox _filter;
    readonly TextBlock _treeEmptyState;
    readonly TabStripPanel _tabStrip;
    readonly Panel _sessionHost;
    readonly Control _emptyState;
    readonly TextBlock _emptyStateTitle;
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
    readonly StackPanel _fullScreenRecording;
    KeyGesture _fullScreenShortcut;

    // Full screen hides every pane and bar; these are what to put back.
    bool _fullScreen;
    WindowState _stateBeforeFullScreen;

    bool IFullScreenHost.IsFullScreen => _fullScreen;

    event EventHandler? FullScreenChanged;

    event EventHandler? IFullScreenHost.FullScreenChanged
    {
        add => FullScreenChanged += value;
        remove => FullScreenChanged -= value;
    }
    (PixelPoint Position, WindowState State)? _positionBeforeFullScreen;

    // The offer to reopen last time's sessions, set aside while a session is full screen.
    bool _restoreOfferBeforeFullScreen;

    /// <summary>While a remote desktop is spread over every display: its view, and the window as it was before.</summary>
    (RemoteFrameView View, PixelPoint Position, double Width, double Height, WindowDecorations Decorations, bool Topmost)? _spanned;
    readonly Button _settingsRailButton;
    readonly Dictionary<string, (Button RailButton, Control View)> _sides;
    readonly RecentConnections _recent;
    readonly ConnectionHistory _history;
    readonly RecentPanel _recentPanel;
    readonly CommandsPanel _commandsPanel;
    readonly CommandEditorPane _commandEditor;
    readonly Recording.RecordingsPanel _recordingsPanel;
    readonly ContentControl _recordingsHost;

    // The Recordings section is open, so the main area shows its player rather than the sessions.
    bool _recordingsShown;
    readonly CredentialsView _credentialsView;
    string _side = "connections";
    readonly Button _lockVault;
    readonly HostStatusController _hostStatus;
    readonly TextBlock _noticeText;
    int _noticeVersion;
    readonly MenuItem _connectMenuItem, _newSessionMenuItem, _browseFilesMenuItem, _wakeMenuItem, _managementMenuItem, _addGuestMenuItem, _moveOutOfHostMenuItem, _duplicateMenuItem, _exportMenuItem, _deleteMenuItem;
    readonly List<SessionTab> _tabs = [];

    // Settings: opening a connection that's already open starts another session rather than going to it.
    bool _newSessionOnOpen;
    SessionTab? _currentTab;
    readonly NetworkWatch _network = new();

    bool _rebuilding;
    Point? _dragStart;
    NodeViewModel? _dragRow;
    // The press a drag starts from: Avalonia starts the drag with it once the pointer has moved far enough.
    PointerPressedEventArgs? _dragPress;

    // A plain press on a row that's part of a multi-selection keeps the selection, so the rows can be dragged
    // together; released without a drag, the press selects just that row.
    NodeViewModel? _pressedInSelection;
    TreeViewItem? _dropRow;

    // For the XAML previewer only.
    public MainWindow() : this(DataLocationResolver.ForPortable(Path.GetTempPath(), PlatformFolders.ForCurrentOs(), PortableDataChoice.WithApp))
    {
    }

    /// <param name="movedFrom">The previous data folder, when Orrery restarted after Move data.</param>
    /// <param name="openOnStart">A Remote Desktop (.rdp) file given on the command line, connected to once the window opens.</param>
    /// <param name="importOnStart">
    /// Files first-launch setup chose to import, each through the usual preview once the window opens. Given at all, even
    /// empty, it stands in for the offer to import mRemoteNG's connections, which setup has already made.
    /// </param>
    /// <param name="updates">The update engine, which the window owns from here; null for no update checks at all.</param>
    /// <param name="newVaultPassword">
    /// The master password first-launch setup chose: the vault is made with it as the window opens, before the imports,
    /// so they don't ask for it again. Ignored when the store isn't a vault still to be made.
    /// </param>
    internal MainWindow(DataLocation location, string? movedFrom = null, string? openOnStart = null, IReadOnlyList<string>? importOnStart = null,
        IUpdateService? updates = null, string? newVaultPassword = null)
    {
        _newVaultPassword = newVaultPassword;
        AvaloniaXamlLoader.Load(this);
        _location = location;
        _movedFrom = movedFrom;
        _openOnStart = openOnStart;
        _importOnStart = importOnStart;
        _settingsStore = AppSettingsStore.In(location);
        _presence = new DataFolderPresence(location.DataDir, Environment.MachineName);
        Palette.Use(_settingsStore.Load().InterfaceColorScheme);
        Input.WindowsKeyHook.Shortcuts = _settingsStore.Load().SystemShortcutsGo;

        var storageText = this.FindControl<TextBlock>("StorageText")!;
        storageText.Text = location.DataDir;
        ToolTip.SetTip(storageText, location.DataDir);
        if (FolderAccess.IsOpenToOthers(location.DataDir))
        {
            // Said once, at start-up; it doesn't stop anything.
            var accessText = this.FindControl<Controls.Notice>("DataAccessText")!;
            accessText.Text = "Other people who use this computer can change the data folder";
            accessText.Message.FontSize = 11;
            accessText.IsVisible = true;
            ToolTip.SetTip(accessText, $"{location.DataDir}\n\nOrrery's saved hosts and trusted keys live here, so someone else could change them. "
                + "Settings › Move data can move them to a folder only you can use.");
        }
        this.FindControl<TextBlock>("ModeText")!.Text = location.Mode == StorageMode.Portable ? "Portable" : "Installed";

        // Elsewhere a shell elevates itself, with sudo.
        this.FindControl<MenuItem>("ElevatedTerminalMenuItem")!.IsVisible = OperatingSystem.IsWindows();
        if (OperatingSystem.IsMacOS())
        {
            this.FindControl<TextBlock>("QuickConnectShortcut")!.Text = "⌘K";
            this.FindControl<TextBlock>("EmptyStateLocal")!.Text = "Cmd+Shift+T opens a terminal on this computer.";
            ToolTip.SetTip(this.FindControl<Button>("LocalTerminalButton")!, "Local terminal (Cmd+Shift+T)");
        }

        _treeView = this.FindControl<TreeView>("Tree")!;
        _filter = this.FindControl<TextBox>("FilterBox")!;
        _treeEmptyState = this.FindControl<TextBlock>("TreeEmptyState")!;
        _tabStrip = this.FindControl<TabStripPanel>("TabStrip")!;
        _sessionHost = this.FindControl<Panel>("SessionHost")!;
        _emptyState = this.FindControl<Control>("EmptyState")!;
        _emptyStateTitle = this.FindControl<TextBlock>("EmptyStateTitle")!;
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
            ["commands"] = (this.FindControl<Button>("CommandsRailButton")!, this.FindControl<Control>("CommandsSide")!),
            ["recordings"] = (this.FindControl<Button>("RecordingsRailButton")!, this.FindControl<Control>("RecordingsSide")!),
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
        _fullScreenRecording = this.FindControl<StackPanel>("FullScreenRecording")!;
        _fullScreenShortcut = Input.FullScreenShortcut.FromSettings(_settingsStore.Load().FullScreenShortcut);
        _dimmer = this.FindControl<Border>("Dimmer")!;
        _lockVault = this.FindControl<Button>("LockVaultButton")!;
        _connectMenuItem = this.FindControl<MenuItem>("ConnectMenuItem")!;
        _newSessionMenuItem = this.FindControl<MenuItem>("NewSessionMenuItem")!;
        _browseFilesMenuItem = this.FindControl<MenuItem>("BrowseFilesMenuItem")!;
        _wakeMenuItem = this.FindControl<MenuItem>("WakeMenuItem")!;
        _managementMenuItem = this.FindControl<MenuItem>("ManagementMenuItem")!;
        _noticeText = this.FindControl<TextBlock>("NoticeText")!;
        _addGuestMenuItem = this.FindControl<MenuItem>("AddGuestMenuItem")!;
        _moveOutOfHostMenuItem = this.FindControl<MenuItem>("MoveOutOfHostMenuItem")!;
        _duplicateMenuItem = this.FindControl<MenuItem>("DuplicateMenuItem")!;
        _exportMenuItem = this.FindControl<MenuItem>("ExportMenuItem")!;
        _deleteMenuItem = this.FindControl<MenuItem>("DeleteMenuItem")!;

        _certificateValidator = new ServerCertificateValidator(
            new JsonCertificateTrustStore(location.FileIn("trusted-certificates.json")),
            prompt => Dispatcher.UIThread.InvokeAsync(
                () => AskAndRefocusAsync(new CertificatePromptWindow(prompt))));

        _hostKeyValidator = new HostKeyValidator(
            new JsonCertificateTrustStore(location.FileIn("known-hosts.json")),
            prompt => Dispatcher.UIThread.InvokeAsync(
                () => AskAndRefocusAsync(new HostKeyPromptWindow(prompt))));

        // Before any page engine opens it.
        WebData.MoveOldProfile(location.DataDir);
        ComputerFiles.TidyOldCache(location);
        if (WebData.ClearIfAsked(location.DataDir))
        {
            StorageLog.LogInformation("Cleared the web pages' data, as Settings asked");
        }

        _credentials = CredentialStores.For(location, _settingsStore.Load());
        _recordings = new Recording.RecordingService(location, _settingsStore, _credentials);
        StorageLog.LogInformation("Credentials are kept in {Store}", (_credentials as KeychainCredentialStore)?.BackendName ?? "the vault file");
        if (_credentials is VaultFileStore vault)
        {
            vault.AutoLocked += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await ShowVaultLockedAsync();
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

        _history = ConnectionHistory.In(location);
        _tree.Removed += (_, e) => _history.Forget(e.Ids);
        _properties = new PropertiesPanel(_tree, _credentials, () => _settingsStore.Load().ZoomRemoteDesktops, _history,
            webPagesInOrreryByDefault: () => _settingsStore.Load().OpensWebPagesInOrrery);
        _properties.ConnectRequested += async (_, entry) => await ConnectAsync(entry);
        _properties.SessionSettingsApplied += (_, _) => UpdateSessionToolbar();
        _properties.ReconnectRequested += async (_, tab) =>
        {
            await CloseTabAsync(tab.Session);
            await tab.Origin.Reconnect();
        };
        _inspectorContent.Content = _properties;
        _propertiesHost.Child = new Panel { Children = { _inspectorContent } };

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
        (_commandsPanel, _commandEditor) = CreateCommandsPage();
        this.FindControl<ContentControl>("CommandsSide")!.Content = _commandsPanel;
        _recordingsPanel = new Recording.RecordingsPanel(_recordings);
        this.FindControl<ContentControl>("RecordingsSide")!.Content = _recordingsPanel;
        _recordingsHost = this.FindControl<ContentControl>("RecordingsHost")!;
        _recordingsHost.Content = _recordingsPanel.Stage;

        // A web page is drawn in a window of its own above Orrery's, so it hides while anything is opened over it.
        foreach (var cover in new Control[] { _settingsHost, _dimmer, _recordingsHost, _sidePanel, _propertiesHost })
        {
            cover.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty || e.Property == ZIndexProperty)
                {
                    CoverWebPages();
                }
            };
        }

        _hostStatus = new HostStatusController(() => _treeView.ItemsSource as IEnumerable<NodeViewModel> ?? [],
            () => _sidePanel.IsVisible && _side == "connections" && WindowState != WindowState.Minimized);
        var startSettings = _settingsStore.Load();
        _hostStatus.Method = startSettings.HostCheckMethod;
        _hostStatus.Enabled = startSettings.ChecksHostStatus;
        RememberRecentConnections(startSettings.RemembersRecentConnections);
        _newSessionOnOpen = startSettings.OpensNewSession;

        WireTree();
        WireTitleBar();
        WireFullScreen();
        WireTabStrip();
        WirePanes();
        _workspaceStore = WorkspaceStore.In(location);
        _savedWorkspace = _workspaceStore.Load();
        InitWorkspace();
        InitUpdates(updates);
        RebuildTree();
        UpdateVaultButton();
        UpdateSessionChrome();

        KeyDown += OnWindowKeyDown;
        Opened += async (_, _) =>
        {
            // Checks straight away, in the background, then on the schedule in Settings.
            _updates?.Start();

            // In the background: a full folder can take a moment, and nothing waits on it.
            _ = Task.Run(_recordings.ApplyRetention);
            FitTitleBarToCaptionButtons();

            await CreateVaultFromSetupAsync();
            await WarnAboutSharedFolderAsync();
            WatchDataFolder();

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
            else if (_importOnStart is not null)
            {
                await ImportFilesAsync(_importOnStart);
            }
            else if (_openOnStart is null)
            {
                await OfferMRemoteNgImportAsync();
            }

            await OfferWorkspaceAsync();
            if (_openOnStart is not null)
            {
                await OpenRdpFileAsync(_openOnStart);
            }
        };

        _network.Back += (_, _) => NetworkBack();
        Closing += (_, _) =>
        {
            // Before the sessions go, so each file is closed properly rather than left as if Orrery had stopped.
            _recordings.StopAll(TimeSpan.FromSeconds(3));
            _network.Dispose();
            _properties.TrySave();
            SaveNow();
            SaveWorkspace();
            ClosePopouts();
            _hostStatus.Dispose();
            SessionLogger.CloseAll();
            foreach (var session in OpenSessions())
            {
                _ = session.DisposeAsync().AsTask();
            }

            (_credentials as IDisposable)?.Dispose();
            _presence.Dispose();
            _dataWatcher?.Dispose();

            // Last, once everything is saved: an update put off until now, then the engine and anything it was doing.
            if (!App.Restarting)
            {
                _updates?.FinishOnQuit();
            }

            _updates?.Dispose();
        };
    }

    /// <summary>
    /// Says, in the status bar, when another computer has Orrery open on this data folder (a sync app sharing it), now
    /// or later on: each takes in the other's changes, so it's only so nobody is surprised. Conflict copies the sync app
    /// left of Orrery's files need someone to look at them, so those are said in a window.
    /// </summary>
    async Task WarnAboutSharedFolderAsync()
    {
        IReadOnlyList<OtherComputer> others;
        IReadOnlyList<string> conflicts;
        try
        {
            others = await Task.Run(_presence.Start);
            conflicts = await Task.Run(() => SyncConflicts.Find(_location.DataDir, others.Select(o => o.Name)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("App").LogWarning(e, "Couldn't check for other computers using the data folder");
            return;
        }

        _presence.OtherOpened += other => Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
            {
                Log.For("App").LogInformation("Another computer ({Computer}) opened Orrery on this data folder", other.Tag);
                ShowNotice(OtherComputersNotice([other])!);
            }
        });

        if (OtherComputersNotice(others) is { } notice)
        {
            Log.For("App").LogInformation("Orrery is open on {Count} other computer(s) using this data folder: {Computers}",
                others.Count, string.Join(", ", others.Select(o => o.Tag)));
            ShowNotice(notice);
        }

        if (ConflictCopiesWarning(conflicts, _location.DataDir) is { } warning)
        {
            Log.For("App").LogWarning("The data folder has {Count} sync conflict copies", conflicts.Count);
            if (await Dialogs.AskAsync(this, "Your sync app made conflict copies", warning, "Open data folder", "Close"))
            {
                Settings.SettingsView.OpenFolder(_location.DataDir);
            }
        }
    }

    /// <summary>The status bar's line for other computers with Orrery open on this folder, or null for none.</summary>
    internal static string? OtherComputersNotice(IReadOnlyList<OtherComputer> others) => others.Count switch
    {
        0 => null,
        1 => $"Orrery is also open on {others[0].Name}. Changes on either show on both.",
        _ => $"Orrery is also open on {string.Join(", ", others.Select(o => o.Name))}. Changes on any show on all.",
    };

    /// <summary>What's said about the sync app's conflict copies in <paramref name="dataDir"/>, or null for none.</summary>
    internal static string? ConflictCopiesWarning(IReadOnlyList<string> conflicts, string dataDir) => conflicts.Count == 0 ? null
        : "Your sync app kept these copies when the same file changed on two computers before it could copy either "
          + "change. Orrery only reads the files under their own names, so anything in a copy isn't in Orrery. Compare "
          + "each copy with its file, then delete the copy."
          + (conflicts.Any(c => c.StartsWith("vault", StringComparison.OrdinalIgnoreCase))
              ? " A copy of the vault is encrypted, so it can't be compared that way: keep it until you've checked that no "
                + "saved credentials are missing."
              : "")
          + $"\n\nIn {dataDir}:\n" + string.Join("\n", conflicts.Select(c => "  " + c));

    // A sync app may be part way through writing the file, or holding it: tried again for about a second.
    const int TreeLoadAttempts = 4;
    static readonly TimeSpan TreeLoadRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// A damaged connections.json is moved aside, never overwritten, and Orrery starts with an empty tree. One a newer
    /// Orrery saved, or one that can't be opened just now, is left where it is: in a folder a sync app shares, moving
    /// it would take it from every computer, and the first to save would then have its few connections merged in as if
    /// the others had been deleted. The store doesn't save over either.
    /// </summary>
    internal static (ConnectionTree Tree, string? Warning) LoadTree(ConnectionStore store, DataLocation location)
    {
        var path = location.FileIn("connections.json");
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return (store.Load(), null);
            }
            catch (InvalidDataException e) when (ConnectionStore.IsNewerFile(e))
            {
                StorageLog.LogError("connections.json was saved by a newer Orrery; left as it is, and started empty");
                return (ConnectionTree.Empty(),
                    "connections.json was saved by a newer version of Orrery, on this computer or another that shares the data "
                    + "folder. This copy can't read it, so it shows no connections and leaves the file as it is. Update Orrery "
                    + $"to see them again.\n\nThe file is:\n{path}");
            }
            catch (Exception e) when (attempt < TreeLoadAttempts && e is JsonException or IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(TreeLoadRetryDelay);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                StorageLog.LogError("Couldn't open connections.json ({Message}); left as it is, and started empty", e.Message);
                return (ConnectionTree.Empty(),
                    "Orrery couldn't open connections.json, so it's starting with an empty list. The file is left as it is, and "
                    + "Orrery takes its connections in once it can open it. If a sync app is still downloading it, wait for "
                    + $"that, then restart Orrery.\n\nThe file is:\n{path}\n\nDetails: {e.Message}");
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException)
            {
                var aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
                string where;
                try
                {
                    File.Move(path, aside);
                    where = $"The file was moved to:\n{aside}";
                }
                catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException)
                {
                    aside = path;
                    where = $"It couldn't be moved aside, so it's still at:\n{path}";
                }

                StorageLog.LogError("Couldn't read connections.json ({Message}); kept it as {Aside} and started empty", e.Message, aside);
                return (ConnectionTree.Empty(),
                    $"Orrery couldn't read connections.json, so it's starting with an empty list. {where}\n\nRecent copies are in "
                    + $"the backups folder next to it.\n\nDetails: {e.Message}");
            }
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
            ShowMerge(_connectionStore.Save(_tree));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or InvalidDataException)
        {
            _ = Dialogs.ShowMessageAsync(this, "Couldn't save your connections", e.Message);
        }
    }

    // ---- A data folder shared with other computers ----

    static readonly string[] WatchedFiles = ["connections.json", "vault.frv", AppSettingsStore.FileName];

    void WatchDataFolder()
    {
        try
        {
            _dataWatcher = new DataFolderWatcher(_location.DataDir, WatchedFiles);
            _dataWatcher.Changed += names => Dispatcher.UIThread.Post(() => _ = OnDataFilesChangedAsync(names));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.For("Storage").LogWarning(e, "Can't watch the data folder for changes from other computers");
        }
    }

    /// <summary>Takes in what another computer saved to the shared files, as a sync app brings it in.</summary>
    internal async Task OnDataFilesChangedAsync(IReadOnlyCollection<string> names)
    {
        if (!IsVisible)
        {
            return;
        }

        // This computer's own saves come through here too; those have nothing to take in.
        if (names.Contains("connections.json", StringComparer.OrdinalIgnoreCase) && _connectionStore.ChangedElsewhere())
        {
            await TakeInConnectionChangesAsync();
        }

        if (names.Contains("vault.frv", StringComparer.OrdinalIgnoreCase) && _credentials is VaultFileStore { ChangedElsewhere: true })
        {
            await _credentialsView.RefreshListAsync();
            if (!_credentials.IsLocked)
            {
                await _properties.RefreshCredentialsAsync();
            }
        }
    }

    async Task TakeInConnectionChangesAsync()
    {
        // The inspector's edits go into the tree first, so the merge keeps them. If they don't validate yet, the next
        // change, or the next save, takes the file's in.
        if (!await _properties.ShowAsync(_properties.Node))
        {
            return;
        }

        var report = _connectionStore.ReloadIfChanged(_tree);
        if (report is null)
        {
            return;
        }

        ShowMerge(report);
        if (_connectionStore.NeedsSave)
        {
            SaveNow();
        }
    }

    /// <summary>Redraws what a merge with another computer's changes touched, and says what both computers changed.</summary>
    void ShowMerge(ConnectionMergeReport? report)
    {
        if (report is null || !IsVisible)
        {
            return;
        }

        if (report.ChangedAnything)
        {
            RebuildTree();
            var shown = _properties.Node;
            _ = _properties.ShowAsync(shown is null || shown == _tree.Root || _tree.Find(shown.Id) is not null ? shown : null);
        }

        if (report.Conflicts.Count > 0)
        {
            foreach (var conflict in report.Conflicts)
            {
                Log.For("Storage").LogWarning("Connections merged with another computer's: {Conflict}", conflict);
            }

            ShowNotice(report.Conflicts.Count == 1
                ? $"Took in changes from another computer. {report.Conflicts[0]}."
                : $"Took in changes from another computer; {report.Conflicts.Count} were changed on both, and this computer's kept. The log lists them.");
        }
        else if (report.ChangedAnything)
        {
            ShowNotice("Took in changes to your connections from another computer");
        }
    }


    /// <summary>Shows a trust prompt over the window, then gives the session the focus it held back while the prompt was up.</summary>
    async Task<CertificateTrustDecision> AskAndRefocusAsync(Window prompt)
    {
        var decision = await prompt.ShowDialog<CertificateTrustDecision>(this);
        Dispatcher.UIThread.Post(() => _currentTab?.FocusContent(), DispatcherPriority.Background);
        return decision;
    }
    // ---- Tree ----

    void WireTree()
    {
        _filter.TextChanged += (_, _) => RebuildTree();

        _treeView.SelectionChanged += async (_, _) =>
        {
            if (!_rebuilding)
            {
                await ShowSelectedAsync();
            }
        };

        // A host's row also expands on double-click (the tree item does that and marks the event handled), so
        // the expansion is put back: double-click connects, the arrow expands. Double-clicks on the arrow are left alone.
        // It goes to the connection's open session, if it has one; with Ctrl held it opens another.
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

            await OpenAsync(entry, newSession: e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control));
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // A middle-click on a connection always opens another session, as it opens a new tab in a browser.
        _treeView.AddHandler(PointerReleasedEvent, async (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle && RowAt(e.Source)?.Node is ConnectionEntry entry)
            {
                e.Handled = true;
                await OpenAsync(entry, newSession: true);
            }
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
        _treeView.ContextMenu!.Opening += (_, e) =>
        {
            // A right-click selects its row, but edits that don't validate put the selection back on theirs: the menu
            // would act on that one instead. It stays shut, and the inspector says what's wrong.
            if (!_properties.TrySave())
            {
                e.Cancel = true;
                return;
            }

            var nodes = SelectedNodes();
            int connections = nodes.OfType<ConnectionEntry>().Count();
            _connectMenuItem.IsEnabled = connections > 0;
            _connectMenuItem.Header = connections > 1 ? $"Connect {connections}" : "Connect";
            _newSessionMenuItem.IsVisible = connections == 1;
            _browseFilesMenuItem.IsVisible = SingleSelectedNode is ConnectionEntry ssh && SettingsResolver.Resolve(ssh).Protocol.Value == Protocol.Ssh;
            int wakeable = nodes.OfType<ConnectionEntry>().Count(e => !string.IsNullOrEmpty(e.MacAddress));
            _wakeMenuItem.IsVisible = nodes.OfType<ConnectionEntry>().Any();
            _wakeMenuItem.IsEnabled = wakeable > 0;
            _wakeMenuItem.Header = wakeable > 1 ? $"Wake {wakeable}" : "Wake";
            ToolTip.SetTip(_wakeMenuItem, wakeable == 0 ? "Set a MAC address in the connection's settings to wake it." : null);

            // Only on the connection that has one: it's never inherited, so a folder or several rows have none to open.
            _managementMenuItem.IsVisible = SingleSelectedNode is ConnectionEntry { ManagementUrl: { Length: > 0 } };
            ToolTip.SetTip(_managementMenuItem, (SingleSelectedNode as ConnectionEntry)?.ManagementUrl);
            _addGuestMenuItem.IsVisible = SingleSelectedNode is ConnectionEntry { GuestOf: null };
            _moveOutOfHostMenuItem.IsVisible = SingleSelectedNode is ConnectionEntry { GuestOf: not null };
            _duplicateMenuItem.IsEnabled = SingleSelectedNode is not null;

            // Exports what's selected; with nothing selected there's nothing here to export (Settings exports everything).
            _exportMenuItem.IsEnabled = SingleSelectedNode is not null;
            _deleteMenuItem.IsEnabled = nodes.Count > 0;
            _deleteMenuItem.Header = nodes.Count > 1 ? $"Delete {nodes.Count} items" : "Delete";
        };

        // Drag a row onto a folder to move it in, or onto a connection to drop it before, after, or in as a guest (TreeDrop).
        _treeView.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(_treeView).Properties.IsLeftButtonPressed)
            {
                _dragStart = e.GetPosition(_treeView);
                _dragPress = e;
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
            if (_dragStart is not { } start || _dragPress is not { } press || _dragRow is not { } row
                || !e.GetCurrentPoint(_treeView).Properties.IsLeftButtonPressed)
            {
                return;
            }

            var delta = e.GetPosition(_treeView) - start;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8)
            {
                return;
            }

            _dragStart = null;
            _dragPress = null;
            _pressedInSelection = null;

            // The whole selection when the dragged row is part of it, else just that row.
            var rows = _treeView.SelectedItems.Contains(row) ? SelectedNodes() : [row.Node];
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(DragFormat, string.Join(",", TreeDrop.Normalize(_tree.Root, rows).Select(n => n.Id))));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _treeView.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _dragStart = null;
            _dragPress = null;
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

                if (!SaveEdits())
                {
                    return;
                }

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
        var titleBar = this.FindControl<Border>("TitleBar")!;
        root.Margin = OffScreenMargin;
        titleBar.Padding = TitleBarPadding();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == OffScreenMarginProperty || e.Property == WindowStateProperty
                || e.Property == ExtendClientAreaToDecorationsHintProperty)
            {
                root.Margin = OffScreenMargin;
                titleBar.Padding = TitleBarPadding();
            }
        };

        // The title bar is drawn by Orrery, so its empty parts move and maximize the window.
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
    /// On Windows a window that isn't maximized has a 1-pixel border drawn over the top row of the title bar, which
    /// would leave quick connect a pixel closer to the top than to the bar's bottom line. Maximized, the border is
    /// off screen, so the bar's content is centered as it is.
    /// </summary>
    Thickness TitleBarPadding() =>
        OperatingSystem.IsWindows() && ExtendClientAreaToDecorationsHint && WindowState == WindowState.Normal
            ? new Thickness(0, 1, 0, 0)
            : default;

    /// <summary>
    /// Leaves room for the system's window buttons: on the right on Windows, on the left on macOS. Where the
    /// window keeps its own title bar (most Linux desktops), none is needed. Both side columns stay equal
    /// so quick connect is centered on the window.
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
    /// The rail switches the side panel between connections, recent, credentials and recordings. Clicking the open
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
            // Clicking Recordings again goes back to the sessions; after a session was chosen, it shows the player again.
            if (side == "recordings" && !_recordingsShown)
            {
                ShowRecordingsHost(true);
                _recordingsPanel.Refresh();
                return;
            }

            if (_recordingsShown)
            {
                ShowRecordingsHost(false);
            }

            ClickActiveRailItem();
            return;
        }

        // The credentials editor takes the inspector's place, so edits that don't validate keep it until they're fixed.
        if (!_properties.TrySave() && side == "credentials")
        {
            RevealInspector();
            return;
        }

        _side = side;
        if (side is "connections" or "recent" or "commands")
        {
            SaveLayout(s => s with { SidePanelPage = side == "connections" ? null : side });
        }

        ShowRecordingsHost(side == "recordings");
        foreach (var (name, (_, view)) in _sides)
        {
            view.IsVisible = name == side;
        }

        RevealSidePanel();

        _inspectorContent.Content = side switch
        {
            "credentials" => _credentialsView.Editor,
            "commands" => _commandEditor,
            _ => _properties,
        };
        switch (side)
        {
            case "credentials":
                RevealInspector();
                await _credentialsView.ShowAsync();
                break;
            case "recent":
                _recentPanel.Refresh();
                break;
            case "commands":
                _commandsPanel.Refresh();
                _commandEditor.UpdateSend();
                break;
            case "recordings":
                _recordingsPanel.Refresh();
                break;
            default:
                await _properties.ShowAsync(SelectedNode);
                break;
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
            else if (Input.SessionEscape.Matches(e.Key, e.KeyModifiers) && _currentTab is { } tab
                && (e.Source as Visual)?.FindAncestorOfType<ShortcutRecorder>(includeSelf: true) is not { IsRecording: true })
            {
                // Out of the session, which keeps every other key, to its tab: keyboard-only and screen reader users'
                // way back to the rest of the window. Enter on the tab goes back in.
                e.Handled = true;
                if (_fullScreen)
                {
                    // Leaving full screen puts the focus back in the session once the layout settles; the tab
                    // takes it after that.
                    ExitFullScreen();
                    Dispatcher.UIThread.Post(() => tab.Header.Focus(NavigationMethod.Tab), DispatcherPriority.Background);
                }
                else
                {
                    tab.Header.Focus(NavigationMethod.Tab);
                }
            }
        }, RoutingStrategies.Tunnel);

        // The bar is narrower than the strip that shows it, so leaving one for the other keeps it; it goes once the
        // pointer is over neither. Otherwise sliding off the bar along the top edge showed it again, and moving down
        // from there, never over the bar, left it showing.
        _fullScreenBar.PointerExited += (_, e) => HideFullScreenBarUnlessOver(e);

        // Every move is seen here before the session takes it. The strip at the top isn't hit-tested, so the session
        // under it still gets the pointer and its clicks (a remote window's title bar or close button sits there);
        // reaching it only shows the bar. Leaving both hides it: an exit can be missed or come with the last position
        // still on the strip, and the bar then stayed over the remote desktop.
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_fullScreenBar.IsVisible)
            {
                HideFullScreenBarUnlessOver(e);
            }
            else if (IsOver(_fullScreenHotZone, e))
            {
                ShowFullScreenBar();
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Full screen hides the tab strip and its recording dot, so the bar says so instead: only while recording, and
    /// in the bar's name for a screen reader.
    /// </summary>
    void ShowFullScreenRecording()
    {
        bool recording = _currentTab?.IsRecording == true;
        _fullScreenRecording.IsVisible = recording;
        if (recording)
        {
            this.FindControl<TextBlock>("FullScreenRecordingText")!.Text = _currentTab!.RecordingText;
        }

        Avalonia.Automation.AutomationProperties.SetName(_fullScreenBar,
            recording ? $"{_currentTab!.Origin.Title}, {_currentTab.RecordingText.ToLowerInvariant()}" : _currentTab?.Origin.Title);
    }

    void ShowFullScreenBar()
    {
        _fullScreenTitle.Text = _currentTab?.Origin.Title;
        _fullScreenHint.Text = $"{Input.FullScreenShortcut.Describe(_fullScreenShortcut)} to leave";
        ShowFullScreenRecording();
        _fullScreenBar.IsVisible = true;
    }

    static bool IsOver(Control control, PointerEventArgs e) =>
        control.IsVisible && new Rect(control.Bounds.Size).Contains(e.GetPosition(control));

    void HideFullScreenBarUnlessOver(PointerEventArgs e)
    {
        if (!IsOver(_fullScreenBar, e) && !IsOver(_fullScreenHotZone, e))
        {
            _fullScreenBar.IsVisible = false;
        }
    }

    void OnExitFullScreenClick(object? sender, RoutedEventArgs e) => ExitFullScreen();

    /// <summary>Every tab in the strip's order, the current one in bold, to go to one that's squeezed or scrolled out of sight.</summary>
    void OnTabListClick(object? sender, RoutedEventArgs e)
    {
        var items = StripTabs().Select(tab =>
        {
            var item = new MenuItem
            {
                Header = $"{tab.Origin.Title} · {tab.Origin.Label}",
                FontWeight = tab == _currentTab ? FontWeight.SemiBold : FontWeight.Normal,
            };
            ToolTip.SetTip(item, $"{tab.Address} · {tab.StateText}");
            item.Click += (_, _) => SelectTab(tab);
            return (Control)item;
        }).ToList();
        Controls.PopupMenu.ShowAt((Control)sender!, items, PlacementMode.BottomEdgeAlignedRight);
    }

    // ---- Tab strip keys and dragging ----

    SessionTab? _draggedTab;
    Point? _tabDragStart;

    /// <summary>How far one notch of the wheel moves the tab bar, in pixels: about half a tab.</summary>
    internal const double TabWheelStep = 60;

    void WireTabStrip()
    {
        // The wheel scrolls tabs that don't all fit sideways, as a browser's tab bar does; up goes left.
        var scroller = this.FindControl<ScrollViewer>("TabScroller")!;

        // The tabs narrow to fit the bar, less the new-session buttons beside them, before it scrolls.
        scroller.SizeChanged += (_, _) =>
        {
            var row = (Panel)_tabStrip.Parent!;
            double buttons = row.Children.Where(c => c != _tabStrip && c.IsVisible).Sum(c => c.Bounds.Width + c.Margin.Left + c.Margin.Right)
                + ((row as StackPanel)?.Spacing ?? 0) * (row.Children.Count - 1);
            _tabStrip.Room = scroller.Bounds.Width - buttons;
        };

        // A list of every tab once there's more than one to choose from.
        var tabList = this.FindControl<Button>("TabListButton")!;
        _tabStrip.Children.CollectionChanged += (_, _) => tabList.IsVisible = StripTabs().Count > 1;
        scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            double room = scroller.Extent.Width - scroller.Viewport.Width;
            double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            if (room <= 0 || delta == 0)
            {
                return;
            }

            scroller.Offset = scroller.Offset.WithX(Math.Clamp(scroller.Offset.X - delta * TabWheelStep, 0, room));
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        // Caught on the way down, as the full-screen shortcut is; TabShortcuts decides what a focused session keeps.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            var strip = StripTabs();
            if (e.Handled || strip.Count == 0 || _settingsHost.IsVisible
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
            else if (Input.TabShortcuts.Target(command, _currentTab is null ? 0 : strip.IndexOf(_currentTab), strip.Count) is { } index)
            {
                SelectTab(strip[index]);
            }
        }, RoutingStrategies.Tunnel);

        // A local terminal, popping the current tab out, splitting and quick connect, caught on the way down too so a
        // focused terminal doesn't take them as Ctrl+T, Ctrl+O, Ctrl+E and Ctrl+K.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || _dimmer.IsVisible
                || (e.Source as Visual)?.FindAncestorOfType<ShortcutRecorder>(includeSelf: true) is { IsRecording: true })
            {
                return;
            }

            if (Input.TabShortcuts.OpensLocalTerminal(e.Key, e.KeyModifiers, FocusPlaceOf(e.Source), OperatingSystem.IsMacOS()))
            {
                e.Handled = true;
                OpenLocalTerminal();
            }
            else if (Input.TabShortcuts.PopsOut(e.Key, e.KeyModifiers, FocusPlaceOf(e.Source), OperatingSystem.IsMacOS()) && _currentTab is { } tab
                && !_settingsHost.IsVisible)
            {
                e.Handled = true;
                PopOut(tab);
            }
            else if (Input.TabShortcuts.Splits(e.Key, e.KeyModifiers, FocusPlaceOf(e.Source), OperatingSystem.IsMacOS()) && _currentTab is { } current
                && !_settingsHost.IsVisible)
            {
                e.Handled = true;
                ToggleSplit(current);
            }
            else if (Input.TabShortcuts.OpensQuickConnect(e.Key, e.KeyModifiers, FocusPlaceOf(e.Source), OperatingSystem.IsMacOS()))
            {
                e.Handled = true;
                _ = OpenQuickConnectAsync();
            }
        }, RoutingStrategies.Tunnel);

        // Clicking into or tabbing into the other side of a split makes it the current tab.
        _sessionHost.AddHandler(GotFocusEvent, (_, e) => OnSessionAreaFocus(e.Source));
        _sessionHost.AddHandler(PointerPressedEvent, (_, e) => OnSessionAreaFocus(e.Source), RoutingStrategies.Tunnel, handledEventsToo: true);

        // Drag a tab sideways to move it; it lands where it's let go. Dragged well above or below the strip, it pops
        // out into a window of its own where it's let go. The tab's own click still selects it.
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

            var at = e.GetPosition(_tabStrip);
            double dx = at.X - start.X;
            if (!_tabDragging && (Math.Abs(dx) > 6 || Math.Abs(at.Y - start.Y) > 6))
            {
                _tabDragging = true;

                // The button theme animates RenderTransform (a pressed "shrink"), and an animated value outranks the
                // drag's own, so its transitions are off for the drag; the tabs slide with the drag's own instead.
                foreach (var each in StripTabs())
                {
                    each.Header.Transitions = [];
                }

                // The tab lifts off the strip and follows the pointer; where it was is left as an empty slot, which
                // moves to where it will land.
                _tabGhost = TabGhost.Show(this, tab, tab.Header.Bounds.Width);
                ShowAsSlot(tab.Header, true);
            }

            if (_tabDragging)
            {
                _tabTearing = Input.TabShortcuts.TearsOff(at.Y, _tabStrip.Bounds.Height);
                _tabSplitSide = _tabTearing ? SplitSideAt(tab, e.GetPosition(this)) : null;
                ShowSplitPreview(_tabSplitSide);
                ShowTabDrag(tab, dx, at);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        _tabStrip.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (_draggedTab is { } tab && _tabDragging)
            {
                int target = _tabDropIndex;
                bool tearing = _tabTearing;
                var splitSide = _tabSplitSide;
                var at = this.PointToScreen(e.GetPosition(this));
                EndTabDrag();

                // After the release has been through the tab, which is still in the strip until then.
                if (splitSide is { } onRight)
                {
                    Dispatcher.UIThread.Post(() => SplitWith(tab, onRight));
                }
                else if (tearing)
                {
                    Dispatcher.UIThread.Post(() => PopOut(tab, at: at));
                }
                else
                {
                    MoveTab(tab, target);
                }

                return;
            }

            EndTabDrag();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        _tabStrip.AddHandler(PointerCaptureLostEvent, (_, _) => EndTabDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    bool _tabDragging;
    int _tabDropIndex;

    // The held tab is far enough above or below the strip to pop out where it's let go.
    bool _tabTearing;

    // Torn off over the session area's left (false) or right (true) edge: it splits there instead of popping out.
    bool? _tabSplitSide;

    // The held tab, drawn over the window while it's dragged.
    TabGhost? _tabGhost;

    /// <summary>
    /// Draws the drag: the held tab, lifted, follows the pointer along the strip, and the tabs it passes slide aside
    /// so its empty slot shows where it will land. Pulled off the strip to pop out, it follows the pointer anywhere and
    /// opens into a picture of the window it will become, and the slot goes back to its place. Only transforms move.
    /// </summary>
    void ShowTabDrag(SessionTab tab, double dx, Point at)
    {
        var strip = StripTabs();
        int from = strip.IndexOf(tab);
        var bounds = tab.Header.Bounds;
        double left = strip[0].Header.Bounds.Left, right = strip[^1].Header.Bounds.Right;
        dx = Math.Clamp(dx, left - bounds.Left, right - bounds.Right);

        // Judged by the leading edge, not the center: a tab wider than its neighbor could never get its center
        // past the neighbor's middle at the end of the strip.
        double edge = dx < 0 ? bounds.Left + dx : bounds.Right + dx;
        _tabDropIndex = _tabTearing ? from : Input.TabShortcuts.DropIndex([.. strip.Select(t => t.Header.Bounds.Center.X)], from, edge);
        tab.Header.Cursor = _tabTearing ? new Cursor(StandardCursorType.DragMove) : null;
        if (_tabGhost is { } ghost)
        {
            ghost.AsWindow = _tabTearing;
            ghost.MoveTo(_tabStrip, _tabTearing ? at - TabGhost.WindowGrip : new Point(bounds.Left + dx, bounds.Top));
        }

        double step = bounds.Width + _tabStrip.Spacing;
        double slot = 0;
        for (int i = 0; i < strip.Count; i++)
        {
            int shift = Input.TabShortcuts.Shift(i, from, _tabDropIndex);
            slot -= shift * (strip[i].Header.Bounds.Width + _tabStrip.Spacing);
            if (i != from)
            {
                SlideTo(strip[i].Header, shift * step, animate: true);
            }
        }

        SlideTo(tab.Header, slot, animate: true);
    }

    /// <summary>A tab's place in the strip, kept for it while it's dragged: its outline, with nothing in it.</summary>
    static void ShowAsSlot(Control header, bool slot)
    {
        header.Classes.Set("slot", slot);
        if (header is ContentControl { Content: Control content })
        {
            content.Opacity = slot ? 0 : 1;
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
            each.Header.ClearValue(CursorProperty);
            ShowAsSlot(each.Header, false);
        }

        _tabGhost?.Remove();
        ShowSplitPreview(null);
        (_draggedTab, _tabDragStart, _tabDragging, _tabTearing, _tabGhost, _tabSplitSide) = (null, null, false, false, null, null);
    }

    SessionTab? TabAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } button ? _tabs.FirstOrDefault(t => t.Header == button) : null;

    /// <summary>
    /// Moves a tab to <paramref name="index"/> in the strip. Among all the open sessions it goes just before or after
    /// the tab it lands on, so a popped-out session keeps its place for when it comes back.
    /// </summary>
    void MoveTab(SessionTab tab, int index)
    {
        var strip = StripTabs();
        int from = strip.IndexOf(tab);
        if (from < 0 || index == from || index < 0 || index >= strip.Count)
        {
            return;
        }

        var landsOn = strip[index];
        _tabs.Remove(tab);
        int at = _tabs.IndexOf(landsOn);
        _tabs.Insert(index > from ? at + 1 : at, tab);
        _tabStrip.Children.Move(from, index);
    }

    static Input.FocusPlace FocusPlaceOf(object? source) => (source as Visual) switch
    {
        { } visual when visual.FindAncestorOfType<RemoteFrameView>(includeSelf: true) is not null => Input.FocusPlace.Desktop,
        { } visual when visual.FindAncestorOfType<TerminalView>(includeSelf: true) is not null => Input.FocusPlace.Terminal,
        _ => Input.FocusPlace.App,
    };

    /// <summary>
    /// The current session fills the screen the window is on, or <paramref name="screen"/>, or for a remote desktop the
    /// displays its connection chooses (<paramref name="choice"/> instead, for this time only); everything else hides
    /// until the full-screen shortcut.
    /// </summary>
    void EnterFullScreen(Avalonia.Platform.Screen? screen = null, FullScreenChoice? choice = null)
    {
        if (_fullScreen || _currentTab is not { } tab)
        {
            return;
        }

        // Full screen is for one session: the other side of a split waits in its tab.
        EndSplit(tab);

        IReadOnlyList<Avalonia.Platform.Screen>? spread = null;
        if (screen is null && tab.View is RemoteFrameView desktopView)
        {
            var chosen = ChosenScreens(tab, choice, desktopView);
            if (chosen.Count == 1)
            {
                screen = chosen[0];
            }
            else if (chosen.Count > 1)
            {
                spread = chosen;
            }
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
        _restoreOfferBeforeFullScreen = _restoreBar is { IsVisible: true };
        if (_restoreBar is not null)
        {
            _restoreBar.IsVisible = false;
        }

        _stateBeforeFullScreen = WindowState;
        _titleBar.IsVisible = _rail.IsVisible = _statusBar.IsVisible = _tabStripBar.IsVisible = _updateBarHost.IsVisible = false;
        HidePanesForFullScreen();
        _sessionToolbar.IsVisible = false;
        _fullScreenHotZone.IsVisible = true;

        // Avalonia keeps a title bar strip at the top of a full-screen window that draws into its client
        // area; Orrery has its own bar for leaving, so the window goes back to plain decorations meanwhile.
        ExtendClientAreaToDecorationsHint = false;
        if (spread is null || !TrySpanDisplays(tab, spread))
        {
            WindowState = WindowState.FullScreen;
        }

        FullScreenChanged?.Invoke(this, EventArgs.Empty);
        Dispatcher.UIThread.Post(tab.FocusContent, DispatcherPriority.Background);
    }

    /// <summary>
    /// The displays a remote desktop's full screen fills: its connection's choice, or <paramref name="choice"/>. Several
    /// only on Windows (see <see cref="SpanningWindow"/>); elsewhere the first of them. Empty for the display the window
    /// is on. Says on the desktop when chosen displays aren't connected, or several can't be used here.
    /// </summary>
    IReadOnlyList<Avalonia.Platform.Screen> ChosenScreens(SessionTab tab, FullScreenChoice? choice, RemoteFrameView desktop)
    {
        choice ??= tab.Origin.EntryId is { } id && _tree.Find(id) is { } node ? SettingsResolver.Resolve(node).FullScreen.Value : FullScreenChoice.ThisDisplay;
        var screens = Screens.All;
        var picked = choice.Pick(screens.Select(AreaOf).ToList(), screens.Select(s => DisplayPicker.Of(s).Name).ToList(), out int missing)
            .Select(i => screens[i]).ToList();
        if (choice.On == FullScreenOn.ChosenDisplays && missing > 0)
        {
            desktop.ShowNotice(picked.Count == 0
                ? "None of the displays chosen for full screen is connected, so it's on this display."
                : missing == 1 ? "A display chosen for full screen isn't connected, so it's left out." : $"{missing} displays chosen for full screen aren't connected, so they're left out.");
        }

        // Wayland gives a window no say in where it goes or which displays it covers, and macOS gives each display its
        // own full-screen space, so only Windows can put one window over several.
        if (picked.Count > 1 && !OperatingSystem.IsWindows())
        {
            desktop.ShowNotice("Full screen is on one display. Spreading a remote desktop over several works on Windows only.");
            return [picked[0]];
        }

        return picked;
    }

    /// <summary>
    /// This computer's displays for an .rdp file's selectedmonitors, in the order Windows lists them, which is how
    /// mstsc numbers them; null elsewhere, where the numbers mean nothing.
    /// </summary>
    IReadOnlyList<ChosenDisplay>? DisplaysForRdpFiles() => OperatingSystem.IsWindows()
        ? Screens.All.Select(s => ChosenDisplay.Of(AreaOf(s), DisplayPicker.Of(s).Name)).ToList()
        : null;

    static ScreenArea AreaOf(Avalonia.Platform.Screen screen) => new(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, screen.IsPrimary);

    /// <summary>
    /// Puts the window over <paramref name="screens"/>, with a monitor of the remote desktop for each where the server
    /// takes a layout (RDP); VNC's picture is fitted over them. Displays between chosen ones are cut out of the window,
    /// and the remote monitors, moved together, are each drawn on their display. False, with the reason on the
    /// desktop, when they can't make one desktop; full screen is then the one display.
    /// </summary>
    bool TrySpanDisplays(SessionTab tab, IReadOnlyList<Avalonia.Platform.Screen> screens)
    {
        if (!OperatingSystem.IsWindows() || tab.View is not RemoteFrameView desktop
            || (desktop.Session is IMultiMonitorSession && !desktop.FollowsViewSize))
        {
            return false;
        }

        var areas = screens.Select(AreaOf).ToList();
        if (MonitorLayout.Arrange(areas, out var covered, out bool moved, out var whyNot) is not { } monitors)
        {
            ConnectLog.LogInformation("Full screen for {Title} is on one display: {Reason}", tab.Origin.Title, whyNot);
            desktop.ShowNotice($"Full screen is on this display only. {whyNot}");
            return false;
        }

        WindowState = WindowState.Normal;
        _spanned = (desktop, Position, Width, Height, WindowDecorations, Topmost);
        WindowDecorations = WindowDecorations.None;

        // Windows hides the taskbar for a window that fills one display, not for one across several, so this stays on top.
        Topmost = true;
        void Place()
        {
            SpanningWindow.Cover(this, covered);
            SpanningWindow.Limit(this, covered, areas);
        }

        Place();
        Dispatcher.UIThread.Post(() =>
        {
            if (_spanned is not null && OperatingSystem.IsWindows())
            {
                Place();
            }
        }, DispatcherPriority.Background);

        if (desktop.Session is IMultiMonitorSession)
        {
            desktop.SpannedMonitors = monitors;
        }

        if (moved)
        {
            int left = monitors.Min(m => m.Left), top = monitors.Min(m => m.Top);
            var layout = new PixelSize(monitors.Max(m => m.Left + m.Width) - left, monitors.Max(m => m.Top + m.Height) - top);
            desktop.ShowPieces(areas.Select((a, i) => new RemoteFrameView.Piece(
                new PixelRect(a.X - covered.X, a.Y - covered.Y, a.Width, a.Height),
                new PixelRect(monitors[i].Left - left, monitors[i].Top - top, monitors[i].Width, monitors[i].Height))).ToList(), layout);
        }

        ConnectLog.LogInformation("Spreading {Title} over {Count} displays, {Width}x{Height} in all{Moved}", tab.Origin.Title, monitors.Count, covered.Width, covered.Height,
            moved ? ", moved together on the remote desktop" : string.Empty);
        return true;
    }

    void ExitFullScreen()
    {
        if (!_fullScreen)
        {
            return;
        }

        _fullScreen = false;
        if (_spanned is { } spanned)
        {
            _spanned = null;
            spanned.View.SpannedMonitors = null;
            spanned.View.ShowPieces(null, default);
            if (OperatingSystem.IsWindows())
            {
                SpanningWindow.Limit(this, default, null);
            }
            Topmost = spanned.Topmost;
            WindowDecorations = spanned.Decorations;
            Position = spanned.Position;
            Width = spanned.Width;
            Height = spanned.Height;
        }

        _fullScreenHotZone.IsVisible = _fullScreenBar.IsVisible = false;
        _titleBar.IsVisible = _rail.IsVisible = _statusBar.IsVisible = _tabStripBar.IsVisible = _updateBarHost.IsVisible = true;
        ApplyPanes();

        // Still offered unless it was withdrawn meanwhile (ReopenWorkspaceAsync, ForgetOpenTabs).
        if (_restoreOfferBeforeFullScreen && _restoreBar is not null)
        {
            _restoreBar.IsVisible = true;
        }

        _restoreOfferBeforeFullScreen = false;
        if (_positionBeforeFullScreen is { } before)
        {
            WindowState = WindowState.Normal;
            Position = before.Position;
            _stateBeforeFullScreen = before.State;
            _positionBeforeFullScreen = null;
        }

        WindowState = _stateBeforeFullScreen;
        ExtendClientAreaToDecorationsHint = true;
        FullScreenChanged?.Invoke(this, EventArgs.Empty);
        UpdateSessionToolbar();
        if (_currentTab is { } tab)
        {
            Dispatcher.UIThread.Post(tab.FocusContent, DispatcherPriority.Background);
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
            _ = ShowSelectedAsync();
        }
    }

    /// <summary>Shows the selected node in the inspector, or, when the edits there don't validate, goes back to their node, the problem showing, rather than losing them.</summary>
    async Task ShowSelectedAsync()
    {
        if (!await _properties.ShowAsync(SelectedNode) && SelectedNode != _properties.Node)
        {
            Dispatcher.UIThread.Post(SelectEdited);
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

    /// <summary>
    /// Applies the inspector's edits before something that changes the tree or covers the inspector. False if they don't
    /// validate, with the inspector shown saying why, so that waits rather than the edits being lost.
    /// </summary>
    bool SaveEdits()
    {
        if (_properties.TrySave())
        {
            return true;
        }

        RevealInspector();
        return false;
    }

    /// <summary>Selects the row the inspector is editing (none for the defaults), without showing it again.</summary>
    void SelectEdited()
    {
        if (_properties.Node is not { } node || SelectedNode == node)
        {
            return;
        }

        _rebuilding = true;
        try
        {
            if (node == _tree.Root)
            {
                _treeView.SelectedItems.Clear();
            }
            else if (FindRow(node.Id) is null && !string.IsNullOrEmpty(_filter.Text))
            {
                // The filter hides it, so no row could be chosen until the edits are fixed: the filter goes, which
                // brings this back here with the row showing.
                _rebuilding = false;
                _filter.Text = string.Empty;
                return;
            }
            else
            {
                Select([node.Id]);
            }
        }
        finally
        {
            _rebuilding = false;
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

        if (!SaveEdits())
        {
            return;
        }

        parent.Expanded = true;
        _filter.Text = string.Empty;
        _tree.Add(node, parent);
        Select(node.Id);
        Dispatcher.UIThread.Post(_properties.FocusName, DispatcherPriority.Background);
    }

    async void OnConnectMenuClick(object? sender, RoutedEventArgs e) => await ConnectSelectedAsync();

    async void OnNewSessionMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SingleSelectedNode is ConnectionEntry entry)
        {
            await OpenAsync(entry, newSession: true);
        }
    }

    async void OnBrowseFilesMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SingleSelectedNode is ConnectionEntry entry)
        {
            await ConnectAsync(entry, files: true);
        }
    }

    async void OnWakeMenuClick(object? sender, RoutedEventArgs e) => await WakeSelectedAsync();

    async void OnManagementMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SingleSelectedNode is ConnectionEntry entry)
        {
            await OpenManagementPageAsync(entry);
        }
    }

    /// <summary>
    /// Opens a connection's management page, if it's an http or https address: in a tab, or in the browser if the
    /// connection or Settings says so.
    /// </summary>
    async Task OpenManagementPageAsync(ConnectionEntry entry)
    {
        if (!ManagementUrl.TryNormalize(entry.ManagementUrl, out var text) || !Uri.TryCreate(text, UriKind.Absolute, out var url))
        {
            await Dialogs.ShowMessageAsync(this, $"Can't open \"{entry.Name}\"'s management page",
                "Its management URL isn't a web address. Set one such as https://10.20.0.1:8443 in the connection's settings.");
            return;
        }

        if (WebPagesInOrrery(entry.Id))
        {
            ConnectLog.LogInformation("Opening the management page of \"{Name}\" in a tab: {Site}", entry.Name, url.GetLeftPart(UriPartial.Authority));
            var protocol = url.Scheme == Uri.UriSchemeHttps ? Protocol.Https : Protocol.Http;
            OpenWebTab(new SessionOrigin($"{entry.Name} · management", protocol, entry.Id, () => OpenManagementPageAsync(entry)) { ManagementPage = true },
                url, new ConnectionTarget(url.Host, url.Port, null, null));
            return;
        }

        // Only the scheme, host and port: a page's path or query can carry tokens.
        ConnectLog.LogInformation("Opening the management page of \"{Name}\" in the browser: {Site}", entry.Name, url.GetLeftPart(UriPartial.Authority));
        if (OpenInBrowser(url) is { } problem)
        {
            ConnectLog.LogWarning("Couldn't open the browser for \"{Name}\"'s management page: {Message}", entry.Name, problem);
            await Dialogs.ShowMessageAsync(this, "Couldn't open the browser", problem);
        }
    }

    /// <summary>
    /// Hands <paramref name="url"/> to the default browser. Null if it went; what went wrong if not. Only http and https
    /// go: a page can navigate itself to any scheme, and the shell would hand another to whatever program claims it.
    /// </summary>
    internal static string? OpenInBrowser(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return $"Only web addresses open in the browser, not {url.Scheme}: ones.";
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Whether web pages open in a tab for this connection: its own choice or its folders', else Settings'. Where the
    /// engine is missing they go to the browser instead, with a note saying why.
    /// </summary>
    bool WebPagesInOrrery(Guid? entryId)
    {
        bool appSetting = _settingsStore.Load().OpensWebPagesInOrrery;
        bool wanted = entryId is { } id && _tree.Find(id) is { } node ? SettingsResolver.Resolve(node).InOrrery(appSetting) : appSetting;
        if (wanted && !WebPageView.EngineAvailable)
        {
            ConnectLog.LogWarning("WebKitGTK (libwebkit2gtk-4.1) isn't installed, so the page opens in the browser");
            ShowNotice("Opened in your browser: pages in a tab need WebKitGTK (libwebkit2gtk-4.1)");
            return false;
        }

        return wanted;
    }

    /// <summary>Sends a wake-up packet for each selected connection that has a MAC address.</summary>
    Task WakeSelectedAsync() => WakeAsync(SelectedNodes().OfType<ConnectionEntry>());

    async Task WakeAsync(IEnumerable<ConnectionEntry> entries)
    {
        var problems = new List<string>();
        var woken = new List<string>();
        foreach (var entry in entries.Where(en => !string.IsNullOrEmpty(en.MacAddress)))
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
        Controls.ScreenReader.Announce(_noticeText, text, "Orrery.Notice", Controls.ScreenReader.MostRecent);
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
            await OpenAsync(entry);
        }
    }

    /// <summary>
    /// Goes to the connection's open session, the one shown last, wherever it is: its tab, or its own window. With none
    /// open, with <paramref name="newSession"/>, or with Settings saying so, it connects again in a new session.
    /// </summary>
    async Task OpenAsync(ConnectionEntry entry, bool newSession = false)
    {
        if (!newSession && !_newSessionOnOpen && SessionTab.OpenFor(_tabs, entry.Id) is { } open)
        {
            ShowTab(open);
            return;
        }

        await ConnectAsync(entry);
    }

    async void OnEditDefaultsClick(object? sender, RoutedEventArgs e)
    {
        if (!SaveEdits())
        {
            return;
        }

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

        if (!SaveEdits())
        {
            return;
        }

        _filter.Text = string.Empty;
        host.GuestsCollapsed = false;
        var guest = new ConnectionEntry { Name = "New guest" };
        _tree.AddGuest(guest, host);
        Select(guest.Id);
        Dispatcher.UIThread.Post(_properties.FocusName, DispatcherPriority.Background);
    }

    void OnMoveOutOfHostMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is ConnectionEntry { GuestOf: not null } guest && SaveEdits())
        {
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
        if (!SaveEdits())
        {
            return;
        }

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
        string body = $"{nodes.Count} items will be deleted" + (inside > 0 ? $", with {Plural(inside, "item")} in them." : ".");
        bool editedGoes = EditedGoesWith(nodes, withGuests: true);
        if ((editedGoes || SaveEdits()) && await Dialogs.ConfirmAsync(this, $"Delete {nodes.Count} items?", body, "Delete"))
        {
            await ClearInspectorForDelete(editedGoes);
            foreach (var node in nodes)
            {
                Remove(node);
            }
        }
    }

    async Task DeleteAsync(ConnectionNode node)
    {
        if (node is ConnectionEntry { IsHost: true } host)
        {
            var choice = await new HostDeleteWindow(host, _tree).ShowDialog<HostDeleteChoice?>(this);
            bool hostEditedGoes = choice is not null && EditedGoesWith([host], withGuests: choice.KeepGuestsIn is null);
            if (choice is null || (!hostEditedGoes && !SaveEdits()))
            {
                return;
            }

            await ClearInspectorForDelete(hostEditedGoes);
            if (choice.KeepGuestsIn is { } destination)
            {
                KeepInheritedSettings(host, withGuests: false);
                _tree.RemoveKeepingGuests(host, destination);
            }
            else
            {
                Remove(host);
            }

            return;
        }

        string body = node is ConnectionFolder folder && folder.Children.Count > 0
            ? $"\"{node.Name}\" and {Plural(folder.Descendants().Count(), "item")} in it will be deleted."
            : $"\"{node.Name}\" will be deleted.";
        bool editedGoes = EditedGoesWith([node], withGuests: true);
        if ((editedGoes || SaveEdits()) && await Dialogs.ConfirmAsync(this, $"Delete {(node is ConnectionFolder ? "folder" : "connection")}?", body, "Delete"))
        {
            await ClearInspectorForDelete(editedGoes);
            Remove(node);
        }
    }

    /// <summary>Whether the inspector's node goes with <paramref name="deleted"/>: one of them, in one, or a guest of one.</summary>
    bool EditedGoesWith(IEnumerable<ConnectionNode> deleted, bool withGuests) =>
        _properties.Node is { } edited && deleted.Any(node => node == edited
            || (node is ConnectionFolder folder && edited.Ancestors().Contains(folder))
            || (withGuests && edited is ConnectionEntry { GuestOf: { } host } && host == node));

    /// <summary>Empties the inspector before a delete; the edits there are dropped only if their node is going.</summary>
    async Task ClearInspectorForDelete(bool editedGoes)
    {
        if (editedGoes)
        {
            _properties.DiscardEdits();
        }

        await _properties.ShowAsync(null);
    }

    /// <summary>Deletes <paramref name="node"/> with everything in it, after <see cref="KeepInheritedSettings"/>.</summary>
    void Remove(ConnectionNode node)
    {
        KeepInheritedSettings(node, withGuests: true);
        _tree.Remove(node);
    }

    /// <summary>
    /// Gives each connection about to be deleted with <paramref name="node"/> the settings it inherits from its host and
    /// folders, as its own. An open tab's Reconnect and Duplicate session then still go through the same jump host, as
    /// the same user, rather than straight to the host once the folders are gone.
    /// </summary>
    internal static void KeepInheritedSettings(ConnectionNode node, bool withGuests)
    {
        IEnumerable<ConnectionEntry> going = node switch
        {
            ConnectionFolder folder => folder.Descendants().OfType<ConnectionEntry>(),
            ConnectionEntry entry when withGuests => entry.Guests.Prepend(entry),
            ConnectionEntry entry => [entry],
            _ => [],
        };
        foreach (var entry in going.ToList())
        {
            if (entry.GuestOf is { } host)
            {
                Fill(entry.Settings, host.Settings, InheritableSettings.PassedToGuests);
            }

            foreach (var folder in entry.Ancestors())
            {
                Fill(entry.Settings, folder.Settings);
            }
        }

        // The two full-screen settings are one choice, so a node that makes it takes neither from further up.
        static void Fill(InheritableSettings settings, InheritableSettings from, IReadOnlySet<string>? only = null)
        {
            var (chosen, every) = (settings.ChosenDisplays, settings.UseAllMonitors);
            settings.FillFrom(from, only);
            if (chosen is not null || every is not null)
            {
                (settings.ChosenDisplays, settings.UseAllMonitors) = (chosen, every);
            }
            else if (settings.ChosenDisplays is { } taken)
            {
                // Its own copy, so the two never share one.
                settings.ChosenDisplays = taken.ToList();
            }
        }
    }

    // ---- Connecting ----

    /// <summary>
    /// Whether Ctrl+Alt with the key is AltGr typing a character on this keyboard, which the Ctrl+Alt shortcut then leaves
    /// to the session or page. Ctrl+Alt+B is the only one on a key that can type: the rest are on Enter, Home, End and Page keys.
    /// </summary>
    internal Func<Key, bool> TypesWithAltGr { get; set; } = Input.AltGr.TypesCharacter;

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // A focused session swallows keys first, so Ctrl+K inside a remote desktop goes to the remote machine, and in a
        // terminal to the shell; Ctrl+Shift+K opens quick connect from a terminal.
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
            ToggleSidePanel();
        }
        else if (e.Key == Key.B && e.KeyModifiers == (modifier | KeyModifiers.Alt) && !(modifier == KeyModifiers.Control && TypesWithAltGr(e.Key)))
        {
            e.Handled = true;
            ToggleInspector();
        }
    }

    async void OnQuickConnectClick(object? sender, RoutedEventArgs e) => await OpenQuickConnectAsync();

    /// <summary>A connection's settings, ready to change, without connecting.</summary>
    void EditEntry(ConnectionEntry entry)
    {
        _filter.Text = string.Empty;
        if (_side != "connections")
        {
            OnRailClick(_sides["connections"].RailButton, new RoutedEventArgs());
        }

        RevealSidePanel();
        RevealInspector();
        foreach (var folder in entry.Ancestors())
        {
            folder.Expanded = true;
        }

        RebuildTree();
        Select(entry.Id);
        Dispatcher.UIThread.Post(_properties.FocusName, DispatcherPriority.Background);
    }

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
            var open = _tabs.Select(t => new OpenSession(t.Origin.Title, t.Address, t.Origin.Protocol, t.Origin.Label)).ToList();
            choice = await new QuickConnectWindow(_tree, open, PaletteCommands()).ShowOver(this);
        }
        finally
        {
            _dimmer.IsVisible = false;
        }

        if (choice?.SessionIndex is { } index)
        {
            if (index < _tabs.Count)
            {
                ShowTab(_tabs[index]);
            }
        }
        else if (choice is { Alt: true, Command.AltRun: { } alternative })
        {
            await alternative();
        }
        else if (choice?.Command is { } command)
        {
            await command.Run();
        }
        else if (choice is { Edit: true, Entry: { } toEdit })
        {
            EditEntry(toEdit);
        }
        else if (choice?.NewName is { } name)
        {
            var created = new ConnectionEntry { Name = name, Host = choice.Host ?? string.Empty };

            // A serial port or web page typed in makes that kind of connection; anything else takes its folder's protocol.
            if (choice.Protocol is Protocol.Serial || ProtocolInfo.IsWeb(choice.Protocol))
            {
                created.Settings.Protocol = choice.Protocol;
                if (ProtocolInfo.IsWeb(choice.Protocol) && choice.Port != ProtocolInfo.DefaultPort(choice.Protocol))
                {
                    created.Settings.Port = choice.Port;
                }
            }

            AddAndEdit(created);
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

    /// <param name="skipKeys">Ask for a password even if the connection has a key file or an SSH agent is running: those keys were just refused.</param>
    /// <param name="files">SSH: open a file browser (SFTP) instead of a terminal.</param>
    async Task ConnectAsync(ConnectionEntry entry, bool skipKeys = false, bool files = false)
    {
        if (_properties.Node == entry && !_properties.TrySave())
        {
            return;
        }

        var resolved = SettingsResolver.Resolve(entry);
        var protocol = resolved.Protocol.Value;
        // A local shell's host is the shell, and empty is the one chosen in Settings.
        if (protocol == Protocol.Local)
        {
            ConnectLog.LogInformation("Opening \"{Name}\": a local shell{Elevation}", entry.Name, entry.RunAsAdministrator ? ", as administrator" : string.Empty);
            OpenLocalTerminal(string.IsNullOrWhiteSpace(entry.Host) ? null : entry.Host, entry.RunAsAdministrator, entry);
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Host))
        {
            await Dialogs.ShowMessageAsync(this, $"\"{entry.Name}\" has no host", protocol == Protocol.Serial
                ? "Enter the serial port's name, such as COM3 or /dev/ttyUSB0, in its settings first."
                : "Enter a host name or IP address in its settings first.");
            return;
        }

        if (ProtocolInfo.IsWeb(protocol))
        {
            await OpenWebPageAsync(entry.Name, entry.Id, protocol, entry.Host, resolved.Port.Value, resolved.JumpHostId.Value is not null);
            return;
        }

        files &= protocol == Protocol.Ssh;
        var origin = new SessionOrigin(files ? $"{entry.Name} · files" : entry.Name, protocol, entry.Id, () => ConnectAsync(entry, files: files), resolved.AutoReconnect.Value)
        {
            Files = files,
            ReachableDirectly = resolved.JumpHostId.Value is null && ChecksHostDirectly(protocol),
        };
        if (protocol == Protocol.Serial)
        {
            var line = SerialLine.From(resolved);
            ConnectLog.LogInformation("Opening \"{Name}\": serial port {Port} at {Line}", entry.Name, entry.Host, line);
            StartSerial(origin, entry.Host, line);
            return;
        }

        try
        {
            // SSH goes through its jump hosts itself; RDP, VNC and Telnet through a tunnel the jump hosts make.
            if (await JumpHopsAsync(entry) is not { } jumps)
            {
                return;
            }

            ConnectLog.LogInformation("Connecting \"{Name}\": {Protocol}{Files} to {Host}:{Port}{Via}", entry.Name, protocol, files ? " files" : string.Empty,
                entry.Host, resolved.Port.Value, jumps.Count == 0 ? string.Empty : " through " + string.Join(", ", jumps.Select(j => $"{j.Target.Host}:{j.Target.Port}")));
            // An SSH session opens its own forwards and the SOCKS proxy its folder asks for.
            var route = new SshRoute(jumps, protocol == Protocol.Ssh ? PortForward.WithSocks(entry.PortForwards, resolved.SocksProxy.Value) : [],
                SshSession.KeepAliveFor(resolved.KeepAliveSeconds.Value));

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

                var (read, credential) = await ReadSavedCredentialAsync(id);
                if (!read)
                {
                    return;
                }

                if (credential is not null)
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

            // SSH: the connection's key file, with its passphrase asked for now if it has one. Skipped after the
            // server refused the keys, so the retry goes straight to asking for a password.
            KeyFileLogin? keyFile = null;
            if (protocol == Protocol.Ssh && !skipKeys && resolved.KeyFile.Value is { } keyPath)
            {
                if (await PrepareKeyFileAsync(keyPath, entry.Name) is not { } prepared)
                {
                    ConnectLog.LogDebug("\"{Name}\": the key file passphrase prompt was canceled", entry.Name);
                    return;
                }

                keyFile = prepared;
            }

            // SSH with no saved credential: the key file and the agent's keys, if there are any, without asking for a password.
            bool useAgent = password is null && protocol == Protocol.Ssh && resolved.UseSshAgent.Value && !skipKeys
                && !string.IsNullOrWhiteSpace(username) && await AgentHasKeysAsync();
            if (password is null && protocol == Protocol.Ssh && !skipKeys && !string.IsNullOrWhiteSpace(username) && (keyFile is not null || useAgent))
            {
                ConnectLog.LogInformation("\"{Name}\": logging in as {User} with {Keys}", entry.Name, username, KeysIn(keyFile, useAgent));
                StartSshWithKeys(origin, new ConnectionTarget(entry.Host, resolved.Port.Value, username, domain),
                    whenRefused: () => ConnectAsync(entry, skipKeys: true, files), keyFile, useAgent, resolved.AllowLegacySsh.Value, route);
                return;
            }

            if (password is null)
            {
                var prompt = await new ConnectPromptWindow(_credentials, entry.Name, $"{entry.Host}:{resolved.Port.Value}", username, domain, offerRemember: true)
                    .ShowDialog<ConnectPromptResult?>(this);
                if (prompt is null)
                {
                    ConnectLog.LogDebug("\"{Name}\": the login prompt was canceled", entry.Name);
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
                if (prompt.CredentialId is { } chosenId)
                {
                    var (chosenRead, chosenCredential) = await ReadSavedCredentialAsync(chosenId);
                    if (!chosenRead)
                    {
                        return;
                    }

                    saved = chosenCredential;
                }
            }

            var target = new ConnectionTarget(entry.Host, resolved.Port.Value, username, domain);
            // Checked when saved; a hand-edited file that doesn't parse just fits the window.
            DesktopResolution.TryParse(resolved.DesktopSize.Value, out var desktopSize, out _);
            await StartSessionAsync(origin, target, password, saved, resolved.ShareClipboard.Value, resolved.AllowLegacySsh.Value, route, desktopSize,
                resolved.EnlargeRemoteDesktop.Value, resolved.DisplayQuality.Value, keyFile, resolved.ViewOnly.Value, resolved.Scaling.Value);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Jump hosts, the key file, the prompt or the session itself: reading the credential says so itself.
            await Dialogs.ShowMessageAsync(this, $"Couldn't connect to \"{entry.Name}\"", e.Message);
        }
    }

    /// <summary>
    /// The saved credential <paramref name="id"/>, null when it's been deleted; Read is false, after saying so, when
    /// the store couldn't be read.
    /// </summary>
    async Task<(bool Read, Credential? Credential)> ReadSavedCredentialAsync(Guid id)
    {
        try
        {
            return (true, await _credentials.GetAsync(id));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't read the saved credential", e.Message);
            return (false, null);
        }
    }

    async Task ConnectDirectAsync(QuickConnectChoice choice, bool skipAgent = false)
    {
        var host = choice.Host!;
        if (ProtocolInfo.IsWeb(choice.Protocol))
        {
            await OpenWebPageAsync(host, null, choice.Protocol, host, choice.Port);
            return;
        }

        var origin = new SessionOrigin(host, choice.Protocol, null, () => ConnectDirectAsync(choice), AutoReconnect: true)
        {
            ReachableDirectly = ChecksHostDirectly(choice.Protocol),
        };
        if (choice.Protocol == Protocol.Serial)
        {
            StartSerial(origin, host, SerialLine.Default);
            return;
        }

        if (choice.Protocol == Protocol.Telnet)
        {
            StartTelnet(origin, new ConnectionTarget(host, choice.Port, choice.Username, null));
            return;
        }

        if (choice.Protocol == Protocol.Ssh && !skipAgent && !string.IsNullOrWhiteSpace(choice.Username) && await AgentHasKeysAsync())
        {
            StartSshWithKeys(origin, new ConnectionTarget(host, choice.Port, choice.Username, null),
                whenRefused: () => ConnectDirectAsync(choice, skipAgent: true), keyFile: null, useAgent: true);
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

    /// <param name="viewOnly">VNC: open watching only.</param>
    /// <param name="scaling">VNC: how the desktop is fitted into the tab.</param>
    Task StartSessionAsync(SessionOrigin origin, ConnectionTarget target, string password, Credential? saved,
        bool shareClipboard = true, bool allowLegacySsh = false, SshRoute? route = null, (int Width, int Height)? desktopSize = null, bool? enlarge = null,
        DisplayQuality quality = DisplayQuality.Balanced, KeyFileLogin? keyFile = null, bool viewOnly = false, Orrery.Core.Connections.DesktopScaling scaling = Orrery.Core.Connections.DesktopScaling.Fit)
    {
        switch (origin.Protocol)
        {
            case Protocol.Ssh:
                return StartSshAsync(origin, target, password, saved, allowLegacySsh, route, keyFile);
            case Protocol.Vnc:
                StartVnc(origin, target, password, shareClipboard, enlarge, route?.Jumps, quality, viewOnly, scaling);
                return Task.CompletedTask;
            default:
                StartRdp(origin, target, password, shareClipboard, desktopSize, enlarge, route?.Jumps, quality, scaling);
                return Task.CompletedTask;
        }
    }

    /// <summary>VNC servers set their own desktop size; the view shrinks it to fit and zooms it like RDP.</summary>
    /// <param name="enlarge">The connection's own choice; null uses the app setting.</param>
    /// <param name="jumps">SSH jump hosts to tunnel through; directly when there are none.</param>
    /// <param name="viewOnly">Open watching only, as the session menu's View only does.</param>
    void StartVnc(SessionOrigin origin, ConnectionTarget target, string password, bool shareClipboard, bool? enlarge = null, IReadOnlyList<SshHop>? jumps = null,
        DisplayQuality quality = DisplayQuality.Balanced, bool viewOnly = false, Orrery.Core.Connections.DesktopScaling scaling = Orrery.Core.Connections.DesktopScaling.Fit)
    {
        Open(origin, () =>
        {
            var session = VncSession.Start(target, password, shareClipboard, route: Tunnel(jumps), quality: quality);
            var view = NewRemoteFrameView(origin, session, enlarge ?? _settingsStore.Load().ZoomRemoteDesktops, followsViewSize: true);
            view.ViewOnly = viewOnly;
            view.Scaling = scaling;
            return (view, session);
        });
        UpdateVaultButton();
    }

    /// <param name="fixedSize">A desktop size to keep; null fits the session area and follows it.</param>
    /// <param name="enlarge">The connection's own choice; null uses the app setting. A desktop that follows the view
    /// is matched to this display's scaling by telling the server it, as mstsc does, so it stays pixel for pixel; a
    /// fixed size is enlarged instead.</param>
    /// <param name="jumps">SSH jump hosts to tunnel through; directly when there are none.</param>
    /// <param name="scaling">How a desktop that doesn't match the tab is fitted into it.</param>
    void StartRdp(SessionOrigin origin, ConnectionTarget target, string password, bool shareClipboard, (int Width, int Height)? fixedSize = null, bool? enlarge = null,
        IReadOnlyList<SshHop>? jumps = null, DisplayQuality quality = DisplayQuality.Balanced,
        Orrery.Core.Connections.DesktopScaling scaling = Orrery.Core.Connections.DesktopScaling.Fit)
    {
        Open(origin, () =>
        {
            bool matchScaling = enlarge ?? _settingsStore.Load().ZoomRemoteDesktops;
            bool tellServer = matchScaling && fixedSize is null;
            bool zoom = matchScaling && !tellServer;
            var (width, height) = fixedSize ?? DesktopSizeForView(zoom);
            var problems = new List<string>();
            var options = RdpOptionsFor(origin, problems);
            var session = RdpSession.Start(target, password, width, height, _certificateValidator, shareClipboard, Tunnel(jumps), quality, options,
                tellServer ? RenderScaling : null);
            var view = NewRemoteFrameView(origin, session, zoom, followsViewSize: fixedSize is null);
            view.Scaling = scaling;
            if (problems.Count > 0)
            {
                // Said once the desktop shows, where it's seen, rather than over the connecting screen.
                void OnConnected(object? sender, SessionStateChangedEventArgs e)
                {
                    if (e.State == SessionState.Connected)
                    {
                        session.StateChanged -= OnConnected;
                        Dispatcher.UIThread.Post(() => view.ShowNotice(string.Join(" ", problems)));
                    }
                }

                session.StateChanged += OnConnected;
            }

            return (view, session);
        });
        UpdateVaultButton();
    }

    /// <summary>
    /// A saved connection's sound, shared folders and console session, with what can't be shared added to
    /// <paramref name="problems"/>; Orrery's defaults for a host typed into quick connect.
    /// </summary>
    RdpOptions RdpOptionsFor(SessionOrigin origin, List<string> problems)
    {
        if (origin.EntryId is not { } id || _tree.Find(id) is not { } node)
        {
            return new RdpOptions();
        }

        var resolved = SettingsResolver.Resolve(node);
        var drives = RdpDrives.ForThisComputer(resolved.ShareAllDrives.Value, resolved.SharedFolders.Value, problems);
        foreach (var problem in problems)
        {
            ConnectLog.LogWarning("{Name}: {Problem}", origin.Title, problem);
        }

        return new RdpOptions
        {
            PlayAudio = resolved.RemoteAudio.Value == Orrery.Core.Connections.RemoteAudio.PlayHere,
            Drives = drives,
            AdminSession = resolved.AdminSession.Value,
        };
    }

    /// <summary>A remote desktop view with the color correction from Settings, unless its connection turns that off.</summary>
    RemoteFrameView NewRemoteFrameView(SessionOrigin origin, IFrameBufferSession session, bool zoom, bool followsViewSize)
    {
        var settings = _settingsStore.Load();
        return new RemoteFrameView(session, zoom)
        {
            FollowsViewSize = followsViewSize,
            AllowsColorCorrection = AllowsColorCorrection(origin),
            Correction = ColorCorrection.For(settings.Vision, settings.CorrectionStrength),
        };
    }

    /// <summary>The recording playing now takes the color vision, correction and terminal colors just saved in Settings.</summary>
    void FollowColorsInPlayer()
    {
        var settings = _settingsStore.Load();
        _recordingsPanel.Stage.Player?.SetColors(settings.Vision, ColorCorrection.For(settings.Vision, settings.CorrectionStrength),
            settings.TerminalColors ?? TerminalColorScheme.Standard);
    }

    void OnLocalTerminalClick(object? sender, RoutedEventArgs e) => OpenLocalTerminal();

    /// <summary>
    /// A shell on this computer in a new tab, starting in the home folder: <paramref name="shellId"/>, or the one
    /// chosen in settings. Duplicating or restarting the tab opens the same shell again.
    /// </summary>
    /// <param name="entry">The saved connection it's opened from, which names the tab and says where it starts.</param>
    void OpenLocalTerminal(string? shellId = null, bool elevated = false, ConnectionEntry? entry = null)
    {
        var shell = LocalShells.Find(shellId ?? _settingsStore.Load().LocalShell);
        elevated &= OperatingSystem.IsWindows();
        var name = entry?.Name ?? shell.Name;
        var folder = StartFolder(entry?.StartFolder);
        var origin = new SessionOrigin(elevated ? $"{name} (Administrator)" : name, Protocol.Ssh, entry?.Id, () =>
        {
            OpenLocalTerminal(shell.Id, elevated, entry);
            return Task.CompletedTask;
        })
        {
            Local = true,
            LocalShell = shell.Id,
            Elevated = elevated,
        };
        LocalTerminalSession? started = null;
        Open(origin, () =>
        {
            var session = started = LocalTerminalSession.Start(shell, 80, 24, folder, elevated);
            return (NewTerminalView(session), session);
        });
        if (elevated && _tabs.LastOrDefault(t => t.Session == started) is { } tab)
        {
            tab.Warning.Show(ElevatedWarning);
        }
    }

    /// <summary>A saved local shell's starting folder, with ~ for the home folder; null (the home folder) when it's unset or gone.</summary>
    internal static string? StartFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        folder = folder.Trim();
        if (folder == "~" || folder.StartsWith("~/", StringComparison.Ordinal) || folder.StartsWith("~\\", StringComparison.Ordinal))
        {
            folder = Path.Combine(home, folder.Length > 2 ? folder[2..] : string.Empty);
        }

        return Directory.Exists(folder) ? folder : null;
    }

    internal const string ElevatedWarning = "Administrator shell. Whatever it runs runs as administrator, and while this tab is open, "
        + "anything else running as you could reach it through Orrery. Close it when you're done.";

    void OnElevatedTerminalClick(object? sender, RoutedEventArgs e) => OpenLocalTerminal(elevated: true);

    void StartTelnet(SessionOrigin origin, ConnectionTarget target, IReadOnlyList<SshHop>? jumps = null) => Open(origin, () =>
    {
        var session = TelnetSession.Start(target, 80, 24, Tunnel(jumps));
        return (NewTerminalView(session), session);
    });

    /// <summary>A terminal on a serial port of this computer, such as a switch's console cable.</summary>
    void StartSerial(SessionOrigin origin, string portName, SerialLine line) => Open(origin, () =>
    {
        var session = SerialSession.Start(new ConnectionTarget(portName, 0, null, null), line);
        return (NewTerminalView(session), session);
    });

    /// <summary>
    /// Opens a web connection: in a tab, or in the default browser if the connection or Settings says so. Opening it
    /// counts as connecting in the history and the Recent list. A jump host can't be used, since neither the tab nor
    /// the browser would go through it, so one set for it stops the page opening rather than going around it.
    /// </summary>
    /// <param name="entryId">The saved connection, or null for one typed into quick connect.</param>
    async Task OpenWebPageAsync(string title, Guid? entryId, Protocol protocol, string host, int port, bool behindJumpHost = false)
    {
        if (behindJumpHost)
        {
            await Dialogs.ShowMessageAsync(this, $"Can't open \"{title}\" through a jump host",
                "Web pages can't go through Orrery's jump hosts, in a tab or in your browser. Set this connection to connect directly, "
                + "or give the jump host a SOCKS proxy (D 1080) and set your browser to use it.");
            return;
        }

        if (WebAddress.Url(protocol, host, port) is not { } url)
        {
            await Dialogs.ShowMessageAsync(this, $"Can't open \"{title}\"",
                "Its host isn't a web address Orrery can open: a name or IP address, with a path after it if you like, such as router.lan/admin.");
            return;
        }

        if (WebPagesInOrrery(entryId))
        {
            ConnectLog.LogInformation("Opening \"{Name}\" in a tab: {Site}", title, url.GetLeftPart(UriPartial.Authority));
            // The tab adds itself to Recent, and to the history once its session is connected, which it is at once.
            OpenWebTab(new SessionOrigin(title, protocol, entryId, () => OpenWebPageAsync(title, entryId, protocol, host, port)),
                url, new ConnectionTarget(host, port, null, null));
            return;
        }

        // Only the scheme, host and port: a page's path or query can carry tokens.
        ConnectLog.LogInformation("Opening \"{Name}\" in the browser: {Site}", title, url.GetLeftPart(UriPartial.Authority));
        if (OpenInBrowser(url) is { } problem)
        {
            ConnectLog.LogWarning("Couldn't open the browser for \"{Name}\": {Message}", title, problem);
            if (entryId is { } failed && _tree.Find(failed) is ConnectionEntry)
            {
                _history.RecordFailed(failed, DateTimeOffset.Now);
            }

            await Dialogs.ShowMessageAsync(this, "Couldn't open the browser", problem);
            return;
        }

        if (entryId is { } id && _tree.Find(id) is ConnectionEntry)
        {
            _history.RecordConnected(id, DateTimeOffset.Now);
        }

        _recent.Add(new RecentConnection(entryId, title, protocol, host, port, null, DateTimeOffset.Now));
    }

    /// <summary>
    /// A web page in a new tab, with the color correction from Settings unless its connection turns that off (a pop-up
    /// follows the page it came from). Its sign-ins and cookies are kept in <c>web</c> in Orrery's data folder.
    /// </summary>
    void OpenWebTab(SessionOrigin origin, Uri url, ConnectionTarget target, bool? allowsCorrection = null)
    {
        var settings = _settingsStore.Load();
        var view = new WebPageView(url, WebData.FolderIn(_location.DataDir))
        {
            AllowsColorCorrection = allowsCorrection ?? AllowsColorCorrection(origin),
            Correction = ColorCorrection.For(settings.Vision, settings.CorrectionStrength),
        };
        view.NewWindowRequested += (_, address) => OpenPopUpTab(address, view.AllowsColorCorrection);
        AddTab(view, new WebSession(target, view.Frames), origin);
    }

    /// <summary>The "Color correction" of the connection a tab was opened from; on for one that isn't a saved connection.</summary>
    bool AllowsColorCorrection(SessionOrigin origin) =>
        origin.EntryId is not { } id || _tree.Find(id) is not { } node || SettingsResolver.Resolve(node).ColorCorrection.Value;

    /// <summary>
    /// A page's pop-up or new-window link, in a tab beside it. Like a management page it isn't a connection: it isn't
    /// added to Recent and isn't reopened with the workspace.
    /// </summary>
    void OpenPopUpTab(Uri address, bool allowsCorrection)
    {
        var protocol = address.Scheme == Uri.UriSchemeHttps ? Protocol.Https : Protocol.Http;
        OpenWebTab(new SessionOrigin(address.Host, protocol, null, () =>
        {
            OpenPopUpTab(address, allowsCorrection);
            return Task.CompletedTask;
        }) { ManagementPage = true }, address, new ConnectionTarget(address.Host, address.Port, null, null), allowsCorrection);
    }

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
    /// Logs in with the key file, then the agent's keys, and no password. If the server refuses them all, the tab
    /// closes and <paramref name="whenRefused"/> asks for a login the usual way.
    /// </summary>
    void StartSshWithKeys(SessionOrigin origin, ConnectionTarget target, Func<Task> whenRefused, KeyFileLogin? keyFile, bool useAgent,
        bool allowLegacySsh = false, SshRoute? route = null)
    {
        Open(origin, () =>
        {
            var login = new SshLogin(target.Username!, null, UseAgent: useAgent, KeyFile: keyFile?.Path, KeyFilePassphrase: keyFile?.Passphrase);
            var (view, session, rejected) = NewSshView(origin, target, login, allowLegacySsh, route);
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
    async Task StartSshAsync(SessionOrigin origin, ConnectionTarget target, string password, Credential? saved, bool allowLegacySsh, SshRoute? route,
        KeyFileLogin? keyFile = null)
    {
        if (string.IsNullOrWhiteSpace(target.Username))
        {
            await Dialogs.ShowMessageAsync(this, "SSH needs a username", "Set a username on the connection or its saved credential, then connect again.");
            return;
        }

        var login = SshLoginFor(target.Username, password, saved, keyFile);
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
            var files = SftpSession.Start(target, login, _hostKeyValidator, allowLegacySsh, route?.Jumps, route?.KeepAlive);
            return (new FileBrowserView(files), files, () => files.AuthenticationRejected);
        }

        var shell = SshSession.Start(target, login, _hostKeyValidator, 80, 24, allowLegacySsh, route?.Jumps, route?.Forwards, route?.KeepAlive);
        return (NewTerminalView(shell), shell, () => shell.AuthenticationRejected);
    }

    /// <summary>
    /// A saved SSH-key credential logs in with its key instead of the password. The connection's key file, if it has
    /// one, comes after that and before the password.
    /// </summary>
    static SshLogin SshLoginFor(string username, string? password, Credential? saved, KeyFileLogin? keyFile = null) =>
        saved?.Kind is CredentialKind.SshKey or CredentialKind.SshKeyWithPassphrase
            ? new SshLogin(username, null, saved.Secret, saved.Passphrase, KeyFile: keyFile?.Path, KeyFilePassphrase: keyFile?.Passphrase)
            : new SshLogin(username, password, KeyFile: keyFile?.Path, KeyFilePassphrase: keyFile?.Passphrase);

    /// <summary>A connection's key file and the passphrase it needs, if any, which is held only for this session.</summary>
    sealed record KeyFileLogin(string Path, string? Passphrase)
    {
        public override string ToString() => "KeyFileLogin";
    }

    /// <summary>What the log says a login uses: the key file by its name only, and the agent.</summary>
    static string KeysIn(KeyFileLogin? keyFile, bool useAgent) => (keyFile, useAgent) switch
    {
        ({ } file, true) => $"the key file {SshKeyFile.NameOf(file.Path)} and the SSH agent's keys",
        ({ } file, false) => $"the key file {SshKeyFile.NameOf(file.Path)}",
        _ => "the SSH agent's keys",
    };

    /// <summary>
    /// Gets <paramref name="path"/> ready to log in with. Only an encrypted key needs anything: its passphrase, from
    /// the credential store if one was saved there (and the store is open), else asked for until it opens the key.
    /// A key file that is missing or unusable isn't dealt with here; the session says so on the connection-lost
    /// screen. Null if the passphrase prompt was canceled.
    /// </summary>
    /// <param name="what">Which connection wants it, for the prompt.</param>
    async Task<KeyFileLogin?> PrepareKeyFileAsync(string path, string what)
    {
        string? passphrase = null, toSave = null;
        bool wrong = false, lookedForSaved = false;
        while (true)
        {
            var problem = await Task.Run(() => SshKeyFile.Check(path, passphrase));
            if (problem is not { Problem: KeyFileProblem.NeedsPassphrase or KeyFileProblem.WrongPassphrase })
            {
                if (toSave is not null && problem is null)
                {
                    await SavePassphraseAsync(path, toSave);
                }

                return new KeyFileLogin(path, passphrase);
            }

            wrong = problem.Problem == KeyFileProblem.WrongPassphrase;
            if (!lookedForSaved && !wrong)
            {
                lookedForSaved = true;
                if (await SavedPassphraseAsync(path) is { } saved)
                {
                    passphrase = saved;
                    continue;
                }
            }

            var answer = await new KeyPassphraseWindow(what, SshKeyFile.NameOf(path), wrong, offerSave: true).ShowDialog<KeyPassphraseResult?>(this);
            if (answer is null)
            {
                return null;
            }

            passphrase = answer.Passphrase;
            toSave = answer.Save && passphrase.Length > 0 ? passphrase : null;
        }
    }

    /// <summary>The passphrase saved for this key file, when the credential store is open and has one. Never asks to unlock it.</summary>
    async Task<string?> SavedPassphraseAsync(string path)
    {
        if (_credentials.IsLocked)
        {
            return null;
        }

        try
        {
            return (await _credentials.GetAsync(SshKeyFile.PassphraseCredentialId(path)))?.Secret;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ConnectLog.LogWarning("Couldn't look for a saved passphrase for {Key}: {Message}", SshKeyFile.NameOf(path), e.Message);
            return null;
        }
    }

    /// <summary>
    /// Saves a key file's passphrase as a credential, found again by the file's path (see
    /// <see cref="SshKeyFile.PassphraseCredentialId"/>), the way a typed password is saved from the login prompt.
    /// </summary>
    async Task SavePassphraseAsync(string path, string passphrase)
    {
        try
        {
            if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
            {
                return;
            }

            var name = SshKeyFile.NameOf(path);
            await _credentials.SaveAsync(new Credential(SshKeyFile.PassphraseCredentialId(path), $"Passphrase for {name}", CredentialKind.Password, null, null, passphrase,
                Notes: "The passphrase of a key file, saved when connecting. Orrery finds it again by the file's path."));
            ConnectLog.LogInformation("Saved the passphrase for the key file {Key} as a credential", name);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't save the passphrase", e.Message);
        }
    }

    /// <summary>What an SSH session goes through and opens besides its shell, and how often it sends keep-alives (null: the default).</summary>
    sealed record SshRoute(IReadOnlyList<SshHop> Jumps, IReadOnlyList<PortForward> Forwards, TimeSpan? KeepAlive = null);

    static readonly ILogger ConnectLog = Log.For("Connect");
    static readonly ILogger StorageLog = Log.For("Storage");
    static readonly ILogger WakeLog = Log.For("Wake");

    /// <summary>A username as the log shows it, with its domain when there is one.</summary>
    static string Account(string? username, string? domain) =>
        string.IsNullOrEmpty(username) ? "(no username)" : string.IsNullOrEmpty(domain) ? username : $"{domain}\\{username}";

    /// <summary>
    /// The jump hosts on the way to <paramref name="entry"/>, each with its login: its saved credential, else the
    /// SSH agent's keys, else asked for now. Null when the chain is broken or a prompt was canceled.
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

            KeyFileLogin? keyFile = null;
            if (resolved.KeyFile.Value is { } keyPath)
            {
                if (await PrepareKeyFileAsync(keyPath, $"Jump host {jump.Name}") is not { } prepared)
                {
                    return null;
                }

                keyFile = prepared;
            }

            SshLogin login;
            bool useAgent = saved is null && resolved.UseSshAgent.Value && !string.IsNullOrWhiteSpace(username) && await AgentHasKeysAsync();
            if (saved is null && !string.IsNullOrWhiteSpace(username) && (keyFile is not null || useAgent))
            {
                login = new SshLogin(username, null, UseAgent: useAgent, KeyFile: keyFile?.Path, KeyFilePassphrase: keyFile?.Passphrase);
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

                login = SshLoginFor(username, password, saved, keyFile);
            }

            hops.Add(new SshHop(new ConnectionTarget(jump.Host, resolved.Port.Value, username, null), login, resolved.AllowLegacySsh.Value,
                SshSession.KeepAliveFor(resolved.KeepAliveSeconds.Value)));
        }

        return hops;
    }

    /// <summary>A terminal at the saved text size and colors; Ctrl+= and Ctrl+- in any terminal resize them all.</summary>
    TerminalView NewTerminalView(ITerminalSession session)
    {
        var settings = _settingsStore.Load();
        var view = new TerminalView(session)
        {
            Margin = TerminalMargin,
            TextSize = settings.TerminalFontSize ?? AppSettings.DefaultTerminalFontSize,
            ColorVision = settings.Vision,
            ColorScheme = settings.TerminalColors ?? TerminalColorScheme.Standard,
            Announcements = Controls.ScreenReader.TerminalAnnouncements(settings),
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
        if (!origin.Local && !origin.ManagementPage)
        {
            _recent.Add(new RecentConnection(origin.EntryId, origin.Title, origin.Protocol,
                session.Target.Host, session.Target.Port, session.Target.Username, DateTimeOffset.Now));
        }

        _tabs.Add(tab);
        _tabStrip.Children.Add(tab.Header);
        _sessionHost.Children.Add(tab.Host);
        if (RecordsBySetting(origin))
        {
            _ = StartRecordingAsync(tab);
        }

        if (_settingsHost.IsVisible)
        {
            CloseSettings();
        }

        SelectTab(tab);
        UpdateSessionChrome();
    }

    SessionTab NewTab(Control view, IConnectionSession session, SessionOrigin origin, int reconnectAttempt = 0, bool followsDrop = false)
    {
        var tab = new SessionTab(session, view, origin) { ReconnectAttempt = reconnectAttempt, FollowsDrop = followsDrop };
        SessionLogger.Attach(tab);
        tab.Selected += (_, _) => SelectTab(tab);
        tab.CloseRequested += async (_, _) => await CloseTabAsync(session);
        tab.ReconnectRequested += async (_, _) => await ReconnectAsync(tab);
        tab.Header.ContextMenu = TabMenu(tab);
        tab.RecordingStoppedByItself += (_, reason) => ShowNotice(tab, $"{tab.Origin.Title}: {reason}");
        tab.RecordingChanged += (_, _) =>
        {
            RefreshSessionBar(tab);
            if (tab == _currentTab)
            {
                ShowFullScreenRecording();
            }
        };
        HistoryRecorder.Attach(tab, _tree, _history);

        // The bar under the tabs shows the remote desktop's size, which follows the window as it's resized.
        if (view is RemoteFrameView desktop)
        {
            desktop.DesktopResized += (_, _) =>
            {
                if (tab.Popout is { } popout)
                {
                    popout.Detail.Text = DescribeSession(tab);
                }
                else if (tab == _currentTab)
                {
                    _sessionDetail.Text = DescribeSession(tab);
                }
            };
        }

        // A web page's address, loading and back and forward are in that bar too.
        if (view is WebPageView web)
        {
            web.Covered = SessionsCovered;
            web.Changed += (_, _) => RefreshSessionBar(tab);
        }

        // A reconnect is a tab of its own, so each tab sends them once, the first time it connects.
        bool sentWhenConnected = false;
        void SendOnceConnected()
        {
            if (!sentWhenConnected && tab.Session.State == SessionState.Connected)
            {
                sentWhenConnected = true;
                Dispatcher.UIThread.Post(() => _ = SendWhenConnectedAsync(tab));
            }
        }

        tab.StateChanged += (_, _) =>
        {
            UpdateTreeStatus();
            SendOnceConnected();
            if (ShouldReconnect(tab))
            {
                BeginReconnect(tab);
            }
            else if (MayHaveGoneOffline(tab))
            {
                _ = WatchAfterEndAsync(tab);
            }

            RefreshSessionBar(tab);
        };
        SendOnceConnected();
        return tab;
    }

    // ---- Reconnecting by itself ----

    bool ShouldReconnect(SessionTab tab) => tab.CanRetryByItself && !tab.IsReconnecting && _tabs.Contains(tab);

    readonly HostReturnWatch _hostReturn = new(new TcpHostProbe());

    static bool ChecksHostDirectly(Protocol protocol) => protocol is Protocol.Rdp or Protocol.Ssh or Protocol.Vnc or Protocol.Telnet;

    /// <summary>
    /// The server ended the session itself, and it may reconnect by itself: a logoff or an administrator's disconnect,
    /// or the computer restarting or shutting down. Which one shows in whether the host goes on answering.
    /// </summary>
    bool MayHaveGoneOffline(SessionTab tab) =>
        tab.Session.State == SessionState.Disconnected && tab.HasConnected && !tab.EndedByUser && !tab.RetryStopped
        && tab.Origin is { Restart: not null, ReachableDirectly: true } && !tab.IsReconnecting && _tabs.Contains(tab);

    /// <summary>
    /// After the server ended a session: if its host stops answering soon after, waits for it to answer again and
    /// reconnects. One still answering ended the session on purpose, so it's left as it is.
    /// </summary>
    async Task WatchAfterEndAsync(SessionTab tab)
    {
        var target = tab.Session.Target;
        bool wanted() => _tabs.Contains(tab) && !tab.EndedByUser && !tab.RetryStopped && !tab.IsReconnecting;
        try
        {
            if (!await _hostReturn.GoesDownAsync(target.Host, target.Port, wanted, CancellationToken.None) || !wanted())
            {
                return;
            }
        }
        catch (Exception e)
        {
            ConnectLog.LogDebug("Couldn't check whether {Host} went offline: {Message}", target.Host, e.Message);
            return;
        }

        ConnectLog.LogInformation("\"{Name}\": {Host} stopped answering after the session ended; reconnecting when it's back", tab.Origin.Title, target.Host);
        await WaitForHostAsync(tab, sessionEnded: true);
    }

    /// <summary>Shows the tab waiting for its host, and reconnects when the host answers again.</summary>
    async Task WaitForHostAsync(SessionTab tab, bool sessionEnded)
    {
        var target = tab.Session.Target;
        var token = tab.ShowWaitingForHost(sessionEnded);
        bool back;
        try
        {
            back = await _hostReturn.ComesBackAsync(target.Host, target.Port, () => _tabs.Contains(tab), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || !_tabs.Contains(tab))
        {
            return;
        }

        if (!back)
        {
            ConnectLog.LogInformation("\"{Name}\": {Host} didn't answer again within {Minutes} min", tab.Origin.Title, target.Host, HostReturnWatch.WaitAtMost.TotalMinutes);
            tab.ShowStoppedWaiting(HostReturnWatch.WaitAtMost);
            return;
        }

        ConnectLog.LogInformation("\"{Name}\": {Host} is answering again; reconnecting", tab.Origin.Title, target.Host);
        tab.CancelReconnect();
        await RestartTabAsync(tab, 0);
    }

    /// <summary>
    /// A try after a drop failed because the host didn't answer at all: it went offline, restarting say, so rather
    /// than spending the tries, the tab waits for it to answer again.
    /// </summary>
    internal static bool HostStoppedAnswering(SessionTab tab) =>
        tab.Origin.ReachableDirectly && tab.Dropped && !tab.HasConnected
        && tab.Failure is { Kind: FailureKind.Refused or FailureKind.TimedOut or FailureKind.Unreachable };

    /// <param name="fresh">Count from the first try again, as when the network has just come back.</param>
    void BeginReconnect(SessionTab tab, bool fresh = false)
    {
        if (!fresh && HostStoppedAnswering(tab))
        {
            _ = WaitForHostAsync(tab, sessionEnded: false);
            return;
        }

        // A try that got connected starts the count again.
        int attempt = fresh || tab.HasConnected ? 1 : tab.ReconnectAttempt + 1;
        if (attempt > ReconnectSchedule.MaxAttempts)
        {
            tab.ShowGaveUp(ReconnectSchedule.MaxAttempts);
            return;
        }

        var delay = fresh ? NetworkWatch.Settle : ReconnectSchedule.Delay(attempt);
        tab.ShowReconnecting(attempt, ReconnectSchedule.MaxAttempts, delay, () => _ = RestartTabAsync(tab, attempt));
    }

    /// <summary>
    /// The OS says the network is back: sessions counting down try now, and ones that gave up start again, unless
    /// the user pressed Stop or the failure needs them.
    /// </summary>
    void NetworkBack()
    {
        foreach (var tab in _tabs.ToList())
        {
            if (!tab.RetrySoon(TimeSpan.FromSeconds(1)) && ShouldReconnect(tab))
            {
                BeginReconnect(tab, fresh: true);
            }
        }
    }

    /// <summary>
    /// Reconnect, from the connection-lost screen or the toolbar. A session that can reopen itself does so in its
    /// own tab with the same login; one whose login, host key or certificate was refused is opened afresh, so the
    /// login is asked for and the key or certificate shown again.
    /// </summary>
    async Task ReconnectAsync(SessionTab tab)
    {
        // A try already connecting behind the connection-lost screen is left to finish.
        if (!_tabs.Contains(tab) || (tab.IsShowingFailure && tab.Session.State == SessionState.Connecting))
        {
            return;
        }

        tab.CancelReconnect();
        if (tab.Origin.Restart is not null && tab.Failure is not { NeedsUser: true })
        {
            await RestartTabAsync(tab, 0);
            return;
        }

        await CloseTabAsync(tab.Session);
        await tab.Origin.Reconnect();
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
        // Reconnecting after a drop, by itself or from the button, is still about that drop.
        var tab = NewTab(view, session, old.Origin, attempt, old.IsShowingFailure && old.Dropped);

        // The Connection card keeps what happened before, drops included.
        if (tab.Link is { } link && old.Link is { } before)
        {
            link.CarryFrom(before);
        }

        // A muted tab stays muted when it reconnects.
        tab.IsMuted = old.IsMuted;
        tab.LastShown = old.LastShown;

        // The recording carries on in the same file, with the reconnect marked on its timeline.
        if (old.Recorder is { IsRecording: true } recorder)
        {
            old.Recorder = null;
            tab.Recorder = recorder;
            Recording.RecordingService.Bind(recorder, tab);
        }
        if (old.IsShowingFailure)
        {
            tab.ContinueFrom(old, attempt > 0 ? $"Trying again now (try {attempt} of {ReconnectSchedule.MaxAttempts})…" : "Trying again now…");
        }

        bool current = old == _currentTab;
        _tabs[index] = tab;
        if (old.Popout is { } popout)
        {
            // A popped-out session reconnects in its own window.
            old.Popout = null;
            tab.Popout = popout;
            popout.Hold(tab);
            UpdatePopoutBar(popout);
            if (popout.IsActive)
            {
                Dispatcher.UIThread.Post(tab.FocusContent, DispatcherPriority.Background);
            }
        }
        else
        {
            _tabStrip.Children[_tabStrip.Children.IndexOf(old.Header)] = tab.Header;
            _sessionHost.Children[_sessionHost.Children.IndexOf(old.Host)] = tab.Host;
            ReplaceInSplit(old, tab);
            if (current)
            {
                SelectTab(tab);
            }
            else
            {
                tab.IsSelected = false;
                ShowSplit();
            }
        }

        UpdateSessionChrome();
        old.EndedByUser = true;
        await old.Session.DisposeAsync();
    }

    /// <param name="chosen">Chosen or opened, rather than next in line after another closed, so it leaves Recordings.</param>
    void SelectTab(SessionTab? tab, bool chosen = true, bool focus = true)
    {
        PlaceInSplit(_currentTab, tab);
        _currentTab = tab;
        tab?.MarkShown();
        if (tab is not null && chosen && _recordingsShown)
        {
            ShowRecordingsHost(false);
        }

        // A popped-out session is showing in its own window, whichever tab is current here.
        foreach (var each in StripTabs())
        {
            each.IsSelected = each == tab;
        }

        ShowSplit();

        // A pane opened over the sessions to pick this one has done its job; Auto collapses for it.
        if (tab is not null)
        {
            CloseFloatingPanes();
        }

        ApplyPanes();
        UpdateSessionToolbar();
        if (tab is not null)
        {
            tab.Header.BringIntoView();
            if (focus)
            {
                Dispatcher.UIThread.Post(tab.FocusContent, DispatcherPriority.Background);
            }
        }
    }

    async Task CloseTabAsync(IConnectionSession session)
    {
        if (_tabs.FirstOrDefault(t => t.Session == session) is { } tab)
        {
            tab.EndedByUser = true;
            tab.CancelReconnect();
            tab.Recorder?.Stop();
            int index = StripTabs().IndexOf(tab);

            // The other side of a split takes the whole area.
            var partner = SplitPartner(tab);
            if (partner is not null)
            {
                EndSplit(select: false);
            }
            _tabs.Remove(tab);
            if (tab.Popout is { } popout)
            {
                tab.Popout = null;
                popout.Release();
            }
            else
            {
                _tabStrip.Children.Remove(tab.Header);
                _sessionHost.Children.Remove(tab.Host);
            }

            var strip = StripTabs();
            if (strip.Count == 0)
            {
                ExitFullScreen();
            }

            if (tab == _currentTab || partner is not null)
            {
                SelectTab(partner ?? (strip.Count == 0 ? null : strip[Math.Min(index, strip.Count - 1)]), chosen: false);
            }

            UpdateSessionChrome();
        }

        await session.DisposeAsync();
    }

    /// <summary>"RDP · 1920×1080", "SSH · Connecting…": the session's kind and how it is, for the bar under the tabs.</summary>
    static string DescribeSession(SessionTab tab) => $"{tab.Origin.Label} · " + (tab.Session, tab.Session.State) switch
    {
        (ILocalSession, SessionState.Connecting) => "Starting…",
        (ILocalSession { ExitCode: { } code }, SessionState.Disconnected) => $"Exited with code {code}",
        (ILocalSession, SessionState.Disconnected) => "Ended",
        (ILocalSession, SessionState.Failed) => "Couldn't start",
        (ILocalSession, _) => "Running",
        (WebSession, _) => tab.View switch
        {
            WebPageView { Loading: true } => "Loading…",
            WebPageView { Failed: true } => "Didn't load",
            _ => "Loaded",
        },
        (_, SessionState.Connecting) => "Connecting…",
        (_, SessionState.Disconnected) => "Disconnected",
        (_, SessionState.Failed) => tab.HasConnected ? "Connection lost" : "Couldn't connect",
        (IFrameBufferSession { Width: > 0 } frame, _) => $"{frame.Width}×{frame.Height}",
        _ => "Connected",
    } + (tab.View is RemoteFrameView { ViewOnly: true } ? " · View only" : string.Empty);

    const string ClosedLock = "M7 11h10a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2z M8 11V7a4 4 0 0 1 8 0v4";
    const string OpenLock = "M7 11h10a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-6a2 2 0 0 1 2-2z M8 11V7a4 4 0 0 1 7.75-1.4";

    /// <summary>
    /// Whether what's on the wire is encrypted: SSH, RDP and a page whose address is https are; Telnet, plain HTTP and
    /// VNC (only its classic security types are offered) aren't. Null for a local shell or serial port, which cross
    /// no network.
    /// </summary>
    internal static bool? Encrypted(SessionTab tab) => tab switch
    {
        { Origin.Local: true } or { Origin.Protocol: Protocol.Serial } => null,
        { View: WebPageView page } => page.Address.Scheme == Uri.UriSchemeHttps,
        { Origin.Protocol: Protocol.Ssh or Protocol.Rdp } => true,
        _ => false,
    };

    /// <summary>
    /// The lock before the address: closed and green only while an encrypted session is connected, open in amber for
    /// one that isn't encrypted, and not there for a local one.
    /// </summary>
    void ShowLock(SessionTab tab, Controls.StrokeIcon lockIcon)
    {
        bool? encrypted = Encrypted(tab);
        lockIcon.IsVisible = encrypted is not null;
        lockIcon.Data = Geometry.Parse(encrypted == false ? OpenLock : ClosedLock);
        // A page that didn't load, such as one whose certificate isn't trusted, isn't shown as connected.
        bool connected = tab.Session.State == SessionState.Connected && tab.View is not WebPageView { Failed: true };
        lockIcon.Foreground = encrypted == false ? Palette.Warning
            : (IBrush?)this.FindResource(connected ? "OrreryConnected" : "OrreryMuted");
        string said = encrypted == false ? "Not encrypted" : "Encrypted";
        ToolTip.SetTip(lockIcon, said);
        Avalonia.Automation.AutomationProperties.SetName(lockIcon, said);
    }

    /// <summary>The bar under the tabs: where the current session is connected, and what can be done with it.</summary>
    void UpdateSessionToolbar()
    {
        _sessionToolbar.IsVisible = _currentTab is not null && !_fullScreen;
        _properties?.ShowSession(_currentTab);
        _commandEditor?.UpdateSend();
        if (_currentTab is { } tab)
        {
            ShowSessionBar(tab, _sessionAddress, _sessionLock, _sessionDetail, _sessionActions);
        }
        else
        {
            _sessionActions.Children.Clear();
        }
    }

    /// <summary>
    /// Fills a session's bar: the main window's under the tabs, or a popped-out session's above it. Where it's
    /// connected, and what can be done with it.
    /// </summary>
    void ShowSessionBar(SessionTab tab, TextBlock address, Controls.StrokeIcon lockIcon, TextBlock detail, Panel actions)
    {
        actions.Children.Clear();
        var session = tab.Session;
        var state = session.State;
        address.Text = tab.Address;
        ShowLock(tab, lockIcon);
        detail.Text = DescribeSession(tab);
        var popOut = tab.Popout is null
            ? ToolbarIcon(Controls.Icons.PopOut, $"Pop out into its own window ({Input.TabShortcuts.PopOutDescription(OperatingSystem.IsMacOS())})", () => PopOut(tab))
            : ToolbarIcon(Controls.Icons.PopIn, $"Back to the tabs ({Input.TabShortcuts.PopOutDescription(OperatingSystem.IsMacOS())})", () => Rejoin(tab));

        // A web page has a browser's buttons and no connection to end: closing the tab is enough.
        if (tab.View is WebPageView web)
        {
            actions.Children.Add(ToolbarIcon(Controls.Icons.Back, "Back", web.GoBack, web.CanGoBack));
            actions.Children.Add(ToolbarIcon(Controls.Icons.Forward, "Forward", web.GoForward, web.CanGoForward));
            actions.Children.Add(ToolbarIcon(Controls.Icons.Refresh, "Reload", web.Reload));
            if (Recording.RecordingService.Recordable(web)
                && Recording.RecordingControl.For(tab, async () => await StartRecordingAsync(tab), () => StopRecording(tab), () => ShowRecordings()) is { } webRecording)
            {
                actions.Children.Add(webRecording);
            }

            actions.Children.Add(ToolbarIcon(Controls.Icons.External, "Open in the default browser", async () =>
            {
                if (OpenInBrowser(web.Address) is { } problem)
                {
                    await Dialogs.ShowMessageAsync(WindowOf(tab), "Couldn't open the browser", problem);
                }
            }));
            actions.Children.Add(popOut);
            Button webMore = null!;
            webMore = ToolbarIcon(Controls.Icons.More, "More options", () => Controls.PopupMenu.ShowAt(webMore, SessionMenu(tab), PlacementMode.BottomEdgeAlignedRight));
            actions.Children.Add(webMore);
            return;
        }

        // Recording: a REC pill with its settings while it records, a button to start one while connected.
        if ((state == SessionState.Connected || tab.IsRecording) && tab.View is TerminalView or RemoteFrameView
            && Recording.RecordingControl.For(tab, async () => await StartRecordingAsync(tab), () => StopRecording(tab), () => ShowRecordings()) is { } recording)
        {
            actions.Children.Add(recording);
        }

        if (state == SessionState.Connected && tab.View is RemoteFrameView desktop && !desktop.ViewOnly)
        {
            var ctrlAltDel = new Button { Content = "Ctrl+Alt+Del" };
            ToolTip.SetTip(ctrlAltDel, $"Send Ctrl+Alt+Del to the remote computer ({Input.CtrlAltDelShortcut.Description})");
            ctrlAltDel.Click += (_, _) =>
            {
                desktop.SendKeys(RemoteKeyCombo.CtrlAltDel);
                desktop.Focus();
            };
            actions.Children.Add(ctrlAltDel);
        }

        if (state == SessionState.Connected)
        {
            var fullScreen = new Button { Content = Controls.StrokeIcon.Create(Controls.Icons.FullScreen, 15) };
            fullScreen.Classes.Add("icon");
            fullScreen.Classes.Add("small");
            // A popped-out session's bar shows over it in full screen too, so there it's the way out.
            bool leaves = tab.Popout is { IsFullScreen: true };
            string label = leaves ? "Exit full screen" : "Full screen";
            ToolTip.SetTip(fullScreen, $"{label} ({Input.FullScreenShortcut.Describe(_fullScreenShortcut)})");
            Avalonia.Automation.AutomationProperties.SetName(fullScreen, label);
            fullScreen.Click += (_, _) => ToggleFullScreen(tab);
            actions.Children.Add(fullScreen);
        }

        actions.Children.Add(popOut);

        var more = new Button { Content = Controls.StrokeIcon.Create(Controls.Icons.More, 16) };
        more.Classes.Add("icon");
        more.Classes.Add("small");
        ToolTip.SetTip(more, "More options");
        Avalonia.Automation.AutomationProperties.SetName(more, "More options");
        more.Click += (_, _) => Controls.PopupMenu.ShowAt(more, SessionMenu(tab), PlacementMode.BottomEdgeAlignedRight);
        actions.Children.Add(more);

        if (state is SessionState.Connecting or SessionState.Connected)
        {
            var disconnect = new Button { Content = tab.Origin.Local ? "End" : "Disconnect" };
            disconnect.Classes.Add("danger");
            disconnect.Click += async (_, _) =>
            {
                tab.EndedByUser = true;
                await session.DisconnectAsync();
            };
            actions.Children.Add(disconnect);
        }
        else
        {
            var reconnect = new Button { Content = tab.Origin.Local ? "Restart" : "Reconnect" };
            reconnect.Classes.Add("accent");
            reconnect.Click += async (_, _) => await ReconnectAsync(tab);
            actions.Children.Add(reconnect);
        }
    }

    /// <summary>Whether Settings, Recordings, quick connect's dimming or a pane opened over the sessions covers them.</summary>
    bool SessionsCovered => _settingsHost.IsVisible || _dimmer.IsVisible || _recordingsHost.IsVisible
        || (_sidePanel.IsVisible && _sidePanel.ZIndex > 0) || (_propertiesHost.IsVisible && _propertiesHost.ZIndex > 0);

    void CoverWebPages()
    {
        bool covered = SessionsCovered;
        foreach (var web in StripTabs().Select(t => t.View).OfType<WebPageView>())
        {
            web.Covered = covered;
        }
    }

    /// <summary>A small icon button for the session toolbar, named for screen readers by its tip.</summary>
    static Button ToolbarIcon(string icon, string name, Action click, bool enabled = true)
    {
        var button = new Button { Content = Controls.StrokeIcon.Create(icon, 15), IsEnabled = enabled };
        button.Classes.Add("icon");
        button.Classes.Add("small");
        ToolTip.SetTip(button, name);
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
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
                }, combo == RemoteKeyCombo.CtrlAltDel ? Input.CtrlAltDelShortcut.MenuText : null));
            }

            items.Add(keys);
            var viewOnly = MenuItemFor("View only", () =>
            {
                desktop.ViewOnly = !desktop.ViewOnly;
                RefreshSessionBar(tab);
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
            items.Add(MenuItemFor("Find…", () => tab.FindBar?.Open(terminal.SelectedText), mac ? "Cmd+F" : "Ctrl+Shift+F"));
            items.Add(new Separator());
            if (SavedCommandMenuItems(tab, terminal).ToList() is { Count: > 0 } saved)
            {
                items.AddRange(saved);
                items.Add(new Separator());
            }

            items.Add(MenuItemFor("Larger text", () => ChangeTerminalTextSize(1), $"{command}+OemPlus"));
            items.Add(MenuItemFor("Smaller text", () => ChangeTerminalTextSize(-1), $"{command}+OemMinus"));
            items.Add(MenuItemFor("Reset text size", () => ChangeTerminalTextSize(0), $"{command}+D0"));
            items.Add(new Separator());
            items.Add(MenuItemFor("Save output…", async () => await SaveOutputAsync(tab, terminal)));
            items.Add(MenuItemFor("Clear scrollback", terminal.ClearScrollback));
        }
        else if (tab.View is WebPageView page)
        {
            items.Add(MenuItemFor("Print…", async () =>
            {
                try
                {
                    page.Print();
                }
                catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
                {
                    await Dialogs.ShowMessageAsync(this, "Couldn't print the page", e.Message);
                }
            }, enabled: !page.Loading));
            items.Add(MenuItemFor("Save as PDF…", async () => await SavePdfAsync(tab, page), enabled: !page.Loading));
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
                foreach (var other in StripTabs().Where(t => t != tab).ToList())
                {
                    await CloseTabAsync(other.Session);
                }
            }, enabled: StripTabs().Count > 1));
        };
        return menu;
    }

    /// <summary>
    /// What applies to any session: fill the screen, pop it out or put it back, open it again alongside, or start it over.
    /// </summary>
    IEnumerable<Control> TabActions(SessionTab tab)
    {
        bool mac = OperatingSystem.IsMacOS();
        yield return MenuItemFor(tab.Popout is { IsFullScreen: true } ? "Exit full screen" : "Full screen", () => ToggleFullScreen(tab),
            Input.FullScreenShortcut.Format(_fullScreenShortcut), tab.Session.State == SessionState.Connected);

        if (Screens.All.Count > 1)
        {
            var current = Screens.ScreenFromWindow(WindowOf(tab));
            var on = MenuItemFor("Full screen on", null, enabled: tab.Session.State == SessionState.Connected);

            // Spreading over displays takes the main window's own full screen.
            if (tab.View is RemoteFrameView && OperatingSystem.IsWindows() && tab.Popout is null)
            {
                on.Items.Add(MenuItemFor("Every display", () =>
                {
                    SelectTab(tab);
                    EnterFullScreen(choice: FullScreenChoice.EveryDisplay);
                }));
                on.Items.Add(new Separator());
            }

            foreach (var (screen, i) in Screens.All.Select((s, i) => (s, i)))
            {
                var label = $"Display {i + 1}  ({screen.Bounds.Width}×{screen.Bounds.Height})"
                    + (screen.IsPrimary ? " · main" : string.Empty)
                    + (Equals(screen.Bounds, current?.Bounds) ? " · this window" : string.Empty);
                on.Items.Add(MenuItemFor(label, () => ToggleFullScreen(tab, screen)));
            }

            yield return on;
        }

        yield return tab.Popout is null
            ? MenuItemFor("Pop out", () => PopOut(tab), Input.TabShortcuts.PopOutDescription(mac))
            : MenuItemFor("Back to tabs", () => Rejoin(tab), Input.TabShortcuts.PopOutDescription(mac));

        if (SplitPartner(tab) is not null)
        {
            yield return MenuItemFor("End side by side", () => EndSplit(tab), Input.TabShortcuts.SplitDescription(mac));
        }
        else if (tab.Popout is null && StripTabs() is { Count: > 1 } strip)
        {
            yield return tab != _currentTab || _currentTab is null
                ? MenuItemFor("Side by side with the current tab", () => SplitWith(tab, onRight: true))
                : MenuItemFor(strip[^1] == tab ? "Side by side with the previous tab" : "Side by side with the next tab",
                    () => ToggleSplit(tab), Input.TabShortcuts.SplitDescription(mac));
        }

        if (tab.Origin is { Protocol: Protocol.Ssh, EntryId: { } id } && _tree.Find(id) is ConnectionEntry entry)
        {
            yield return tab.Origin.Files
                ? MenuItemFor("Open a terminal", async () => await ConnectAsync(entry))
                : MenuItemFor("Browse files (SFTP)", async () => await ConnectAsync(entry, files: true));
        }

        if (Recording.RecordingService.Recordable(tab.View))
        {
            yield return tab.IsRecording
                ? MenuItemFor("Stop recording", () => StopRecording(tab))
                : MenuItemFor("Start recording", async () => await StartRecordingAsync(tab));
        }

        if (tab.CanMute)
        {
            yield return tab.IsMuted
                ? MenuItemFor("Unmute tab", () => tab.IsMuted = false)
                : MenuItemFor("Mute tab", () => tab.IsMuted = true);
        }

        yield return MenuItemFor("Duplicate session", async () => await tab.Origin.Reconnect());
        yield return MenuItemFor(tab.Origin.Local ? "Restart" : "Reconnect", async () =>
        {
            await CloseTabAsync(tab.Session);
            await tab.Origin.Reconnect();
        });
    }

    // ---- Recording (docs/design-session-recording.md) ----

    /// <summary>
    /// Whether the connection a tab opened says to record it; a quick connection follows the defaults for all
    /// connections. A local shell, a file browser, a management page or a page's pop-up isn't recorded by a setting,
    /// nor a web page where pages can't be recorded (Linux and macOS).
    /// </summary>
    bool RecordsBySetting(SessionOrigin origin) => origin is { Files: false, Local: false, ManagementPage: false }
        && (!ProtocolInfo.IsWeb(origin.Protocol) || WebFrames.Supported)
        && (origin.EntryId is { } id && _tree.Find(id) is { } node
            ? SettingsResolver.Resolve(node).RecordSessions.Value
            : _tree.Root.Settings.RecordSessions == true);

    /// <summary>
    /// Whether a recording of the tab keeps what's typed too: its connection's setting, or the defaults' for a terminal
    /// opened without one (a local shell, a quick connect).
    /// </summary>
    bool RecordsTyping(SessionOrigin origin) =>
        origin.EntryId is { } id && _tree.Find(id) is { } node
            ? SettingsResolver.Resolve(node).RecordTyping.Value
            : _tree.Root.Settings.RecordTyping == true;

    async Task StartRecordingAsync(SessionTab tab)
    {
        if (tab.IsRecording || !_tabs.Contains(tab))
        {
            return;
        }

        if (_settingsStore.Load().RecordingNoteShown != true)
        {
            // Only a terminal's recording keeps what's typed.
            var typing = tab.View is TerminalView && RecordsTyping(tab.Origin);
            if (!await Dialogs.ConfirmAsync(WindowOf(tab), "Recording sessions", Recording.RecordingsPanel.NoteFor(typing), "Record"))
            {
                return;
            }

            _settingsStore.Save(_settingsStore.Load() with { RecordingNoteShown = true });
        }

        // Asked over the window the session is in, its own if it's popped out.
        var (recorder, problem) = await _recordings.StartAsync(tab, WindowOf(tab), recordTyping: RecordsTyping(tab.Origin));
        if (recorder is null)
        {
            ShowNotice(tab, $"{tab.Origin.Title}: {problem}");
            return;
        }

        // The tab may have closed, or been replaced by a reconnect, while the vault was being unlocked.
        if (!_tabs.Contains(tab))
        {
            recorder.Stop();
            return;
        }

        tab.Recorder = recorder;
    }

    void StopRecording(SessionTab tab)
    {
        tab.Recorder?.Stop();
        ShowNotice(tab, $"Stopped recording {tab.Origin.Title}");
    }

    /// <summary>Shows or hides the Recordings section's main area. Hiding it pauses the player, so coming back carries on.</summary>
    void ShowRecordingsHost(bool show)
    {
        _recordingsShown = show;
        _recordingsHost.IsVisible = show;
        if (!show)
        {
            _recordingsPanel.Stage.Pause();
        }

        ApplyPanes();
    }

    /// <summary>Opens the Recordings section: the list in the side panel and the player in the main area.</summary>
    internal void ShowRecordings()
    {
        if (_side != "recordings" || !_sidePanel.IsVisible || _settingsHost.IsVisible || !_recordingsShown)
        {
            OnRailClick(_sides["recordings"].RailButton, new RoutedEventArgs());
        }
        else
        {
            _recordingsPanel.Refresh();
        }
    }

    /// <param name="shortcut">Shown beside the item (the key itself is handled elsewhere), in KeyGesture form.</param>
    static MenuItem MenuItemFor(string header, Action? click, string? shortcut = null, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (shortcut is not null)
        {
            Controls.MenuShortcut.Show(item, KeyGesture.Parse(shortcut.Replace("Cmd", "Meta")));
        }

        if (click is not null)
        {
            item.Click += (_, _) => click();
        }

        return item;
    }

    async Task SaveScreenshotAsync(SessionTab tab, RemoteFrameView desktop)
    {
        var owner = WindowOf(tab);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
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
            await Dialogs.ShowMessageAsync(owner, "Couldn't save the screenshot", e.Message);
        }
    }

    async Task SavePdfAsync(SessionTab tab, WebPageView page)
    {
        var owner = WindowOf(tab);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save page as PDF",
            SuggestedFileName = $"{SafeFileName(tab.Origin.Title)} {DateTime.Now:yyyy-MM-dd HHmmss}.pdf",
            DefaultExtension = "pdf",
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("PDF document") { Patterns = ["*.pdf"] }],
        });
        if (file is null)
        {
            return;
        }

        try
        {
            await using var pdf = await page.PdfAsync();
            await using var stream = await file.OpenWriteAsync();
            await pdf.CopyToAsync(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            await Dialogs.ShowMessageAsync(owner, "Couldn't save the page", e.Message);
        }
    }

    async Task SaveOutputAsync(SessionTab tab, TerminalView terminal)
    {
        var owner = WindowOf(tab);
        var file = await owner.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
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
            await Dialogs.ShowMessageAsync(owner, "Couldn't save the output", e.Message);
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
        _emptyState.IsVisible = StripTabs().Count == 0;
        _emptyStateTitle.Text = _tabs.Count == 0 ? "No open sessions"
            : _tabs.Count == 1 ? "The open session is in its own window" : "The open sessions are in their own windows";
        _sessionCount.Text = _tabs.Count switch { 0 => "No sessions open", 1 => "1 session open", var n => $"{n} sessions open" };
        UpdateTreeStatus();
    }

    /// <summary>Lights the dot beside each saved connection that has a session open.</summary>
    void UpdateTreeStatus()
    {
        var states = new Dictionary<Guid, SessionState>();
        foreach (var tab in _tabs)
        {
            // When one connection is open in two tabs, connected wins over connecting, and both over failed.
            if (tab.Origin.EntryId is { } id && tab.Session.State is SessionState.Connected or SessionState.Connecting or SessionState.Failed
                && (!states.TryGetValue(id, out var shown) || TreeRank(tab.Session.State) > TreeRank(shown)))
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

    static int TreeRank(SessionState state) => state switch
    {
        SessionState.Connected => 3,
        SessionState.Connecting => 2,
        SessionState.Failed => 1,
        _ => 0,
    };

    // ---- Import and export ----

    /// <summary>An mRemoteNG confCons.xml, an Orrery export, a Remote Desktop (.rdp) file or an OpenSSH config.</summary>
    static string? DroppedImportFile(DragEventArgs e) =>
        e.DataTransfer.TryGetFile()?.TryGetLocalPath() is { } path && IsImportFile(path) ? path : null;

    static bool IsImportFile(string path) => RdpFileImporter.IsRdpFile(path) || ConnectionExporter.FormatFor(path) switch
    {
        ExportFormat.MRemoteNg or ExportFormat.Orrery => true,
        null => SshConfigImporter.LooksLikeConfig(path),
        _ => false,
    };

    /// <summary>An SSH config has no extension of its own, so anything that isn't one of the other formats is read as one.</summary>
    static bool IsSshConfig(string path) => ConnectionExporter.FormatFor(path) is null && !RdpFileImporter.IsRdpFile(path);

    /// <summary>A file Import can read, chosen by hand: any of the formats, or anything else read as an SSH config.</summary>
    internal static bool CanImport(string path) => IsImportFile(path) || IsSshConfig(path);

    /// <summary>Why <paramref name="path"/> can't be imported. Export writes CSV, which is the one kind turned away.</summary>
    internal static string WhyNotImported(string path) => ConnectionExporter.FormatFor(path) == ExportFormat.Csv
        ? "Orrery can't import CSV files, which are for spreadsheets. To move connections into another copy of Orrery, "
          + "export them as an Orrery file (.json) and import that."
        : "Orrery can't import this kind of file.";

    async void OnOpenRdpFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a Remote Desktop file",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Remote Desktop files") { Patterns = ["*.rdp"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await OpenRdpFileAsync(path);
        }
    }

    /// <summary>
    /// Connects to what a Remote Desktop (.rdp) file describes, without adding it to the tree; Import adds it. A file
    /// imported before connects as that saved connection, with its saved credential and history.
    /// </summary>
    async Task OpenRdpFileAsync(string path)
    {
        ConnectionEntry file;
        try
        {
            var displays = DisplaysForRdpFiles();
            var result = await Task.Run(() => RdpFileImporter.ImportFile(path, displays));
            file = result.Root.Children.OfType<ConnectionEntry>().Single();
            foreach (var warning in result.Report.Warnings.Concat(result.Report.NotYetSupported))
            {
                ConnectLog.LogInformation("{File}: {Note}", Path.GetFileName(path), warning);
            }
        }
        catch (Exception e) when (e is ImportException or IOException or UnauthorizedAccessException)
        {
            StorageLog.LogWarning("Couldn't open {Path}: {Message}", path, e.Message);
            await Dialogs.ShowMessageAsync(this, "Couldn't open this file", e.Message);
            return;
        }

        if (_tree.AllConnections().FirstOrDefault(c => c.ImportSourceId == file.ImportSourceId) is { } saved)
        {
            Select(saved.Id);
            await ConnectAsync(saved);
            return;
        }

        ConnectLog.LogInformation("Opening the Remote Desktop file {File} without saving it", Path.GetFileName(path));
        await ConnectAsync(file);
    }

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

    async void OnImportMenuClick(object? sender, RoutedEventArgs e) => await ChooseImportFileAsync();

    /// <summary>The kinds of file Import offers to open, for its file picker and first-launch setup's.</summary>
    internal static readonly IReadOnlyList<FilePickerFileType> ImportFileTypes =
    [
        new FilePickerFileType("Connection files") { Patterns = ["*.xml", "*.json", "*.rdp"] },
        new FilePickerFileType("mRemoteNG connections") { Patterns = ["*.xml"] },
        new FilePickerFileType("Orrery export") { Patterns = ["*.json"] },
        new FilePickerFileType("Remote Desktop file") { Patterns = ["*.rdp"] },
        new FilePickerFileType("OpenSSH config") { Patterns = ["config", "*.conf", "*"] },
    ];

    async Task ChooseImportFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import connections",
            AllowMultiple = false,
            FileTypeFilter = ImportFileTypes,
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        if (CanImport(path))
        {
            await ImportAsync(path);
        }
        else
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't import this file", WhyNotImported(path));
        }
    }

    /// <summary>Imports ~/.ssh/config, or says it isn't there.</summary>
    async Task ImportDefaultSshConfigAsync()
    {
        var path = SshConfigImporter.DefaultPath;
        if (File.Exists(path))
        {
            await ImportAsync(path);
        }
        else
        {
            await Dialogs.ShowMessageAsync(this, "No SSH config found",
                $"Orrery looked for:\n{path}\n\nIf your config is somewhere else, use Import and choose the file.");
        }
    }

    /// <summary>
    /// Makes the vault with the master password setup chose, leaving it unlocked for the imports that follow. If it can't
    /// be made, says so; the vault then asks for a password when it's first needed, as without setup.
    /// </summary>
    async Task CreateVaultFromSetupAsync()
    {
        var password = _newVaultPassword;
        _newVaultPassword = null;
        if (password is null || _credentials is not VaultFileStore { Exists: false } vault)
        {
            return;
        }

        try
        {
            await vault.UnlockAsync(password);
            StorageLog.LogInformation("Created the vault chosen in setup");
            UpdateVaultButton();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StorageLog.LogError(e, "Couldn't create the vault chosen in setup");
            await Dialogs.ShowMessageAsync(this, "Couldn't create the vault",
                $"{e.Message}\n\nOrrery will ask for a master password when you first save one.");
        }
    }

    /// <summary>Imports each file in turn, each through its own preview, as if chosen with Import.</summary>
    internal async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            await ImportAsync(path);
        }
    }

    async Task ImportAsync(string path)
    {
        if (!SaveEdits())
        {
            return;
        }

        ImportResult? result = null;
        Exception? failure = null;
        bool orrery = ConnectionExporter.FormatFor(path) == ExportFormat.Orrery;
        bool rdpFile = RdpFileImporter.IsRdpFile(path);
        bool sshConfig = IsSshConfig(path);
        var displays = DisplaysForRdpFiles();
        Task<ImportResult> Read(string? password) => Task.Run(() => ImportReview.WithWarnings(rdpFile ? RdpFileImporter.ImportFile(path, displays)
            : sshConfig ? SshConfigImporter.ImportFile(path)
            : orrery ? OrreryImporter.ImportFile(path, password) : MRemoteNgImporter.ImportFile(path, password)));

        try
        {
            result = await Read(null);
        }
        catch (MasterPasswordRequiredException)
        {
            // The prompt tries each password itself and stays open, saying so, while it's wrong.
            async Task<string?> Check(string password)
            {
                try
                {
                    result = await Read(password);
                }
                catch (MasterPasswordRequiredException e) when (e.WrongPassword)
                {
                    return "That password is incorrect.";
                }
                catch (Exception e)
                {
                    failure = e;
                }

                return null;
            }

            // Named, since setup's imports come one after another and each may ask.
            var name = Path.GetFileName(path);
            var entered = orrery
                ? await Dialogs.PromptPasswordAsync(this, "Export password",
                    $"{name} is an Orrery export that includes saved passwords, so it's encrypted. Enter the password it was exported with.", Check)
                : await Dialogs.PromptPasswordAsync(this, "Master password",
                    $"{name} is protected with an mRemoteNG master password. Enter it to read the connections and saved passwords.", Check);
            if (entered is null)
            {
                return;
            }
        }
        catch (Exception e)
        {
            failure = e;
        }

        if (failure is ImportException or IOException or UnauthorizedAccessException)
        {
            StorageLog.LogWarning("Couldn't import {Path}: {Message}", path, failure.Message);
            await Dialogs.ShowMessageAsync(this, "Couldn't import this file", failure.Message);
            return;
        }

        if (failure is not null || result is null)
        {
            // The importer means to fail only as above; anything else is a bug, not a reason to exit.
            Log.For("App").LogError(failure, "Importing {Path} failed", path);
            await Dialogs.ShowMessageAsync(this, "Couldn't import this file",
                "Orrery couldn't read this file. It may be damaged, or not a file Orrery can import.");
            return;
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

        if (!choice.KeepCommands)
        {
            ImportReview.DropCommands(result.Root);
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
        await AskToCheckImportedHostsAsync(importedFolder, choice.IntoNewFolder);
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

    /// <summary>
    /// Host checks contact every host a file names as soon as its folder is open, so a file from someone else could use
    /// them to see this computer's address or to reach ports on its network. Asked once; no turns them off for what
    /// was imported, which its properties can turn back on.
    /// </summary>
    async Task AskToCheckImportedHostsAsync(ConnectionFolder imported, bool intoNewFolder)
    {
        int count = imported.Descendants().OfType<ConnectionEntry>().Count(e => ProbeTarget.For(e, out _) is not null);
        if (count == 0 || !_settingsStore.Load().ChecksHostStatus)
        {
            return;
        }

        if (await Dialogs.AskAsync(this, "Check whether these hosts are up?",
                $"Orrery can show whether the {(count == 1 ? "host" : $"{count} hosts")} in this file {(count == 1 ? "is" : "are")} up, by "
                + "briefly contacting each one while its folder is open. If the file came from someone else, you may want to wait "
                + "until you've looked through it. Either way, the imported folder's properties can change this later.",
                "Check them", "Don't check"))
        {
            return;
        }

        // Off where the import lands: on its new folder, or on each thing it adds at the top. Nothing below may turn it back on.
        foreach (var node in imported.Descendants())
        {
            node.Settings.CheckHostStatus = null;
        }

        if (intoNewFolder)
        {
            imported.Settings.CheckHostStatus = false;
        }
        else
        {
            // Guests of a host at the top count too: they inherit from the host's folder, which is where these land.
            foreach (var node in imported.Descendants().Where(n => n.Parent == imported))
            {
                node.Settings.CheckHostStatus = false;
            }
        }
    }

    string? WriteImportReport(ImportReport report)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var path = Path.Combine(_location.DataDir, "import", $"{report.From.ToLowerInvariant().Replace(' ', '-')}-{now:yyyyMMdd-HHmmss}.md");
            AtomicFile.WriteAllText(path, report.ToMarkdown(now));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    async void OnExportMenuClick(object? sender, RoutedEventArgs e)
    {
        if (SingleSelectedNode is { } node)
        {
            await ExportAsync(node);
        }
    }

    /// <summary>Saves <paramref name="node"/> (the root for everything) as an Orrery, mRemoteNG or CSV file, chosen by its type.</summary>
    async Task ExportAsync(ConnectionNode node)
    {
        if (!SaveEdits())
        {
            return;
        }

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
        await ShowVaultLockedAsync();
    }

    /// <summary>
    /// What a lock hides, by hand or on idle: the credentials, the inspector's credential picker, and the recordings: the
    /// player holds the recordings key and what it has read, and the list shows connection names only while unlocked.
    /// </summary>
    async Task ShowVaultLockedAsync()
    {
        UpdateVaultButton();
        _credentialsView?.ShowLocked();
        _recordingsPanel?.Stage.Close();
        _recordingsPanel?.Refresh();

        // Keep the inspector's edits, then rebuild it so the credential picker stops listing names. Edits that don't
        // validate can't wait for a lock on idle, so they stay in the pane and only the credential list is redrawn.
        if (_properties is not null && !await _properties.ShowAsync(_properties.Node))
        {
            await _properties.RefreshCredentialsAsync();
        }
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

    /// <summary>
    /// Turns the Recent list and the last connected and failed times on or off. Off deletes what they hold (at
    /// start-up too, in case a file was left behind), and the Recent panel and properties pane say nothing is kept.
    /// </summary>
    void RememberRecentConnections(bool on)
    {
        _recent.Enabled = on;
        _history.Enabled = on;
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
        // Settings covers the inspector, so edits that don't validate keep it until they're fixed.
        if (!_settingsHost.IsVisible && SaveEdits())
        {
            var view = new SettingsView(_location, _settingsStore, _credentials, () =>
            {
                _properties.TrySave();
                SaveNow();
                return Task.CompletedTask;
            }, ChooseImportFileAsync, () => ExportAsync(_tree.Root), ImportDefaultSshConfigAsync, VaultUnlockWindow.Biometric, _updates);
            view.CloseRequested += (_, _) => CloseSettings();
            view.ShowRecordingsRequested += (_, _) => ShowRecordings();
            view.ShowRecordAllSessions(_tree.Root.Settings.RecordSessions == true);
            view.RecordAllSessionsChanged += async (_, on) =>
            {
                // Edits to the defaults go in first, so saving them later doesn't put the old choice back.
                _properties.TrySave();
                _tree.Root.Settings.RecordSessions = on ? true : null;
                SaveNow();
                if (_properties.Node == _tree.Root)
                {
                    await _properties.ShowAsync(_tree.Root);
                }
            };
            view.FullScreenShortcutChanged += (_, shortcut) =>
            {
                _fullScreenShortcut = shortcut;
                UpdateSessionToolbar();
            };
            view.TerminalAnnouncementsChanged += (_, on) =>
            {
                foreach (var terminal in _tabs.Select(t => t.View).OfType<TerminalView>())
                {
                    terminal.Announcements = on;
                }
            };
            view.HostStatusChecksChanged += (_, on) => _hostStatus.Enabled = on;
            view.HostStatusMethodChanged += (_, method) => _hostStatus.Method = method;
            view.RestoreSessionsChanged += (_, mode) =>
            {
                if (mode == RestoreSessionsMode.Never)
                {
                    ForgetOpenTabs();
                }
            };
            view.RememberRecentConnectionsChanged += (_, on) => RememberRecentConnections(on);
            view.NewSessionOnOpenChanged += (_, on) => _newSessionOnOpen = on;
            view.PaneModesChanged += (_, modes) => FollowPaneModes(modes.Side, modes.Inspector);
            view.RestartRequested += (_, arguments) => App.Restart(arguments);
            view.TerminalColorsChanged += (_, scheme) =>
            {
                foreach (var terminal in _tabs.Select(t => t.View).OfType<TerminalView>())
                {
                    terminal.ColorScheme = scheme;
                }

                FollowColorsInPlayer();
            };
            view.ColorVisionChanged += (_, change) =>
            {
                foreach (var terminal in _tabs.Select(t => t.View).OfType<TerminalView>())
                {
                    terminal.ColorVision = change.Vision;
                }

                var correction = ColorCorrection.For(change.Vision, change.Strength);
                foreach (var desktop in _tabs.Select(t => t.View).OfType<RemoteFrameView>())
                {
                    desktop.Correction = correction;
                }

                foreach (var page in _tabs.Select(t => t.View).OfType<WebPageView>())
                {
                    page.Correction = correction;
                }

                FollowColorsInPlayer();
            };
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
        ApplyPanes();
        _currentTab?.FocusContent();
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

        // The old copy's vault has the same key as the one in use, so biometric unlock opens it too.
        var biometric = VaultUnlockWindow.Biometric;
        var biometricNote = biometric is not null && _credentials is VaultFileStore vault && biometric.IsOn(vault)
            ? $"\n\nUntil it's deleted, {biometric.Name} unlock opens the old copy's vault as well."
            : string.Empty;
        var shared = await Task.Run(() => SharedOldCopyNote(
            DataFolderPresence.OthersIn(oldDataDir, ComputerFiles.Tag, DateTimeOffset.UtcNow), ComputerFiles.OtherComputersIn(oldDataDir).Count));
        if (shared is not null)
        {
            Log.For("App").LogInformation("The old copy in {OldDataDir} is shared with other computers; asking before deleting it", oldDataDir);
        }

        if (!await Dialogs.ConfirmAsync(this, "Delete the old copy of your data?",
                $"Orrery now uses\n{_location.DataDir}\n\nThe old copy is still in\n{oldDataDir}\n\n{(shared is null ? "" : shared + "\n\n")}Only Orrery's own files are deleted. Anything else in that folder stays.{biometricNote}", "Delete old copy"))
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

    /// <summary>
    /// For the offer to delete the old copy after Move data: who else uses that folder, as a sync app shares it, from
    /// the computers with Orrery open there now (<paramref name="open"/>) and how many others have left files there
    /// (<paramref name="used"/>, counting those open). Null when none has.
    /// </summary>
    internal static string? SharedOldCopyNote(IReadOnlyList<OtherComputer> open, int used)
    {
        var more = Math.Max(0, used - open.Count);
        if (open.Count == 0 && more == 0)
        {
            return null;
        }

        var who = open.Count == 0
            ? more == 1 ? "Another computer has used that folder too" : $"{more} other computers have used that folder too"
            : $"Orrery is open on {string.Join(" and ", open.Select(o => o.Name))} with that folder now"
                + (more == 0 ? "" : more == 1 ? ", and 1 more computer has used it" : $", and {more} more computers have used it");
        return $"{who}, as a sync app shares it. Deleting the old copy deletes the connections, vault and settings there "
            + "on every computer it syncs to. Keep it while any of them still uses it.";
    }

    static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    void UpdateVaultButton()
    {
        _lockVault.IsVisible = _credentials is VaultFileStore && !_credentials.IsLocked;

        // Recordings' names show only while the store is open.
        if (_side == "recordings" && _sidePanel.IsVisible)
        {
            _recordingsPanel.Refresh();
        }
    }
}
