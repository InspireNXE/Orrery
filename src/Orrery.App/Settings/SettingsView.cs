using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Orrery.App.Controls;
using Orrery.App.Credentials;
using Orrery.App.Input;
using Orrery.App.Logging;
using Orrery.App.Setup;
using Orrery.App.Updates;
using Orrery.Core.Credentials;
using Orrery.Core.Diagnostics;
using Orrery.Core.Network;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Orrery.Core.Updates;
using Orrery.Protocols.Local;

namespace Orrery.App.Settings;

/// <summary>
/// The settings page, shown in the main window over the sessions. A list down the side picks one of a few pages
/// (General, Appearance, Sessions, Data and security, About), each a column of sections; the last one shown is
/// remembered. Changes that swap files
/// out from under the running app end in <see cref="RestartRequested"/> rather than a live switch.
/// </summary>
sealed class SettingsView : UserControl
{
    static readonly (string Label, int Minutes)[] AutoLockChoices =
    [
        ("5 minutes", 5), ("15 minutes", 15), ("30 minutes", 30), ("1 hour", 60), ("4 hours", 240), ("Never", 0),
    ];

    static readonly (string Label, int Minutes)[] UpdateCheckChoices =
    [
        ("30 minutes", 30), ("1 hour", 60), ("4 hours", 240), ("12 hours", 720), ("Day", 1440),
    ];

    static readonly IBrush PageBrush = new SolidColorBrush(Color.Parse("#0E1014")).ToImmutable();

    readonly DataLocation _location;
    readonly AppSettingsStore _settingsStore;
    readonly ICredentialStore _credentials;
    readonly Func<Task> _saveEverything;
    readonly Func<Task> _import;
    readonly Func<Task> _export;
    readonly Func<Task>? _importSshConfig;
    readonly UpdateController? _updates;
    readonly ComboBox _storeChoice = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox _autoLock = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock _autoLockNote = Note(string.Empty);
    readonly ListBox _pageList = new() { Background = Brushes.Transparent, Padding = new Thickness(12, 0), Classes = { "side" } };
    readonly ScrollViewer _pageScroll = new();
    readonly Dictionary<string, Control> _pages = [];
    Action _refreshScreenReaderNote = () => { };
    readonly BiometricUnlock? _biometric;
    readonly CheckBox _biometricSwitch = new();
    readonly TextBlock _biometricNote = Note(string.Empty);
    readonly StackPanel _biometricLeftovers = new() { Spacing = 4, IsVisible = false };

    AppSettings _settings;
    bool _loading = true;
    bool _settingBiometricSwitch;

