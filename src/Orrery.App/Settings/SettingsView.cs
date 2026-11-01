using System.Diagnostics;
using System.Reflection;
using Avalonia;
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
using Orrery.Core.Credentials;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Orrery.Protocols.Local;

namespace Orrery.App.Settings;

/// <summary>
/// The settings page, shown in the main window over the sessions: storage (where the data folder is,
/// Move data…), credentials (which store, when the vault locks), import and About. Changes that swap files
/// out from under the running app end in <see cref="RestartRequested"/> rather than a live switch.
/// </summary>
sealed class SettingsView : UserControl
{
    static readonly (string Label, int Minutes)[] AutoLockChoices =
    [
        ("5 minutes", 5), ("15 minutes", 15), ("30 minutes", 30), ("1 hour", 60), ("4 hours", 240), ("Never", 0),
    ];

    static readonly IBrush PageBrush = new SolidColorBrush(Color.Parse("#0E1014")).ToImmutable();

    readonly DataLocation _location;
    readonly AppSettingsStore _settingsStore;
    readonly ICredentialStore _credentials;
    readonly Func<Task> _saveEverything;
    readonly Func<Task> _import;
    readonly Func<Task> _export;
    readonly ComboBox _storeChoice = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox _autoLock = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock _autoLockNote = Note(string.Empty);

    AppSettings _settings;
    bool _loading = true;