    /// <param name="saveEverything">Writes pending edits to disk, so a copy of the data folder is complete.</param>
    /// <param name="import">Asks for an mRemoteNG file and imports it.</param>
    /// <param name="importSshConfig">Imports the user's OpenSSH config; the button shows only when this is given.</param>
    /// <param name="biometric">Windows Hello or Touch ID, offered for the vault file; null where there's none.</param>
    /// <param name="updates">The main window's update checks, for Check now; without them the button doesn't show.</param>
    public SettingsView(DataLocation location, AppSettingsStore settingsStore, ICredentialStore credentials, Func<Task> saveEverything, Func<Task> import, Func<Task> export,
        Func<Task>? importSshConfig = null, BiometricUnlock? biometric = null, UpdateController? updates = null)
    {
        _updates = updates;
        _location = location;
        _settingsStore = settingsStore;
        _credentials = credentials;
        _biometric = biometric;
        _saveEverything = saveEverything;
        _import = import;
        _export = export;
        _importSshConfig = importSshConfig;
        _settings = settingsStore.Load();

        var close = new Button { Content = StrokeIcon.Create(Icons.Close, 14), Width = 28, Height = 28 };
        close.Classes.Add("icon");
        ToolTip.SetTip(close, "Close settings (Esc)");
        Avalonia.Automation.AutomationProperties.SetName(close, "Close settings");
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        Background = PageBrush;
        Content = new DockPanel
        {
            Children =
            {
                Pane.Docked(new Border
                {
                    Background = Dialogs.PanelBrush,
                    BorderBrush = Pane.LineBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Height = 42,
                    Padding = new Thickness(32, 0, 12, 0),
                    Child = new DockPanel
                    {
                        Children =
                        {
                            Pane.Docked(close, Dock.Right),
                            new TextBlock { Text = "Settings", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                }, Dock.Top),
                Pane.Docked(new Border
                {
                    Background = Dialogs.PanelBrush,
                    BorderBrush = Pane.LineBrush,
                    BorderThickness = new Thickness(0, 0, 1, 0),
                    Width = 200,
                    Padding = new Thickness(0, 16, 0, 16),
                    Child = _pageList,
                }, Dock.Left),
                _pageScroll,
            },
        };

        var pages = new (string Name, Control[] Sections)[]
        {
            ("General", [StartupSection(), OpeningSection(), LayoutSection(), KeyboardSection(), HostStatusSection()]),
            ("Appearance", [.. ColorSections(), AccessibilitySection()]),
            ("Sessions", [RemoteDesktopSection(), WebPagesSection(), LocalTerminalSection(), RecordingSection()]),
            ("Data and security", [StorageSection(), CredentialsSection(), ImportSection()]),
            ("About", [UpdatesSection(), LogsSection(), AboutSection()]),
        };
        var host = new Panel();
        foreach (var (name, sections) in pages)
        {
            var page = new StackPanel
            {
                Margin = new Thickness(32, 24, 32, 32),
                MaxWidth = 600,
                HorizontalAlignment = HorizontalAlignment.Left,
                Spacing = 16,
                IsVisible = false,
            };
            page.Children.AddRange(sections.Select(Card));
            host.Children.Add(page);
            _pages[name] = page;
            // Text lines up with the title above: the list's 12 and the row's 20 make the header's 32.
            _pageList.Items.Add(new ListBoxItem
            {
                Content = name,
                Tag = name,
                Padding = new Thickness(20, 0, 8, 0),
                Margin = new Thickness(0, 0, 0, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
            });
        }

        _pageScroll.Content = host;
        Avalonia.Automation.AutomationProperties.SetName(_pageList, "Settings pages");
        _pageList.SelectionChanged += (_, _) =>
        {
            if (_pageList.SelectedItem is ListBoxItem { Tag: string name })
            {
                ShowPage(name);
            }
        };
        _pageList.SelectedItem = _pageList.Items.OfType<ListBoxItem>().FirstOrDefault(i => Equals(i.Tag, _settings.SettingsPage))
            ?? _pageList.Items.OfType<ListBoxItem>().First();

        _loading = false;
    }

    /// <summary>The page showing, by its name in the list.</summary>
    public string CurrentPage => _pages.First(p => p.Value.IsVisible).Key;

    /// <summary>Shows the page called <paramref name="name"/> and remembers it for next time.</summary>
    public void ShowPage(string name)
    {
        if (!_pages.ContainsKey(name))
        {
            return;
        }

        foreach (var (key, page) in _pages)
        {
            page.IsVisible = key == name;
        }

        _pageScroll.Offset = default;
        _refreshScreenReaderNote();
        if (_pageList.SelectedItem is not ListBoxItem { Tag: string selected } || selected != name)
        {
            _pageList.SelectedItem = _pageList.Items.OfType<ListBoxItem>().First(i => Equals(i.Tag, name));
        }

        if (!_loading && _settings.SettingsPage != name)
        {
            _settings = _settings with { SettingsPage = name };
            TrySaveSettings();
        }
    }

    /// <summary>The close button or Esc.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The user asked to see the recordings.</summary>
    public event EventHandler? ShowRecordingsRequested;

    /// <summary>Raised when Record every session is turned on or off; it's kept in the defaults for all connections.</summary>
    public event EventHandler<bool>? RecordAllSessionsChanged;

    readonly CheckBox _recordAll = new() { Content = "Record every session" };

    /// <summary>Shows whether the defaults for all connections record sessions, without raising <see cref="RecordAllSessionsChanged"/>.</summary>
    public void ShowRecordAllSessions(bool on)
    {
        bool loading = _loading;
        _loading = true;
        _recordAll.IsChecked = on;
        _loading = loading;
    }

    /// <summary>A new full-screen shortcut was saved.</summary>
    public event EventHandler<KeyGesture>? FullScreenShortcutChanged;

    /// <summary>Where Alt+Tab and Windows' other shortcuts go from a remote desktop changed, and was saved.</summary>
    public event EventHandler<SystemShortcuts>? SystemShortcutsChanged;

    /// <summary>Terminal announcements were turned on (true) or off.</summary>
    public event EventHandler<bool>? TerminalAnnouncementsChanged;

    /// <summary>A new terminal color scheme was chosen, for open terminals to follow.</summary>
    public event EventHandler<TerminalColorScheme>? TerminalColorsChanged;

    /// <summary>The color vision or the correction strength changed, for open terminals and remote desktops to follow.</summary>
    public event EventHandler<(ColorVision Vision, int Strength)>? ColorVisionChanged;

    /// <summary>Checking whether hosts are up was turned on (true) or off.</summary>
    public event EventHandler<bool>? HostStatusChecksChanged;

    /// <summary>A different way of checking hosts was chosen.</summary>
    public event EventHandler<HostCheckMethod>? HostStatusMethodChanged;

    /// <summary>A start-up choice was made; Never means the saved open tabs are to be deleted now.</summary>
    public event EventHandler<RestoreSessionsMode>? RestoreSessionsChanged;

    /// <summary>Remembering recent connections was turned on or off; off means clearing what's kept.</summary>
    public event EventHandler<bool>? RememberRecentConnectionsChanged;

    /// <summary>What opening a connection that's already open does was changed: true opens another session.</summary>
    public event EventHandler<bool>? NewSessionOnOpenChanged;

    /// <summary>A pane mode was chosen, and saved: the side panel's, then the right-hand pane's.</summary>
    public event EventHandler<(PaneMode Side, PaneMode Inspector)>? PaneModesChanged;

    /// <summary>Orrery must restart with these extra arguments: the data or credential store changed underneath.</summary>
    public event EventHandler<string[]>? RestartRequested;

    Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Avalonia.Input.Key.Escape && !e.Handled)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- Storage ----

    Control StorageSection()
    {
        var path = new SelectableTextBlock { Text = _location.DataDir, FontFamily = Dialogs.MonoFont, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var open = new Button { Content = "Open folder" };
        open.Click += (_, _) => OpenFolder(_location.DataDir);
        var move = new Button { Content = "Move data…" };
        move.Click += async (_, _) => await MoveDataAsync();

        var section = Section("Storage",
            Row("Mode", new TextBlock { Text = _location.Mode == StorageMode.Portable ? "Portable" : "Installed" }),
            Row("Data folder", path),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, move } });

        section.Children.Add(Note(_location.Mode == StorageMode.Portable
            ? "Connections, the vault, backups, cache and logs all live in this folder."
            : $"Connections, settings and backups live in this folder. The cache stays in {_location.CacheDir}."));
        return section;
    }

    async Task MoveDataAsync()
    {
        var folders = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an empty folder for Orrery's data, or one that has it already",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } target)
        {
            return;
        }

        if (DataMover.CheckTarget(_location.DataDir, target) is { } problem)
        {
            if (problem == DataMover.NotEmptyMessage && FirstLaunch.HasData(target))
            {
                await UseExistingDataAsync(target);
                return;
            }

            await Dialogs.ShowMessageAsync(Owner, "Can't move the data there", problem);
            return;
        }

        if (DataMover.WarnAboutTarget(target) is { } warning
            && !await Dialogs.ConfirmAsync(Owner, "Other people can change this folder", $"{warning}\n\n{target}", "Use anyway"))
        {
            return;
        }

        // Biometric unlock is tied to the vault's key, not its folder, so it keeps working after the move.
        var biometricNote = _biometric is not null && _credentials is VaultFileStore vault && _biometric.IsOn(vault)
            ? $"\n\n{_biometric.Name} unlock keeps working with the moved vault. Until you delete the old copy, it opens that one too."
            : string.Empty;
        if (!await Dialogs.ConfirmAsync(Owner, "Move Orrery's data?",
                $"Orrery will copy its own files (connections, settings, the vault, recordings, trusted host keys, backups and import reports) to\n{target}\ncheck every file, and restart using the new folder. Open sessions will close.\n\nAnything else in the current folder stays where it is, and so does the old copy: after the restart, Orrery asks once whether to delete it. Web pages' sign-ins and cookies don't move: you'll sign in to them again.{biometricNote}",
                "Move and restart"))
        {
            return;
        }

        await _saveEverything();
        try
        {
            await Task.Run(() => DataMover.Copy(_location.DataDir, target));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(Owner, "Couldn't move the data", $"{e.Message}\n\nNothing changed; Orrery still uses\n{_location.DataDir}");
            return;
        }

        if (!PointAt(target, out var error))
        {
            await Dialogs.ShowMessageAsync(Owner, "Couldn't switch to the new folder",
                $"The data was copied to {target}, but Orrery couldn't record the new location:\n{error}\n\nOrrery still uses\n{_location.DataDir}");
            return;
        }

        RestartRequested?.Invoke(this, ["--moved-from", _location.DataDir]);
    }

    /// <summary>
    /// A folder that already has Orrery's data, as on a computer that joins one a sync app shares: Orrery restarts using it
    /// as it is, as setup does for a new copy, and nothing is copied. The data in use now stays where it is.
    /// </summary>
    async Task UseExistingDataAsync(string target)
    {
        if (DataMover.CheckExisting(_location.DataDir, target) is { } problem)
        {
            await Dialogs.ShowMessageAsync(Owner, "Can't use that folder", problem);
            return;
        }

        if (DataMover.WarnAboutTarget(target) is { } warning
            && !await Dialogs.ConfirmAsync(Owner, "Other people can change this folder", $"{warning}\n\n{target}", "Use anyway"))
        {
            return;
        }

        var vaultNote = File.Exists(Path.Combine(target, "vault.frv")) ? " Its vault opens with its own master password." : "";
        if (!await Dialogs.AskAsync(Owner, "This folder already has Orrery's data",
                $"{target}\n\nIt has connections or settings from another computer that shares this folder, or from an earlier copy of "
                + $"Orrery. Orrery can restart using them as they are: nothing is copied, and open sessions will close.{vaultNote}\n\n"
                + $"The data Orrery uses now stays in\n{_location.DataDir}",
                "Use this data", "Cancel"))
        {
            return;
        }

        await _saveEverything();
        if (!PointAt(target, out var error))
        {
            await Dialogs.ShowMessageAsync(Owner, "Couldn't switch to that folder",
                $"Orrery couldn't record the new location:\n{error}\n\nOrrery still uses\n{_location.DataDir}");
            return;
        }

        Log.For("Storage").LogInformation("Switching to a data folder that already has Orrery's data");
        RestartRequested?.Invoke(this, []);
    }

    /// <summary>Portable mode records the folder in the marker beside the app; installed mode in a pointer file.</summary>
    bool PointAt(string target, out string? error)
    {
        error = null;
        try
        {
            if (_location.Mode == StorageMode.Portable)
            {
                var appDir = DataLocationResolver.AppDirectory(AppContext.BaseDirectory);
                var marker = PortableMarker.For(PortableDataChoice.Custom, DataLocationResolver.ToStoredCustomPath(appDir, target));
                if (!marker.TrySave(appDir))
                {
                    error = $"The app folder {appDir} is read-only.";
                    return false;
                }
            }
            else
            {
                InstalledLocationPointer.Set(PlatformFolders.ForCurrentOs().Config, target);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }

    // ---- Credentials ----

    Control CredentialsSection()
    {
        var current = CredentialStores.KindFor(_location, _settings);
        var section = Section("Credentials");

        if (CredentialStores.CanUseOsKeychain(_location))
        {
            _storeChoice.Items.Add(new ComboBoxItem { Content = Capitalized(KeychainName), Tag = CredentialStoreKind.OsKeychain });
            _storeChoice.Items.Add(new ComboBoxItem { Content = "Encrypted vault file (master password)", Tag = CredentialStoreKind.VaultFile });
            _storeChoice.SelectedIndex = current == CredentialStoreKind.OsKeychain ? 0 : 1;
            _storeChoice.SelectionChanged += async (_, _) => await SwitchStoreAsync();
            section.Children.Add(Row("Saved credentials are kept in", _storeChoice));
            section.Children.Add(Note($"{Capitalized(KeychainName)} is protected by your login on this computer. The vault file is one encrypted file you can back up or move, opened with a master password."));
        }
        else
        {
            section.Children.Add(Row("Saved credentials are kept in", new TextBlock { Text = "Encrypted vault file (master password)" }));
            section.Children.Add(Note(_location.Mode == StorageMode.Portable
                ? "Portable copies always use the vault file, because the system keychain stays on this computer."
                : "No system keyring is available here (on Linux that's the Secret Service, from GNOME Keyring or KWallet), so saved credentials use the vault file."));
        }

        int minutes = _settings.AutoLockMinutes ?? AppSettings.DefaultAutoLockMinutes;
        foreach (var (label, value) in AutoLockChoices)
        {
            _autoLock.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        if (!AutoLockChoices.Any(c => c.Minutes == minutes))
        {
            _autoLock.Items.Add(new ComboBoxItem { Content = $"{minutes} minutes", Tag = minutes });
        }

        _autoLock.SelectedItem = _autoLock.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag! == minutes);
        _autoLock.SelectionChanged += (_, _) => SaveAutoLock();
        if (_credentials is VaultFileStore { Exists: true } vault)
        {
            var change = new Button { Content = "Change master password…" };
            change.Click += async (_, _) =>
            {
                if (await new ChangeMasterPasswordWindow(vault).ShowDialog<bool>(Owner))
                {
                    var turnedOff = await TurnOffBiometricAfterPasswordChangeAsync(vault);
                    await Dialogs.ShowMessageAsync(Owner, "Master password changed", "Use the new password the next time you unlock the vault."
                        + (turnedOff ? $" {_biometric!.Name} unlock was turned off by the change. You can turn it on again here." : string.Empty));
                }
            };
            section.Children.Add(change);

            if (_biometric is not null)
            {
                section.Children.Add(BiometricSwitch(vault, _biometric));
            }
        }

        section.Children.Add(Row("Lock the vault when unused for", _autoLock));
        section.Children.Add(_autoLockNote);
        UpdateAutoLockNote(current);
        return section;
    }

    /// <summary>
    /// Unlock with Windows Hello or Touch ID. Shown for an existing vault file; disabled, with the reason, where the
    /// system can't do it. Turning it on asks for the master password; turning it off deletes the saved key.
    /// </summary>
    Control BiometricSwitch(VaultFileStore vault, BiometricUnlock biometric)
    {
        _biometricSwitch.Content = $"Unlock with {biometric.Name}";
        _biometricSwitch.IsChecked = biometric.IsOn(vault);
        _biometricSwitch.IsEnabled = false;
        _biometricNote.Text = $"Checking for {biometric.Name}…";
        _biometricSwitch.IsCheckedChanged += async (_, _) => await ToggleBiometricAsync(vault, biometric);

        var remove = new Button { Content = $"Remove old {biometric.Name} setup", Margin = new Thickness(0, 2, 0, 0) };
        AutomationProperties.SetName(remove, $"Remove old {biometric.Name} setup");
        AutomationProperties.SetHelpText(remove, LeftoversNote(biometric));
        remove.Click += async (_, _) => await RemoveBiometricLeftoversAsync(vault, biometric);
        _biometricLeftovers.Children.Add(Note(LeftoversNote(biometric)));
        _biometricLeftovers.Children.Add(remove);

        BiometricChecked = ShowBiometricStateAsync(vault, biometric);
        return new StackPanel { Spacing = 4, Children = { _biometricSwitch, _biometricNote, _biometricLeftovers } };
    }

    static string LeftoversNote(BiometricUnlock biometric) =>
        $"A {biometric.Name} setup is still saved for a vault that used to be at this location, or for this vault before another copy of Orrery changed its master password.";

    /// <summary>
    /// Removes Windows Hello or Touch ID setups saved only for the vault's location (<see cref="BiometricUnlock.HasLeftovers"/>),
    /// once the user confirms that whatever they opened won't open with it any more.
    /// </summary>
    async Task RemoveBiometricLeftoversAsync(VaultFileStore vault, BiometricUnlock biometric)
    {
        if (!await Dialogs.ConfirmAsync(Owner, $"Remove the old {biometric.Name} setup?",
                $"The old vault that used to be here, and any backup of this vault from before its master password was changed, "
                + $"won't open with {biometric.Name} any more. They still open with their master password. This vault isn't affected.",
                "Remove"))
        {
            BiometricLeftoversRemoved?.Invoke(this, EventArgs.Empty);
            return;
        }

        int removed = await biometric.RemoveLeftoversAsync(vault);
        ShowBiometricLeftovers(vault, biometric);
        if (removed > 0)
        {
            _biometricNote.Foreground = Dialogs.MutedBrush;
            _biometricNote.Text = $"The old {biometric.Name} setup was removed. {BiometricNote(biometric)}";
        }

        BiometricLeftoversRemoved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when removing old Windows Hello or Touch ID setups has finished, or was canceled. For tests.</summary>
    internal event EventHandler? BiometricLeftoversRemoved;

    void ShowBiometricLeftovers(VaultFileStore vault, BiometricUnlock biometric) => _biometricLeftovers.IsVisible = biometric.HasLeftovers(vault);

    /// <summary>Finishes when the Windows Hello or Touch ID switch shows whether it can be used. For tests.</summary>
    internal Task BiometricChecked { get; private set; } = Task.CompletedTask;

    async Task ShowBiometricStateAsync(VaultFileStore vault, BiometricUnlock biometric)
    {
        var stale = await biometric.TurnOffIfStaleAsync(vault);
        var unavailable = await RefreshBiometricSwitchAsync(vault, biometric);
        ShowBiometricLeftovers(vault, biometric);
        _biometricNote.Text = stale ?? unavailable ?? BiometricNote(biometric);
        _biometricNote.Foreground = stale is not null ? Dialogs.WarningBrush : Dialogs.MutedBrush;
    }

    /// <summary>
    /// Sets the switch to whether it's on, and whether it can be changed: something that's on can always be turned
    /// off, but turning it on needs the system to allow it now. Returns why it can't be used, or null.
    /// </summary>
    async Task<string?> RefreshBiometricSwitchAsync(VaultFileStore vault, BiometricUnlock biometric)
    {
        var unavailable = await biometric.CheckAvailableAsync();
        bool on = biometric.IsOn(vault);
        SetBiometricSwitch(on);
        _biometricSwitch.IsEnabled = on || unavailable is null;
        return unavailable;
    }

    async Task ToggleBiometricAsync(VaultFileStore vault, BiometricUnlock biometric)
    {
        bool wanted = _biometricSwitch.IsChecked == true;
        if (_settingBiometricSwitch || wanted == biometric.IsOn(vault))
        {
            return;
        }

        _biometricSwitch.IsEnabled = false;
        _biometricNote.Foreground = Dialogs.MutedBrush;
        string note;
        if (wanted)
        {
            bool on = await new BiometricSetupWindow(vault, biometric).ShowDialog<bool>(Owner);
            note = on ? $"{biometric.Name} unlock is on. {BiometricNote(biometric)}" : BiometricNote(biometric);
        }
        else
        {
            // Deleting the key can't be undone, and someone who's forgotten the master password would lose the vault.
            if (!await Dialogs.ConfirmAsync(Owner, $"Turn off {biometric.Name} unlock?",
                    $"Orrery deletes its {biometric.Name} key on this computer, and from then on the vault opens only with its master "
                    + "password. Make sure you know it first.", "Turn off"))
            {
                SetBiometricSwitch(true);
                _biometricSwitch.IsEnabled = true;
                BiometricToggled?.Invoke(this, EventArgs.Empty);
                return;
            }

            await biometric.TurnOffAsync(vault);

            // Not followed by what it does when it's on, which read as if it still did.
            note = $"{biometric.Name} unlock is off, and its key was deleted.";
        }

        var unavailable = await RefreshBiometricSwitchAsync(vault, biometric);
        ShowBiometricLeftovers(vault, biometric); // Turning it off removes them too.
        _biometricNote.Text = biometric.IsOn(vault) || unavailable is null ? note : $"{note} {unavailable}";
        BiometricToggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when a change to the Windows Hello or Touch ID switch has finished. For tests.</summary>
    internal event EventHandler? BiometricToggled;

    /// <summary>
    /// After a master password change: a new data key means the saved one can't open the vault any more, so turn it
    /// off and delete the key, including the record kept for the old password.
    /// </summary>
    internal async Task<bool> TurnOffBiometricAfterPasswordChangeAsync(VaultFileStore vault)
    {
        if (_biometric is null || !await _biometric.TurnOffAsync(vault))
        {
            return false;
        }

        await RefreshBiometricSwitchAsync(vault, _biometric);
        ShowBiometricLeftovers(vault, _biometric);
        _biometricNote.Foreground = Dialogs.WarningBrush;
        _biometricNote.Text = $"{_biometric.Name} unlock was turned off because the master password changed. {BiometricNote(_biometric)}";
        return true;
    }

    /// <summary>
    /// After switching to the system keychain: the vault file stays behind, and a saved Windows Hello or Touch ID key
    /// would still open it, so turn it off and delete the key.
    /// </summary>
    internal async Task TurnOffBiometricForOldVaultAsync()
    {
        if (_credentials is VaultFileStore oldVault && _biometric is not null)
        {
            await _biometric.TurnOffAsync(oldVault);
        }
    }

    void SetBiometricSwitch(bool on)
    {
        _settingBiometricSwitch = true;
        _biometricSwitch.IsChecked = on;
        _settingBiometricSwitch = false;
    }

    static string BiometricNote(BiometricUnlock biometric) =>
        $"{biometric.Name} opens the vault on this computer only. The master password still works, and changing it turns this off."
        + (OperatingSystem.IsMacOS() ? string.Empty
            : $" Your {biometric.Name} PIN, and any face or finger on this Windows account, open it too, so anyone who knows the PIN and can use your account here could open the vault. Changing the PIN doesn't turn this off.");

    void UpdateAutoLockNote(CredentialStoreKind kind)
    {
        _autoLock.IsEnabled = kind == CredentialStoreKind.VaultFile;
        _autoLockNote.Text = kind == CredentialStoreKind.VaultFile
            ? "Open sessions keep running when the vault locks."
            : $"Applies only to the vault file. {Capitalized(KeychainName)} unlocks with your login.";
    }

    void SaveAutoLock()
    {
        if (_loading || _autoLock.SelectedItem is not ComboBoxItem { Tag: int minutes })
        {
            return;
        }

        _settings = _settings with { AutoLockMinutes = minutes };
        TrySaveSettings();
        if (_credentials is VaultFileStore vault)
        {
            vault.AutoLockAfter = _settings.AutoLockAfter;
        }
    }

    async Task SwitchStoreAsync()
    {
        var current = CredentialStores.KindFor(_location, _settings);
        if (_loading || _storeChoice.SelectedItem is not ComboBoxItem { Tag: CredentialStoreKind wanted } || wanted == current)
        {
            return;
        }

        if (!await TrySwitchStoreAsync(current, wanted))
        {
            _loading = true;
            _storeChoice.SelectedIndex = current == CredentialStoreKind.OsKeychain ? 0 : 1;
            _loading = false;
        }
    }

    /// <summary>
    /// Copies every credential into the other store (same ids, so connections keep working), checks the
    /// copies, records the choice, then offers to remove the originals. False if nothing changed.
    /// </summary>
    async Task<bool> TrySwitchStoreAsync(CredentialStoreKind from, CredentialStoreKind to)
    {
        string fromName = StoreName(from), toName = StoreName(to);
        if (!await Dialogs.ConfirmAsync(Owner, $"Switch to {toName}?",
                $"Orrery will copy your saved credentials from {fromName} to {toName}, then restart to use it. Open sessions will close.",
                "Copy and restart"))
        {
            return false;
        }

        if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, Owner))
        {
            return false;
        }

        var target = CredentialStores.Create(_location, to, _settings.AutoLockAfter);
        try
        {
            if (!await VaultUnlockWindow.EnsureUnlockedAsync(target, Owner))
            {
                return false;
            }

            IReadOnlyList<Guid> copied;
            try
            {
                copied = await CredentialCopier.CopyAllAsync(_credentials, target);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                await Dialogs.ShowMessageAsync(Owner, "Couldn't copy the credentials", $"{e.Message}\n\nOrrery keeps using {fromName}.");
                return false;
            }

            _settings = _settings with { CredentialStore = to };
            if (!TrySaveSettings())
            {
                return false;
            }

            await TurnOffBiometricForOldVaultAsync();

            if (copied.Count > 0 && await Dialogs.ConfirmAsync(Owner, $"Remove the originals from {fromName}?",
                    $"{copied.Count} credential{(copied.Count == 1 ? " was" : "s were")} copied to {toName} and checked. Keeping the originals leaves a second copy of each password in {fromName}.",
                    "Remove originals"))
            {
                await RemoveOriginalsAsync(copied, fromName);
            }
        }
        finally
        {
            (target as IDisposable)?.Dispose();
        }

        RestartRequested?.Invoke(this, []);
        return true;
    }

    async Task RemoveOriginalsAsync(IReadOnlyList<Guid> ids, string storeName)
    {
        try
        {
            foreach (var id in ids)
            {
                await _credentials.DeleteAsync(id);
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await Dialogs.ShowMessageAsync(Owner, "Some originals weren't removed", $"{e.Message}\n\nThe copies are safe. You can remove what's left from {storeName} by hand.");
        }
    }

    static string StoreName(CredentialStoreKind kind) =>
        kind == CredentialStoreKind.OsKeychain ? KeychainName : "the vault file";

    static string KeychainName => CredentialStores.OsKeychain?.Name ?? "the system keychain";

    static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    bool TrySaveSettings()
    {
        try
        {
            // The recordings key and note are saved by recording itself, and the last update check and skipped version by
            // the update checks, perhaps while this page is open: keep what's on disk.
            var saved = _settingsStore.Load();
            _settings = _settings with
            {
                RecordingPublicKey = saved.RecordingPublicKey ?? _settings.RecordingPublicKey,
                RecordingNoteShown = saved.RecordingNoteShown ?? _settings.RecordingNoteShown,
                LastUpdateCheck = saved.LastUpdateCheck,
                SkippedUpdateVersion = saved.SkippedUpdateVersion,
            };
            _settingsStore.Save(_settings);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = Dialogs.ShowMessageAsync(Owner, "Couldn't save settings", e.Message);
            return false;
        }
    }

    // ---- Recordings (docs/design-session-recording.md §4.2) ----

    Control RecordingSection()
    {
        ComboBox Choice<T>(IEnumerable<(T Value, string Label)> choices, T current, Func<AppSettings, T, AppSettings> apply, string name)
        {
            var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(box, name);
            foreach (var (value, label) in choices)
            {
                box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            }

            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, current)) ?? box.Items[0];
            box.SelectionChanged += (_, _) =>
            {
                if (!_loading && box.SelectedItem is ComboBoxItem { Tag: T chosen })
                {
                    _settings = apply(_settings, chosen);
                    TrySaveSettings();
                }
            };
            return box;
        }

        var keep = Choice([(7, "7 days"), (30, "30 days"), (90, "90 days"), (365, "A year"), (0, "Until I delete them")],
            _settings.RecordingKeepDays ?? AppSettings.DefaultRecordingKeepDays, (s, v) => s with { RecordingKeepDays = v }, "Keep recordings for");
        var total = Choice([(1, "1 GB"), (5, "5 GB"), (20, "20 GB"), (50, "50 GB")],
            _settings.RecordingTotalGB ?? AppSettings.DefaultRecordingTotalGB, (s, v) => s with { RecordingTotalGB = v }, "Use at most");
        var file = Choice([(1, "1 GB"), (4, "4 GB"), (16, "16 GB")],
            _settings.RecordingFileGB ?? AppSettings.DefaultRecordingFileGB, (s, v) => s with { RecordingFileGB = v }, "Stop a recording at");
        var rate = Choice(AppSettings.RecordingFrameRates.Select(r => (r, $"{r}")).ToArray(),
            _settings.RecordingFramesPerSecond, (s, v) => s with { RecordingFrameRate = v }, "Frames a second for remote desktops, at most");

        AutomationProperties.SetName(_recordAll, "Record every session");
        _recordAll.IsCheckedChanged += (_, _) =>
        {
            if (!_loading)
            {
                RecordAllSessionsChanged?.Invoke(this, _recordAll.IsChecked == true);
            }
        };

        // Sound is stored as it's played, about 11 MB a minute for CD-quality stereo; silence takes nothing.
        var sound = new CheckBox { Content = "Record remote desktop sound (about 11 MB a minute while sound plays)", IsChecked = _settings.RecordsSound };
        sound.IsCheckedChanged += (_, _) =>
        {
            if (!_loading)
            {
                _settings = _settings with { RecordingSound = sound.IsChecked == true };
                TrySaveSettings();
            }
        };

        var names = new CheckBox { Content = "Put connection names in file names", IsChecked = _settings.PutsNamesInRecordingFiles };
        names.IsCheckedChanged += (_, _) =>
        {
            if (!_loading)
            {
                _settings = _settings with { RecordingNamesInFiles = names.IsChecked == true };
                TrySaveSettings();
            }
        };

        var folder = _location.FileIn(Recording.RecordingService.FolderName);
        var used = Note(UsedText(folder));
        var show = new Button { Content = "Show recordings" };
        show.Click += (_, _) => ShowRecordingsRequested?.Invoke(this, EventArgs.Empty);
        var open = new Button { Content = "Open the recordings folder" };
        open.Click += (_, _) =>
        {
            PrivateStorage.CreateDirectory(folder);
            OpenFolder(folder);
        };

        // MP4 export (docs/design-session-recording.md §4.2): each codec and what makes it, from Windows' codecs (read
        // once) and the chosen ffmpeg, run to read its version and encoders. With none that runs, the places installers
        // use are looked in, and one found is offered, not taken: its line comes from its path, so nothing is run.
        IReadOnlyList<Core.Recording.SystemCodec>? systemCodecs = null;
        string? foundFfmpeg = null;
        int refreshes = 0;
        var codecList = new Border { Background = Pane.CardBrush, BorderBrush = Pane.LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        var chooseFfmpeg = new Button { Content = "Choose ffmpeg…" };
        var getFfmpeg = Recording.FfmpegHelp.DownloadButton();
        var clearFfmpeg = new Button { Content = "Don't use ffmpeg", IsVisible = false };
        var notChosen = new TextBlock { Text = "Not chosen", FontSize = 12, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), IsVisible = false };
        var current = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
        var ffmpegText = Note("Checking…");
        var installFfmpeg = Note(Recording.FfmpegHelp.InstallHint);
        installFfmpeg.IsVisible = false;
        var foundLine = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        var foundPath = new TextBlock { FontFamily = Dialogs.MonoFont, FontSize = 12, Foreground = Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        var useFound = new Button { Content = "Use it", Classes = { "accent" }, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(useFound, Dock.Right);
        var foundDot = Dot(Palette.Accent);
        DockPanel.SetDock(foundDot, Dock.Left);
        var found = new Border
        {
            Background = Pane.CardBrush,
            BorderBrush = new SolidColorBrush(Palette.Accent.Color, 0.4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            IsVisible = false,
            Child = new DockPanel
            {
                Children = { foundDot, useFound, new StackPanel { Margin = new Thickness(12, 0), Children = { foundLine, foundPath } } },
            },
        };

        async Task RefreshFfmpegAsync()
        {
            var mine = ++refreshes;
            ffmpegText.Text = "Checking…";
            ffmpegText.IsVisible = true;
            systemCodecs ??= OperatingSystem.IsWindows() ? await Task.Run(Core.Recording.MediaFoundation.Codecs) : [];
            var chosen = _settings.FfmpegPath;
            var info = chosen is null ? null : await Core.Recording.Ffmpeg.ProbeAsync(chosen);
            var candidate = info is null ? await Task.Run(Core.Recording.Ffmpeg.Find) : null;
            if (mine != refreshes)
            {
                return;
            }

            codecList.Child = Pane.Rows([.. Recording.FfmpegHelp.Codecs(systemCodecs, info).Select(CodecLine)]);
            chooseFfmpeg.Content = chosen is null ? "Choose ffmpeg…" : "Choose another…";
            getFfmpeg.IsVisible = info is null;
            clearFfmpeg.IsVisible = chosen is not null;
            notChosen.IsVisible = chosen is null;
            current.Children.Clear();
            if (info is not null)
            {
                var path = new TextBlock { Text = Recording.FfmpegHelp.ShortPath(info.Path), FontFamily = Dialogs.MonoFont, FontSize = 12, Foreground = Dialogs.MutedBrush, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(path, info.Path);
                current.Children.AddRange([Dot(Palette.Success), new TextBlock { Text = $"ffmpeg {Core.Recording.Ffmpeg.ShortVersion(info.Version)}", FontSize = 13, VerticalAlignment = VerticalAlignment.Center }, path]);
            }

            current.IsVisible = info is not null;
            ffmpegText.Text = chosen is not null && info is null ? $"{chosen} didn't run as ffmpeg (moved or uninstalled?), so export doesn't use it." : string.Empty;
            ffmpegText.IsVisible = ffmpegText.Text.Length > 0;
            foundFfmpeg = candidate == chosen ? null : candidate;
            if (foundFfmpeg is not null)
            {
                foundLine.Text = Recording.FfmpegHelp.FoundText(foundFfmpeg);
                foundPath.Text = Recording.FfmpegHelp.ShortPath(foundFfmpeg);
                ToolTip.SetTip(foundPath, foundFfmpeg);
            }

            found.IsVisible = foundFfmpeg is not null;
            installFfmpeg.IsVisible = info is null && foundFfmpeg is null;
        }

        async Task UseFfmpegAsync(string path)
        {
            ffmpegText.Text = "Checking…";
            ffmpegText.IsVisible = true;
            if (await Core.Recording.Ffmpeg.ProbeAsync(path) is null)
            {
                ffmpegText.Text = $"{path} isn't an ffmpeg Orrery could run. Nothing changed.";
                return;
            }

            _settings = _settings with { FfmpegPath = path };
            TrySaveSettings();
            await RefreshFfmpegAsync();
        }

        _ = RefreshFfmpegAsync();
        useFound.Click += async (_, _) =>
        {
            if (foundFfmpeg is { } path)
            {
                await UseFfmpegAsync(path);
            }
        };
        chooseFfmpeg.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not { } top)
            {
                return;
            }

            var picked = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose ffmpeg",
                AllowMultiple = false,
            });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path)
            {
                return;
            }

            await UseFfmpegAsync(path);
        };
        clearFfmpeg.Click += async (_, _) =>
        {
            _settings = _settings with { FfmpegPath = null };
            TrySaveSettings();
            await RefreshFfmpegAsync();
        };

        return Section("Recordings",
            Note("A connection's Record sessions setting, or a tab's menu, records a session: what a terminal printed (never what's typed), "
                + "or what a remote desktop showed. Recordings are encrypted with a key kept with your saved credentials, and play in Orrery."),
            _recordAll,
            Note("Records every connection as it opens, quick connections too, unless a connection or its folder turns Record sessions off. "
                + "It's the Record sessions setting in Defaults for all connections."),
            Row("Keep recordings for", keep),
            Row("Use at most", total),
            Row("Stop a recording at", file),
            Row("Frames a second for remote desktops, at most", rate),
            sound,
            names,
            Note("Old recordings are deleted for good, the oldest first, when they pass these limits."),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { show, open } },
            used,
            new Border { Height = 1, Background = Pane.LineBrush, Margin = new Thickness(0, 8, 0, 4) },
            SubHeading("MP4 EXPORT"),
            codecList,
            Row(OperatingSystem.IsWindows() ? "ffmpeg (optional)" : "ffmpeg", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { chooseFfmpeg, getFfmpeg, clearFfmpeg, notChosen } }),
            current,
            ffmpegText,
            found,
            installFfmpeg,
            Note(OperatingSystem.IsWindows()
                ? "Export… uses Windows' own encoders first. An ffmpeg adds any codec Windows lacks."
                : "Export… makes MP4 through the ffmpeg chosen here. Without one, screen recordings export as AVI."));
    }