    /// <param name="saveEverything">Writes pending edits to disk, so a copy of the data folder is complete.</param>
    /// <param name="import">Asks for an mRemoteNG file and imports it.</param>
    public SettingsView(DataLocation location, AppSettingsStore settingsStore, ICredentialStore credentials, Func<Task> saveEverything, Func<Task> import, Func<Task> export)
    {
        _location = location;
        _settingsStore = settingsStore;
        _credentials = credentials;
        _saveEverything = saveEverything;
        _import = import;
        _export = export;
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
                new ScrollViewer
                {
                    Content = new StackPanel
                    {
                        Margin = new Thickness(32, 24, 32, 32),
                        MaxWidth = 600,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Spacing = 28,
                        Children =
                        {
                            StorageSection(),
                            CredentialsSection(),
                            RemoteDesktopSection(),
                            LocalTerminalSection(),
                            HostStatusSection(),
                            ColoursSection(),
                            KeyboardSection(),
                            AccessibilitySection(),
                            ImportSection(),
                            LogsSection(),
                            AboutSection(),
                        },
                    },
                },
            },
        };

        _loading = false;
    }

    /// <summary>The close button or Esc.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>A new full-screen shortcut was saved.</summary>
    public event EventHandler<KeyGesture>? FullScreenShortcutChanged;

    /// <summary>Terminal announcements were turned on (true) or off.</summary>
    public event EventHandler<bool>? TerminalAnnouncementsChanged;

    /// <summary>A new terminal colour scheme was chosen, for open terminals to follow.</summary>
    public event EventHandler<TerminalColourScheme>? TerminalColoursChanged;

    /// <summary>Checking whether hosts are up was turned on (true) or off.</summary>
    public event EventHandler<bool>? HostStatusChecksChanged;

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
            Title = "Choose an empty folder for Orrery's data",
            AllowMultiple = false,
        });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } target)
        {
            return;
        }

        if (DataMover.CheckTarget(_location.DataDir, target) is { } problem)
        {
            await Dialogs.ShowMessageAsync(Owner, "Can't move the data there", problem);
            return;
        }

        if (DataMover.WarnAboutTarget(target) is { } warning
            && !await Dialogs.ConfirmAsync(Owner, "Other people can change this folder", $"{warning}\n\n{target}", "Use anyway"))
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Owner, "Move Orrery's data?",
                $"Orrery will copy its own files (connections, settings, the vault, backups and import reports) to\n{target}\ncheck every file, and restart using the new folder. Open sessions will close.\n\nAnything else in the current folder stays where it is. The old copy is kept until you choose to delete it.",
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
            _storeChoice.Items.Add(new ComboBoxItem { Content = Capitalised(KeychainName), Tag = CredentialStoreKind.OsKeychain });
            _storeChoice.Items.Add(new ComboBoxItem { Content = "Encrypted vault file (master password)", Tag = CredentialStoreKind.VaultFile });
            _storeChoice.SelectedIndex = current == CredentialStoreKind.OsKeychain ? 0 : 1;
            _storeChoice.SelectionChanged += async (_, _) => await SwitchStoreAsync();
            section.Children.Add(Row("Saved credentials are kept in", _storeChoice));
            section.Children.Add(Note($"{Capitalised(KeychainName)} is protected by your login on this computer. The vault file is one encrypted file you can back up or move, opened with a master password."));
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
                    await Dialogs.ShowMessageAsync(Owner, "Master password changed", "Use the new password the next time you unlock the vault.");
                }
            };
            section.Children.Add(change);
        }

        section.Children.Add(Row("Lock the vault when unused for", _autoLock));
        section.Children.Add(_autoLockNote);
        UpdateAutoLockNote(current);
        return section;
    }

    void UpdateAutoLockNote(CredentialStoreKind kind)
    {
        _autoLock.IsEnabled = kind == CredentialStoreKind.VaultFile;
        _autoLockNote.Text = kind == CredentialStoreKind.VaultFile
            ? "Open sessions keep running when the vault locks."
            : $"Applies only to the vault file. {Capitalised(KeychainName)} unlocks with your login.";
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

    static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    bool TrySaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = Dialogs.ShowMessageAsync(Owner, "Couldn't save settings", e.Message);
            return false;
        }
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

        return Section("Remote desktops",
            zoom,
            Note("Remote desktops aren't told this display's scaling, so without this their text looks as small as it would at 100%. "
                + "Enlarged text is sharp at 200% and slightly soft at 125% or 150%. Applies to new connections. "
                + "A connection or folder can set its own choice in its properties."));
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

        return Section("Host status",
            check,
            Note("About once a minute, Orrery opens a TCP connection to the host and port of each connection you can see in the list, "
                + "and shows a mark beside its protocol: a filled dot if it answered, a cross if it didn't, a dashed ring if it can't be checked. "
                + "The mark before the name is the session's state, not the host's. It sends nothing else, and skips connections that go through a jump host. "
                + "Some networks treat this as a port scan, so turn it off there. Off, nothing is sent and no marks show."));
    }

    // ---- Colours ----

    Control ColoursSection()
    {
        var colourBlind = new CheckBox { Content = "Colour-blind friendly colours", IsChecked = _settings.UseColourBlindColours };
        colourBlind.IsCheckedChanged += (_, _) =>
        {
            bool on = colourBlind.IsChecked == true;
            _settings = _settings with { ColourBlindColours = on ? true : null };
            TrySaveSettings();
            Palette.Use(on);
            Log.For("Settings").LogInformation("Colour-blind friendly colours {State}", on ? "on" : "off");
        };

        var terminal = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        terminal.Items.Add(new ComboBoxItem { Content = "Standard", Tag = TerminalColourScheme.Standard });
        terminal.Items.Add(new ComboBoxItem { Content = "Colour-blind friendly", Tag = TerminalColourScheme.ColourBlind });
        var scheme = _settings.TerminalColours ?? TerminalColourScheme.Standard;
        terminal.SelectedItem = terminal.Items.OfType<ComboBoxItem>().First(i => (TerminalColourScheme)i.Tag! == scheme);
        terminal.SelectionChanged += (_, _) =>
        {
            if (_loading || terminal.SelectedItem is not ComboBoxItem { Tag: TerminalColourScheme chosen })
            {
                return;
            }

            _settings = _settings with { TerminalColours = chosen == TerminalColourScheme.Standard ? null : chosen };
            TrySaveSettings();
            TerminalColoursChanged?.Invoke(this, chosen);
            Log.For("Settings").LogInformation("Terminal colours set to {Scheme}", chosen);
        };

        return Section("Colours",
            colourBlind,
            Note("Session states, warnings and errors use blue, yellow and vermilion, which stay apart with red-green or blue-yellow colour blindness. "
                + "They always have a shape, an icon or a word as well."),
            Row("Terminal colours", terminal),
            Note("Colour-blind friendly turns red into vermilion and green into bluish green, so programs that mark things red or green stay readable. "
                + "Applies to open terminals too."));
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
                + "You can always leave full screen from the bar at the top edge of the screen."));
    }

    // ---- Accessibility ----

    Control AccessibilitySection()
    {
        var announce = new CheckBox { Content = "Read new terminal output aloud", IsChecked = ScreenReader.TerminalAnnouncements(_settings) };
        announce.IsCheckedChanged += (_, _) =>
        {
            bool on = announce.IsChecked == true;
            _settings = _settings with { TerminalAnnouncements = on };
            if (TrySaveSettings())
            {
                TerminalAnnouncementsChanged?.Invoke(this, on);
            }
        };

        return Section("Accessibility",
            announce,
            Note("Your screen reader reads out what a terminal prints, a few lines at a time, but not the echo of what you type. "
                + "It's on by default while a screen reader is running."),
            Note($"Terminals and remote desktops keep the keys you press, Tab included. {SessionEscape.Description} leaves the session "
                + "for its tab, and Enter goes back in."));
    }

    // ---- Import and export ----

    Control ImportSection()
    {
        var import = new Button { Content = "Import…" };
        import.Click += async (_, _) => await _import();
        var export = new Button { Content = "Export…" };
        export.Click += async (_, _) => await _export();
        return Section("Import and export",
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { import, export } },
            Note("Import adds the connections from an mRemoteNG confCons.xml or an Orrery export. You can also drop the file onto the connections list."),
            Note("Export saves every connection as an Orrery file, an mRemoteNG confCons.xml or a CSV. To export one folder, right-click it. "
                + "Passwords and keys are left out unless you choose to include them, encrypted with a password you set. CSV never includes them."));
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
        var detailed = new CheckBox { Content = "Detailed logging, for troubleshooting", IsChecked = AppLog.Detailed };
        detailed.IsCheckedChanged += (_, _) => AppLog.Detailed = detailed.IsChecked == true;

        return Section("Logs",
            Row("Log folder", path),
            open,
            detailed,
            Note("One file a day, kept for two weeks. The log records what connected where and what went wrong. It never holds "
                + "passwords, keys, keystrokes or anything shown on screen. Detailed logging adds more and switches off when Orrery closes."));
    }

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

    static Control Row(string label, Control value) => Dialogs.Field(label, value);

    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Dialogs.MutedBrush, TextWrapping = TextWrapping.Wrap };

    static void OpenFolder(string path)
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