    /// <summary>A codec in the MP4 export list: its name, a dot for what makes it (green Windows, blue ffmpeg, gray nothing), and a note.</summary>
    internal static Control CodecLine(Recording.CodecRow row)
    {
        bool none = row.Source == Recording.Mp4Source.None;
        var name = new TextBlock { Text = row.Name, Width = 64, FontSize = 13 };
        var source = new TextBlock
        {
            Text = row.Source switch { Recording.Mp4Source.Windows => "Windows", Recording.Mp4Source.Ffmpeg => "ffmpeg", _ => "Not available" },
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
        };

        // Muted only when nothing makes it: a null Foreground isn't "inherit", it's no brush, and the text vanishes.
        if (none)
        {
            name.Foreground = source.Foreground = Dialogs.MutedBrush;
        }

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 7),
            Children =
            {
                name,
                Dot(row.Source switch { Recording.Mp4Source.Windows => Palette.Success, Recording.Mp4Source.Ffmpeg => Palette.Accent, _ => Palette.StateEnded }),
                source,
            },
        };
        if (row.Detail is { } detail)
        {
            line.Children.Add(new TextBlock { Text = "· " + detail, FontSize = 13, Margin = new Thickness(6, 0, 0, 0), Foreground = none ? Palette.StateEnded : Dialogs.MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        AutomationProperties.SetName(line, $"{row.Name}: {(none ? "not available" : "from " + row.Source)}{(row.Detail is null ? "" : ", " + row.Detail)}");
        return line;
    }

    /// <summary>A heading inside a section, styled like the section's own and a level below it.</summary>
    static TextBlock SubHeading(string text)
    {
        var heading = new TextBlock { Text = text, Classes = { "section" } };
        AutomationProperties.SetHeadingLevel(heading, 3);
        return heading;
    }

    static Avalonia.Controls.Shapes.Ellipse Dot(IBrush brush) => new() { Width = 8, Height = 8, Fill = brush, VerticalAlignment = VerticalAlignment.Center };

    static string UsedText(string folder)
    {
        var files = Core.Recording.RecordingRetention.List(folder);
        long bytes = files.Sum(f => f.Length);
        return files.Count == 0 ? "No recordings yet." : $"{files.Count} recording{(files.Count == 1 ? "" : "s")}, {bytes / (1024.0 * 1024):0.#} MB.";
    }

    // ---- Startup ----

    Control StartupSection()
    {
        var choices = new (string Label, RestoreSessionsMode Mode)[]
        {
            ("Ask me", RestoreSessionsMode.Ask),
            ("Always reopen them", RestoreSessionsMode.Always),
            ("Never", RestoreSessionsMode.Never),
        };

        var buttons = choices.Select(choice => new RadioButton
        {
            Content = choice.Label,
            GroupName = "RestoreSessions",
            Tag = choice.Mode,
            IsChecked = _settings.RestoreMode == choice.Mode,
        }).ToList();
        foreach (var button in buttons)
        {
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true && !_loading)
                {
                    var mode = (RestoreSessionsMode)button.Tag!;
                    _settings = _settings with { RestoreSessions = mode };
                    TrySaveSettings();
                    RestoreSessionsChanged?.Invoke(this, mode);
                }
            };
        }

        var choiceList = new StackPanel { Spacing = 2, Children = { buttons[0], buttons[1], buttons[2] } };
        AutomationProperties.SetName(choiceList, "Reopen the sessions that were open when Orrery last closed");

        var recent = new CheckBox { Content = "Remember recent connections", IsChecked = _settings.RemembersRecentConnections };
        recent.IsCheckedChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            bool on = recent.IsChecked == true;
            _settings = _settings with { RememberRecentConnections = on };
            TrySaveSettings();
            RememberRecentConnectionsChanged?.Invoke(this, on);
        };
        const string recentNote = "Keeps the Recent list, and the last connected and last failed times shown in a connection's properties. "
            + "Turning this off clears the list and those times, and nothing is recorded until you turn it back on, which starts over. "
            + "The log still notes what connected where.";
        AutomationProperties.SetHelpText(recent, recentNote);

        return Section("Startup",
            new TextBlock { Text = "Reopen the sessions that were open when Orrery last closed" },
            choiceList,
            Note("Never also stops Orrery writing down which sessions are open, and deletes the list it kept. The window's size and position, "
                + "and which panes are showing, are still remembered. A host typed into quick connect reopens by its address alone, "
                + "and asks for the login again."),
            recent,
            Note(recentNote));
    }

    // ---- Opening connections ----

    Control OpeningSection()
    {
        var choice = new ComboBox
        {
            MinWidth = 160,
            ItemsSource = new[]
            {
                new ComboBoxItem { Content = "Go to its open session", Tag = false },
                new ComboBoxItem { Content = "Open a new session", Tag = true },
            },
            SelectedIndex = _settings.OpensNewSession ? 1 : 0,
        };
        AutomationProperties.SetName(choice, "Opening a connection that's already open");
        choice.SelectionChanged += (_, _) =>
        {
            if (_loading || choice.SelectedItem is not ComboBoxItem { Tag: bool newSession })
            {
                return;
            }

            _settings = _settings with { NewSessionOnOpen = newSession };
            if (TrySaveSettings())
            {
                NewSessionOnOpenChanged?.Invoke(this, newSession);
            }
        };

        return Section("Opening connections",
            Row("Double-click a connection that's already open", choice),
            Note("This goes for Enter and Connect too. With several sessions open to it, it goes to the one used last; with none, it opens one. "
                + "Ctrl+double-click, a middle-click, or Open new session on its right-click menu always opens another."));
    }

    // ---- Remote desktops ----

    Control RemoteDesktopSection()
    {
        var zoom = new CheckBox { Content = "Enlarge remote desktops to match this display's scaling", IsChecked = _settings.ZoomRemoteDesktops };
        zoom.IsCheckedChanged += (_, _) =>
        {
            _settings = _settings with { RemoteDesktopZoom = zoom.IsChecked == true };
            TrySaveSettings();
        };

        var zoomNote = Note("RDP tells the server this display's scaling, as Windows' own client does, so the desktop stays sharp with text the size of "
            + "your other apps'. VNC can't be told, so its picture is enlarged instead: sharp at 200%, slightly soft at 125% or 150%. "
            + "Off, remote text looks as small as it would at 100%. Applies to new connections. "
            + "A connection or folder can set its own choice in its properties.");

        // Only Windows lets Orrery take Alt+Tab from the system; elsewhere the Send keys menu sends it.
        if (!OperatingSystem.IsWindows())
        {
            return Section("Remote desktops", zoom, zoomNote);
        }

        var shortcuts = new ComboBox
        {
            MinWidth = 160,
            Name = "SystemShortcuts",
            ItemsSource = new[]
            {
                new ComboBoxItem { Content = "In full screen", Tag = SystemShortcuts.FullScreen },
                new ComboBoxItem { Content = "On the remote computer", Tag = SystemShortcuts.Remote },
                new ComboBoxItem { Content = "On this computer", Tag = SystemShortcuts.Local },
            },
            SelectedIndex = _settings.SystemShortcutsGo switch { SystemShortcuts.Remote => 1, SystemShortcuts.Local => 2, _ => 0 },
        };
        AutomationProperties.SetName(shortcuts, "Alt+Tab and other Windows shortcuts go to the remote computer");
        shortcuts.SelectionChanged += (_, _) =>
        {
            if (_loading || shortcuts.SelectedItem is not ComboBoxItem { Tag: SystemShortcuts choice })
            {
                return;
            }

            _settings = _settings with { RemoteSystemShortcuts = choice };
            if (TrySaveSettings())
            {
                SystemShortcutsChanged?.Invoke(this, choice);
            }
        };

        return Section("Remote desktops",
            zoom,
            zoomNote,
            Row("Alt+Tab, Alt+Esc and Ctrl+Esc go to the remote", shortcuts),
            Note("In full screen, as Windows' own client does, they work on the remote desktop; in a window they stay with this "
                + "computer, so Alt+Tab still gets you out. The Windows key always goes to the remote desktop while it has the keyboard, "
                + "and Ctrl+Alt+Del and Win+L always stay here. A connection or folder can set its own choice in its properties. "
                + "Takes effect at once."));
    }

    // ---- Web pages ----

    Control WebPagesSection()
    {
        var buttons = new[] { (Label: "In an Orrery tab", InOrrery: true), (Label: "In the default browser", InOrrery: false) }.Select(choice => new RadioButton
        {
            Content = choice.Label,
            GroupName = "WebPages",
            Tag = choice.InOrrery,
            IsChecked = _settings.OpensWebPagesInOrrery == choice.InOrrery,
        }).ToList();
        foreach (var button in buttons)
        {
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true && !_loading)
                {
                    _settings = _settings with { WebPagesInOrrery = (bool)button.Tag! };
                    TrySaveSettings();
                }
            };
        }

        var choiceList = new StackPanel { Spacing = 2, Children = { buttons[0], buttons[1] } };
        AutomationProperties.SetName(choiceList, "Open web pages");

        var clear = new Button { Content = "Clear web data…", IsEnabled = WebData.Exists(_location.DataDir) };
        var cleared = Note(string.Empty);
        cleared.IsVisible = false;
        clear.Click += async (_, _) =>
        {
            if (!await Dialogs.ConfirmAsync(Owner, "Clear web data?",
                    "This signs you out of every page opened in an Orrery tab, and deletes their cookies, cache and saved site data. "
                    + "Pages opened in your browser aren't affected.", "Clear web data"))
            {
                return;
            }

            try
            {
                // A page engine that's run since Orrery started keeps its files open until Orrery exits.
                if (Sessions.WebPageView.EngineStarted)
                {
                    WebData.ClearAtStart(_location.DataDir);
                    cleared.Text = "Web data will be cleared when Orrery next starts.";
                }
                else
                {
                    WebData.Clear(_location.DataDir);
                    cleared.Text = "Web data cleared.";
                }

                clear.IsEnabled = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                cleared.Text = $"Couldn't clear web data: {e.Message}";
            }

            cleared.IsVisible = true;
        };

        return Section("Web pages",
            Dialogs.Label("Open web connections and management pages"),
            choiceList,
            Note("An Orrery tab keeps them beside your sessions, with their own sign-ins kept in Orrery's data folder. The default "
                + "browser is for pages that need its extensions or saved passwords; a tab can also send its page there. "
                + "A connection or folder can set its own choice in its properties."),
            clear,
            cleared);
    }

    // ---- Local terminal ----

    Control LocalTerminalSection()
    {
        var shells = LocalShells.Available();
        var note = Note($"New local terminals start in your home folder. {(OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl")}+Shift+T opens one, "
            + "as does the terminal button beside the tabs.");
        if (shells.Count == 1)
        {
            // Linux and macOS: the login shell, which is set for the account rather than here.
            return Section("Local terminal", Row("Shell", new TextBlock { Text = shells[0].Program, FontFamily = Dialogs.MonoFont, FontSize = 12 }), note);
        }

        var current = LocalShells.Find(_settings.LocalShell);
        var choice = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var shell in shells)
        {
            var item = new ComboBoxItem { Content = shell.Name, Tag = shell.Id };
            ToolTip.SetTip(item, shell.Program);
            choice.Items.Add(item);
        }

        choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag! == current.Id);
        choice.SelectionChanged += (_, _) =>
        {
            if (!_loading && choice.SelectedItem is ComboBoxItem { Tag: string id })
            {
                _settings = _settings with { LocalShell = id };
                TrySaveSettings();
            }
        };

        return Section("Local terminal", Row("Shell", choice), note);
    }

    // ---- Host status ----

    Control HostStatusSection()
    {
        var check = new CheckBox { Content = "Show whether hosts are up", IsChecked = _settings.ChecksHostStatus };
        check.IsCheckedChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _settings = _settings with { HostStatusChecks = check.IsChecked == true };
            TrySaveSettings();
            HostStatusChecksChanged?.Invoke(this, check.IsChecked == true);
        };

        var methods = new (HostCheckMethod Method, string Label)[]
        {
            (HostCheckMethod.Tcp, "Connection port (TCP)"),
            (HostCheckMethod.Ping, "Ping (ICMP echo)"),
        };
        var method = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = _settings.ChecksHostStatus };
        foreach (var (value, label) in methods)
        {
            method.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        method.SelectedItem = method.Items.OfType<ComboBoxItem>().First(i => (HostCheckMethod)i.Tag! == _settings.HostCheckMethod);
        method.SelectionChanged += (_, _) =>
        {
            if (!_loading && method.SelectedItem is ComboBoxItem { Tag: HostCheckMethod chosen })
            {
                _settings = _settings with { HostStatusMethod = chosen };
                TrySaveSettings();
                HostStatusMethodChanged?.Invoke(this, chosen);
            }
        };
        check.IsCheckedChanged += (_, _) => method.IsEnabled = check.IsChecked == true;
        const string methodNote = "Connection port opens a TCP connection to each connection's host and port and closes it, sending nothing. "
            + "Some networks treat that as a port scan. Ping sends one ICMP echo to each host, once for a host several connections share. "
            + "Firewalls often block ping, so a host you can connect to may show as down, and where this computer doesn't allow ping "
            + "every host shows the dashed ring. A change applies from the next check.";
        AutomationProperties.SetHelpText(method, methodNote);

        return Section("Host status",
            check,
            Note("About once a minute, Orrery checks the host of each connection you can see in the list, and shows a mark beside its protocol: "
                + "a filled dot if it answered, a cross if it didn't, a dashed ring if it can't be checked. "
                + "The mark before the name is the session's state, not the host's. Connections that go through a jump host are skipped. "
                + "Off, nothing is sent and no marks show."),
            Row("Check hosts by", method),
            Note(methodNote));
    }

    // ---- Colors ----

    static readonly (ColorVision Vision, string Label)[] ColorVisionChoices =
    [
        (ColorVision.None, "No color blindness"),
        (ColorVision.Protanopia, "Protanopia: red-weak"),
        (ColorVision.Deuteranopia, "Deuteranopia: green-weak"),
        (ColorVision.Tritanopia, "Tritanopia: blue-yellow"),
    ];

    /// <summary>Color vision, interface colors and terminal colors: three cards, since choosing a color vision sets the other two.</summary>
    Control[] ColorSections()
    {
        var current = _settings.InterfaceColorScheme;
        var radios = new Dictionary<InterfaceColorScheme, RadioButton>();
        var choices = new (InterfaceColorScheme Scheme, string Text)[]
        {
            (InterfaceColorScheme.Standard, "Standard"),
            (InterfaceColorScheme.Clearer, "Clearer"),
            (InterfaceColorScheme.ColorBlind, "Color-blind (Okabe-Ito)"),
        };
        var interfaceColors = new StackPanel { Spacing = 4 };
        Avalonia.Automation.AutomationProperties.SetName(interfaceColors, "Interface colors");
        foreach (var (choice, text) in choices)
        {
            var radio = new RadioButton { Content = text, GroupName = "InterfaceColors", Tag = choice, IsChecked = choice == current };
            radios[choice] = radio;
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked != true)
                {
                    return;
                }

                _settings = _settings with { InterfaceColors = choice == InterfaceColorScheme.Standard ? null : choice, ColorBlindColors = null };
                TrySaveSettings();
                Palette.Use(choice);
                Log.For("Settings").LogInformation("Interface colors set to {Scheme}", choice);
            };
            interfaceColors.Children.Add(radio);
        }

        var terminal = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        terminal.Items.Add(new ComboBoxItem { Content = "Standard", Tag = TerminalColorScheme.Standard });
        terminal.Items.Add(new ComboBoxItem { Content = "Color-blind friendly", Tag = TerminalColorScheme.ColorBlind });
        var scheme = _settings.TerminalColors ?? TerminalColorScheme.Standard;
        terminal.SelectedItem = terminal.Items.OfType<ComboBoxItem>().First(i => (TerminalColorScheme)i.Tag! == scheme);
        terminal.SelectionChanged += (_, _) =>
        {
            if (_loading || terminal.SelectedItem is not ComboBoxItem { Tag: TerminalColorScheme chosen })
            {
                return;
            }

            _settings = _settings with { TerminalColors = chosen == TerminalColorScheme.Standard ? null : chosen };
            TrySaveSettings();
            TerminalColorsChanged?.Invoke(this, chosen);
            Log.For("Settings").LogInformation("Terminal colors set to {Scheme}", chosen);
        };

        var visionNote = "If you're color blind, choose which kind. Orrery then switches to its color-blind interface and terminal colors, "
            + "with terminal colors tuned to that kind, and corrects remote desktops and web pages in tabs so colors you'd confuse look different. "
            + "You can still change the interface and terminal colors below, and turn correction off for a connection in its settings.";
        var vision = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (choice, label) in ColorVisionChoices)
        {
            vision.Items.Add(new ComboBoxItem { Content = label, Tag = choice });
        }

        vision.SelectedItem = vision.Items.OfType<ComboBoxItem>().First(i => (ColorVision)i.Tag! == _settings.Vision);
        Avalonia.Automation.AutomationProperties.SetHelpText(vision, visionNote);

        var strengthNote = "Only for remote desktops and web pages in tabs: how far their colors are shifted. Lower it if pictures look too changed; "
            + "0% leaves them as they are.";
        var strength = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 10,
            IsSnapToTickEnabled = true,
            SmallChange = 10,
            LargeChange = 25,
            Value = _settings.CorrectionStrength,
            IsEnabled = _settings.Vision != ColorVision.None,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Avalonia.Automation.AutomationProperties.SetName(strength, "Correction strength");
        Avalonia.Automation.AutomationProperties.SetHelpText(strength, strengthNote);
        var strengthText = new TextBlock { Text = $"{_settings.CorrectionStrength}%", Width = 44, VerticalAlignment = VerticalAlignment.Center };

        void VisionChanged() => ColorVisionChanged?.Invoke(this, (_settings.Vision, _settings.CorrectionStrength));

        vision.SelectionChanged += (_, _) =>
        {
            if (_loading || vision.SelectedItem is not ComboBoxItem { Tag: ColorVision chosen })
            {
                return;
            }

            _settings = _settings with { ColorVision = chosen == ColorVision.None ? null : chosen };
            TrySaveSettings();
            strength.IsEnabled = chosen != ColorVision.None;
            Log.For("Settings").LogInformation("Color vision set to {Vision}", chosen);

            // The color-blind sets suit every kind; the choices below stay the user's to change afterwards.
            // Going back to none leaves them as they are.
            if (chosen != ColorVision.None)
            {
                radios[InterfaceColorScheme.ColorBlind].IsChecked = true;
                terminal.SelectedItem = terminal.Items.OfType<ComboBoxItem>().First(i => (TerminalColorScheme)i.Tag! == TerminalColorScheme.ColorBlind);
            }

            VisionChanged();
        };
        strength.ValueChanged += (_, _) =>
        {
            int percent = (int)Math.Round(strength.Value);
            strengthText.Text = $"{percent}%";
            if (_loading || percent == _settings.CorrectionStrength)
            {
                return;
            }

            _settings = _settings with { ColorCorrectionStrength = percent == AppSettings.DefaultColorCorrectionStrength ? null : percent };
            TrySaveSettings();
            VisionChanged();
        };

        return
        [
            Section("Color vision",
                Note("If you're color blind, choose which kind. Orrery then switches the interface and terminal colors below to ones "
                    + "made for it, and shifts remote desktop colors so ones you'd confuse look different."),
                Row("Color vision", vision),
                Row("Correction strength", new DockPanel { Children = { Pane.Docked(strengthText, Dock.Right), strength } }),
                Note(strengthNote)),
            Section("Interface colors",
                Note("The colors of session states in tabs and the connection list, and of warnings and errors. "
                    + "Each also has a shape, an icon or a word, so color is never the only sign."),
                interfaceColors,
                Note("Clearer uses teal, yellow and red, brighter and further apart than Standard. Color-blind uses blue, yellow and "
                    + "vermilion, which stay apart with any kind of color blindness.")),
            Section("Terminal colors",
                Note("The colors programs use in SSH, Telnet and local terminals."),
                Row("Terminal colors", terminal),
                Note("Color-blind friendly turns red into vermilion and green into bluish green, so programs that mark things red or green "
                    + "stay readable. With a color vision chosen above, it's tuned to that kind. Open terminals change at once.")),
        ];
    }

    // ---- Layout ----

    Control LayoutSection()
    {
        ComboBox Choice(string name, PaneMode current, Func<AppSettings, PaneMode, AppSettings> with)
        {
            var choice = new ComboBox
            {
                MinWidth = 160,
                ItemsSource = PaneModes.All.Select(mode => new ComboBoxItem { Content = mode.Label, Tag = mode.Mode }).ToList(),
            };
            choice.SelectedIndex = Array.FindIndex(PaneModes.All, mode => mode.Mode == current);
            AutomationProperties.SetName(choice, name);
            choice.SelectionChanged += (_, _) =>
            {
                if (_loading || choice.SelectedItem is not ComboBoxItem { Tag: PaneMode mode })
                {
                    return;
                }

                _settings = with(_settings, mode);
                if (TrySaveSettings())
                {
                    PaneModesChanged?.Invoke(this, (_settings.SidePanel, _settings.Inspector));
                }
            };
            return choice;
        }

        return Section("Layout",
            Row("Side panel (connections, credentials, recent)", Choice("Side panel", _settings.SidePanel, (s, m) => s with { SidePanelMode = m })),
            Row("Properties pane (connection and credential details)", Choice("Properties pane", _settings.Inspector, (s, m) => s with { InspectorMode = m })),
            Note(string.Join(" ", PaneModes.All.Select(mode => $"{mode.Label}: {mode.Description}"))),
            Note("A pane that isn't pinned opens over the session rather than beside it, so a remote desktop doesn't resize each time. "
                + "On Linux and macOS it opens beside a web page instead, since nothing can be drawn over the page there. Ctrl+B and Ctrl+Alt+B show and hide "
                + "the panes without changing this. The pin in each pane's header, or a right-click on the buttons beside the tabs, changes it. "
                + "Drag a pane's inner edge to change its width."));
    }

    // ---- Keyboard ----

    Control KeyboardSection()
    {
        var recorder = new ShortcutRecorder(FullScreenShortcut.FromSettings(_settings.FullScreenShortcut));
        var reset = new Button { Content = "Use the default" };
        var problem = new Notice(NoticeKind.Error) { IsVisible = false };
        problem.Message.FontSize = 12;

        void Save(KeyGesture shortcut)
        {
            recorder.StopRecording();
            recorder.Gesture = shortcut;
            problem.IsVisible = false;
            reset.IsEnabled = !FullScreenShortcut.SameAs(shortcut, FullScreenShortcut.Default);
            _settings = _settings with { FullScreenShortcut = FullScreenShortcut.ToSettings(shortcut) };
            if (TrySaveSettings())
            {
                FullScreenShortcutChanged?.Invoke(this, shortcut);
            }
        }

        // A shortcut that fails the checks is explained and not kept; the recorder keeps listening for another.
        recorder.Recorded += (_, shortcut) =>
        {
            if (FullScreenShortcut.Problem(shortcut) is { } why)
            {
                problem.Text = $"{FullScreenShortcut.Describe(shortcut)} can't be used: {why}";
                problem.IsVisible = true;
                return;
            }

            Save(shortcut);
        };
        reset.Click += (_, _) => Save(FullScreenShortcut.Default);
        reset.IsEnabled = !FullScreenShortcut.SameAs(recorder.Gesture, FullScreenShortcut.Default);

        return Section("Keyboard",
            Row("Full screen on and off", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { recorder, reset } }),
            problem,
            Note($"Click the shortcut and press the keys you want. It needs two modifier keys{(OperatingSystem.IsMacOS() ? string.Empty : ", one of them Ctrl")}, "
                + "and can't be a key the system, Orrery or typing in a session already uses. "
                + "You can always leave full screen from the bar at the top edge of the screen."),
            Note($"Terminals and remote desktops keep the keys you press, Tab included. {SessionEscape.Description} leaves the session "
                + "for its tab, and Enter goes back in."));
    }

    // ---- Accessibility ----

    Control AccessibilitySection()
    {
        var announce = new CheckBox { Content = "Send new terminal output to your screen reader", IsChecked = ScreenReader.TerminalAnnouncements(_settings) };

        // Orrery has no voice of its own: with no screen reader running, the setting does nothing anyone can hear.
        var noReader = new Notice(NoticeKind.Warning,
            "No screen reader is running, so nothing will be read out. Start Narrator (Windows logo key + Ctrl + Enter), NVDA or JAWS.");
        noReader.Message.FontSize = 12;
        _refreshScreenReaderNote = () => noReader.IsVisible = OperatingSystem.IsWindows() && announce.IsChecked == true && !ScreenReader.IsRunning;
        _refreshScreenReaderNote();
        announce.IsCheckedChanged += (_, _) =>
        {
            bool on = announce.IsChecked == true;
            _refreshScreenReaderNote();
            _settings = _settings with { TerminalAnnouncements = on };
            if (TrySaveSettings())
            {
                TerminalAnnouncementsChanged?.Invoke(this, on);
            }
        };

        return Section("Accessibility",
            announce,
            noReader,
            Note("Your screen reader reads out what a terminal prints, a few lines at a time, but not the echo of what you type. "
                + "It's on by default while a screen reader is running."));
    }

    // ---- Import and export ----

    Control ImportSection()
    {
        var import = new Button { Content = "Import…" };
        import.Click += async (_, _) => await _import();
        var export = new Button { Content = "Export…" };
        export.Click += async (_, _) => await _export();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { import } };
        if (_importSshConfig is { } importSshConfig)
        {
            var sshConfig = new Button { Content = "Import SSH config" };
            sshConfig.Click += async (_, _) => await importSshConfig();
            buttons.Children.Add(sshConfig);
        }

        buttons.Children.Add(export);
        return Section("Import and export",
            buttons,
            Note("Import adds the connections from an mRemoteNG confCons.xml, an Orrery export, a Remote Desktop (.rdp) file or an OpenSSH config file. "
                + "You can also drop the file onto the connections list. To connect with a .rdp file without saving it, right-click the connections list and choose Open Remote Desktop file. "
                + "Import SSH config reads your ~/.ssh/config: each Host name becomes an SSH connection, with its jump hosts. Nothing in the file is run."),
            Note("Export saves every connection as an Orrery file, an mRemoteNG confCons.xml or a CSV. To export one folder, right-click it. "
                + "Passwords and keys are left out unless you choose to include them, encrypted with a password you set. CSV never includes them."));
    }

    // ---- Updates ----

    Control UpdatesSection()
    {
        var current = _updates?.CurrentVersion ?? ReleaseVersion.Current;
        var version = new TextBlock { Text = _updates is { UsesOtherFeed: true } ? $"Orrery {current} (test feed)" : $"Orrery {current}" };

        var every = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = _settings.ChecksForUpdates };
        int minutes = (int)_settings.UpdateCheckInterval.TotalMinutes;
        foreach (var (label, value) in UpdateCheckChoices.Any(c => c.Minutes == minutes) ? UpdateCheckChoices : [.. UpdateCheckChoices, ($"{minutes} minutes", minutes)])
        {
            every.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        every.SelectedItem = every.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag! == minutes);
        every.SelectionChanged += (_, _) =>
        {
            if (!_loading && every.SelectedItem is ComboBoxItem { Tag: int chosen })
            {
                _settings = _settings with { UpdateCheckMinutes = chosen == AppSettings.DefaultUpdateCheckMinutes ? null : chosen };
                TrySaveSettings();
                _updates?.SettingsChanged();
            }
        };

        var automatic = new CheckBox { Content = "Check automatically", IsChecked = _settings.ChecksForUpdates };
        automatic.IsCheckedChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _settings = _settings with { UpdateChecks = automatic.IsChecked == true };
            TrySaveSettings();
            _updates?.SettingsChanged();
            every.IsEnabled = _settings.ChecksForUpdates;
        };

        var choices = new (string Label, UpdateAction Action)[]
        {
            ("Ask me", UpdateAction.Ask),
            ("Download it", UpdateAction.Download),
        };
        var buttons = choices.Select(choice => new RadioButton
        {
            Content = choice.Label,
            GroupName = "WhenUpdateFound",
            Tag = choice.Action,
            IsChecked = _settings.UpdateAction == choice.Action,
        }).ToList();
        foreach (var button in buttons)
        {
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked == true && !_loading)
                {
                    _settings = _settings with { WhenUpdateFound = (UpdateAction)button.Tag! };
                    TrySaveSettings();
                }
            };
        }

        var whenFound = new StackPanel { Spacing = 2, Children = { buttons[0], buttons[1] } };
        AutomationProperties.SetName(whenFound, "When one is out");

        var previews = new CheckBox { Content = "Previews", IsChecked = _settings.OffersPreviewUpdates };
        const string previewsNote = "Previews are offered too: newer versions, less tried.";
        AutomationProperties.SetHelpText(previews, previewsNote);
        previews.IsCheckedChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _settings = _settings with { UpdatePreviews = previews.IsChecked == true };
            TrySaveSettings();
        };

        var section = Section("Updates",
            Row("Version", version),
            automatic,
            Row("Then every", every),
            new TextBlock { Text = "When one is out" },
            whenFound,
            previews,
            Note(previewsNote));

        if (_updates is { } updates)
        {
            var check = new Button { Content = "Check now" };
            var result = new TextBlock
            {
                FontSize = 12,
                Foreground = Dialogs.MutedBrush,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            check.Click += async (_, _) => await CheckNowAsync(updates, check, result);
            var whatsNew = new Button { Content = "What's new" };
            whatsNew.Click += async (_, _) => await updates.ShowWhatsNewAsync();
            section.Children.Add(new DockPanel
            {
                Children = { Pane.Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { check, whatsNew } }, Dock.Left), result },
            });
            UpdateCheckResult = result;
        }

        section.Children.Add(Note("Checks GitHub as Orrery starts, then on that schedule while it's open. Sends only your version."));
        return section;
    }

    /// <summary>What Check now found, beside the button; null without update checks.</summary>
    internal TextBlock? UpdateCheckResult { get; private set; }

    /// <summary>Checks by hand, says what it found beside the button, and opens a newer release's details.</summary>
    async Task CheckNowAsync(UpdateController updates, Button check, TextBlock result)
    {
        check.IsEnabled = false;
        result.Foreground = Dialogs.MutedBrush;
        result.Text = "Checking…";
        UpdateOffer? offer;
        try
        {
            offer = await updates.CheckNowAsync();
        }
        catch (UpdateException e)
        {
            result.Foreground = Dialogs.DangerBrush;
            result.Text = e.Message;
            ScreenReader.Announce(result, result.Text, "Orrery.Update", ScreenReader.MostRecent);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            check.IsEnabled = true;
        }

        result.Text = offer is null ? $"You have the latest version ({updates.CurrentVersion})." : $"{updates.BarText}.";
        ScreenReader.Announce(result, result.Text, "Orrery.Update", ScreenReader.MostRecent);
        if (offer is not null)
        {
            await updates.ShowDetailsAsync();
        }
    }

    // ---- Logs ----

    Control LogsSection()
    {
        var path = new SelectableTextBlock { Text = _location.LogDir, FontFamily = Dialogs.MonoFont, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var open = new Button { Content = "Open log folder" };
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(_location.LogDir);
            OpenFolder(_location.LogDir);
        };
        var deleted = Note(string.Empty);
        deleted.IsVisible = false;
        var delete = new Button { Content = "Delete logs" };
        delete.Click += async (_, _) =>
        {
            if (!await Dialogs.ConfirmAsync(Owner, "Delete the logs?",
                    "Every log file this computer wrote is deleted, today's too, and the log starts again in a new file. Other computers' logs in a shared folder stay. This can't be undone.", "Delete logs"))
            {
                return;
            }

            int count = AppLog.DeleteLogs(_location);
            deleted.Text = count == 1 ? "Deleted 1 log file." : $"Deleted {count} log files.";
            deleted.IsVisible = true;
            ScreenReader.Announce(deleted, deleted.Text, "Orrery.Logs", ScreenReader.MostRecent);
        };

        var keepFor = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        int days = (int)_settings.KeepLogsFor.TotalDays;
        foreach (var choice in LogDayChoices.Contains(days) ? LogDayChoices : [.. LogDayChoices, days])
        {
            keepFor.Items.Add(new ComboBoxItem { Content = $"{choice} days", Tag = choice });
        }

        keepFor.SelectedItem = keepFor.Items.OfType<ComboBoxItem>().First(i => (int)i.Tag! == days);
        var keepNote = Note(KeepNote(days));
        keepFor.SelectionChanged += (_, _) =>
        {
            if (keepFor.SelectedItem is ComboBoxItem { Tag: int chosen })
            {
                _settings = _settings with { LogDays = chosen == AppSettings.DefaultLogDays ? null : chosen };
                TrySaveSettings();
                AppLog.KeepFor = _settings.KeepLogsFor;
                keepNote.Text = KeepNote(chosen);
            }
        };

        var detailed = new CheckBox { Content = "Detailed logging, for troubleshooting", IsChecked = AppLog.Detailed };
        detailed.IsCheckedChanged += (_, _) => AppLog.Detailed = detailed.IsChecked == true;
        var showNames = new CheckBox { Content = "Show names and addresses in logs", IsChecked = AppLog.ShowNames };
        showNames.IsCheckedChanged += (_, _) => AppLog.ShowNames = showNames.IsChecked == true;

        var lookup = new TextBox { FontFamily = Dialogs.MonoFont, PlaceholderText = "host-3f9a2c, or a name or address" };
        var found = Note(string.Empty);
        found.IsVisible = false;
        lookup.TextChanged += (_, _) =>
        {
            found.Text = LookupText(lookup.Text ?? string.Empty);
            found.IsVisible = found.Text.Length > 0;
        };

        return Section("Logs",
            Row("Log folder", path),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, delete } },
            deleted,
            Row("Keep logs for", keepFor),
            keepNote,
            detailed,
            showNames,
            Note("Off, the log writes hosts, IP and web addresses, login names, connection and credential names and file paths as tags "
                + "such as host-3f9a2c, so it can be shared in an issue without saying which servers or people it's about. The same name "
                + "always gets the same tag. Turn this on to read names yourself; it switches off when Orrery closes."),
            Row("Look up a tag", lookup),
            found);
    }

    static readonly int[] LogDayChoices = [7, 14, 30];

    static string KeepNote(int days) =>
        $"One file a day (a new one past 10 MB), kept for {days} days and to 100 MB in all. The log records what connected and what went "
        + "wrong. It never holds passwords, keys, keystrokes or anything shown on screen. Detailed logging adds more and switches off when Orrery closes.";

    // Tags are turned back only here, from what Orrery has seen since it started; nothing that could do it is in the log.
    internal static string LookupText(string text)
    {
        if (text.Trim().Length == 0)
        {
            return string.Empty;
        }

        var matches = AppLog.Lookup(text);
        if (LogMask.IsTag(text))
        {
            return matches.Count == 0
                ? "Not seen since Orrery started. Type the name or address you think it is to see its tag."
                : string.Join("\n", matches.Select(m => $"{m.Tag} is {Described(m.Kind)} {m.Value}"));
        }

        return matches.Count == 0 ? "Shown as it is in the log." : "In the log: " + string.Join(", ", matches.Select(m => $"{m.Tag} ({AsA(m.Kind)})"));
    }

    static string Described(LogMaskKind kind) => kind switch
    {
        LogMaskKind.Host => "the host",
        LogMaskKind.User => "the login name",
        LogMaskKind.Name => "the name",
        LogMaskKind.Path => "the file",
        LogMaskKind.Token => "the token",
        LogMaskKind.Mac => "the hardware address",
        _ => "the address",
    };

    static string AsA(LogMaskKind kind) => kind switch
    {
        LogMaskKind.Host => "as a host",
        LogMaskKind.User => "as a login name",
        LogMaskKind.Name => "as a name",
        _ => "as a file",
    };

    // ---- About ----

    static Control AboutSection()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev";
        return Section("About",
            new TextBlock { Text = $"Orrery {version}" },
            Note("Copyright © 2026 InspireNXE. Released under the MIT License."));
    }

    // ---- Layout helpers ----

    static StackPanel Section(string title, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        var heading = new TextBlock { Text = title.ToUpperInvariant(), Classes = { "section" } };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(heading, 2);
        panel.Children.Add(heading);
        panel.Children.AddRange(children);
        return panel;
    }

    /// <summary>A section on its page: a panel with a hairline border, so where one ends and the next begins is plain.</summary>
    static Border Card(Control section) => new()
    {
        Background = Dialogs.PanelBrush,
        BorderBrush = Pane.LineBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(20, 16, 20, 20),
        Child = section,
    };

    static Control Row(string label, Control value) => Dialogs.Field(label, value);

    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };

    internal static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.For("Settings").LogWarning("Couldn't open {Path}: {Message}", path, e.Message);
        }
    }
}
