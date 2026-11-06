using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using Orrery.App.Connections;
using Orrery.App.Credentials;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;

namespace Orrery.App;

// The commands the Ctrl+K palette offers besides connecting (docs/roadmap.md, "Command palette with actions").
public partial class MainWindow
{
    /// <summary>Asked before connecting to more than this many at once.</summary>
    const int ConnectManyAsk = 10;

    /// <summary>
    /// Everything the palette can run, built as it opens. The current session's commands are its own menu (the More
    /// button and the tab's right-click), read item by item, so the two can't drift apart.
    /// </summary>
    List<PaletteCommand> PaletteCommands()
    {
        var commands = new List<PaletteCommand>();
        if (_currentTab is { } tab)
        {
            string group = $"This session · {tab.Origin.Title}";
            foreach (var control in SessionMenu(tab))
            {
                commands.AddRange(FromMenu(control, null, group));
            }

            commands.AddRange(SavedCommandPaletteCommands(tab));
        }

        commands.AddRange(SessionsCommands());
        commands.AddRange(FolderCommands());
        commands.AddRange(ConnectionCommands());
        commands.AddRange(WindowCommands());
        return commands;
    }

    /// <summary>A menu item as commands: itself, or each item of its submenu ("Send keys: Ctrl+Alt+Del").</summary>
    internal static IEnumerable<PaletteCommand> FromMenu(Control control, string? parent, string group)
    {
        if (control is not MenuItem { IsEnabled: true, IsVisible: true, Header: string header } item || item.Tag is SavedCommandsMenuTag)
        {
            yield break;
        }

        string title = parent is null ? header : $"{parent}: {header}";
        if (item.Items.Count > 0)
        {
            foreach (var child in item.Items.OfType<Control>())
            {
                foreach (var command in FromMenu(child, title, group))
                {
                    yield return command;
                }
            }

            yield break;
        }

        yield return new PaletteCommand(title, group, () =>
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            return Task.CompletedTask;
        }) { Shortcut = item.InputGesture is { } gesture ? Input.FullScreenShortcut.Describe(gesture) : null };
    }

    /// <summary>A session that holds a connection open: not a local shell or a web page, which have none to end.</summary>
    static bool Disconnectable(SessionTab tab) => !tab.Origin.Local && tab.View is not WebPageView
        && tab.Session.State is SessionState.Connected or SessionState.Connecting;

    static bool Reconnectable(SessionTab tab) => tab.View is not WebPageView
        && tab.Session.State is not (SessionState.Connected or SessionState.Connecting);

    IEnumerable<PaletteCommand> SessionsCommands()
    {
        const string group = "Open sessions";
        var down = _tabs.Where(Reconnectable).ToList();
        if (down.Count > 0)
        {
            yield return new PaletteCommand($"Reconnect all disconnected sessions ({down.Count})", group, async () =>
            {
                foreach (var tab in down.Where(_tabs.Contains).ToList())
                {
                    await ReconnectAsync(tab);
                }
            }) { Keywords = ["restart", "dropped"] };
        }

        var up = _tabs.Where(Disconnectable).ToList();
        if (up.Count > 0)
        {
            yield return new PaletteCommand($"Disconnect all sessions ({up.Count})", group, () => DisconnectAsync(up))
            {
                Keywords = ["everything", "end"],
            };
        }

        if (_tabs.Count > 0)
        {
            yield return new PaletteCommand($"Close all tabs ({_tabs.Count})", group, async () =>
            {
                foreach (var tab in _tabs.ToList())
                {
                    await CloseTabAsync(tab.Session);
                }
            }) { Keywords = ["sessions", "everything"] };
        }
    }

    async Task DisconnectAsync(IEnumerable<SessionTab> tabs)
    {
        foreach (var tab in tabs.Where(_tabs.Contains).ToList())
        {
            tab.EndedByUser = true;
            await tab.Session.DisconnectAsync();
        }
    }

    /// <summary>For each folder: connect everything in it, and disconnect what's open from it.</summary>
    IEnumerable<PaletteCommand> FolderCommands()
    {
        const string group = "Folders";
        foreach (var folder in _tree.Root.Descendants().OfType<ConnectionFolder>())
        {
            var entries = folder.Descendants().OfType<ConnectionEntry>().ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            var ids = entries.Select(e => e.Id).ToHashSet();
            var open = _tabs.Where(t => t.Origin.EntryId is { } id && ids.Contains(id) && Disconnectable(t)).ToList();
            if (open.Count > 0)
            {
                yield return new PaletteCommand($"Disconnect everything in {folder.Name} ({open.Count})", group, () => DisconnectAsync(open), folder.Name)
                {
                    Keywords = ["all", "end"],
                };
            }

            yield return new PaletteCommand($"Connect everything in {folder.Name} ({entries.Count})", group, async () =>
            {
                if (entries.Count > ConnectManyAsk
                    && !await Dialogs.ConfirmAsync(this, $"Connect {entries.Count} connections?", $"Everything in {folder.Name} opens, each in its own tab.", "Connect"))
                {
                    return;
                }

                foreach (var entry in entries)
                {
                    await ConnectAsync(entry);
                }
            }, folder.Name) { Keywords = ["all", "open"] };
        }
    }

    /// <summary>What can be done with each saved connection besides connecting, which its own row does.</summary>
    IEnumerable<PaletteCommand> ConnectionCommands()
    {
        const string group = "Connections";
        foreach (var entry in _tree.AllConnections())
        {
            var resolved = SettingsResolver.Resolve(entry);
            string name = entry.Name;
            yield return new PaletteCommand($"Edit {name}", group, () =>
            {
                EditEntry(entry);
                return Task.CompletedTask;
            }, name) { Keywords = ["settings", "properties", "change"] };

            if (resolved.CredentialId.Value is { } credentialId && credentialId != InheritableSettings.NoCredential)
            {
                yield return new PaletteCommand($"Copy password for {name}", group, () => CopyPasswordAsync(entry, credentialId), name)
                {
                    Keywords = ["secret", "credential", "clipboard"],
                };
            }

            if (!string.IsNullOrEmpty(entry.Host))
            {
                yield return new PaletteCommand($"Copy address of {name}", group, () => CopyTextAsync(entry.Host, $"Copied the address of {name}"), name)
                {
                    Keywords = ["host", "ip", "clipboard"],
                };
            }

            if (!string.IsNullOrEmpty(entry.MacAddress))
            {
                yield return new PaletteCommand($"Wake {name}", group, async () =>
                {
                    // The entry itself, not the selection: a filtered tree may not show it, and then can't select it.
                    Select(entry.Id);
                    await WakeAsync([entry]);
                }, name) { Keywords = ["wol", "lan", "power"] };
            }

            if (entry.ManagementUrl is { Length: > 0 })
            {
                yield return new PaletteCommand($"Open management page of {name}", group, () => OpenManagementPageAsync(entry), name)
                {
                    Keywords = ["web", "admin", "console"],
                };
            }

            if (resolved.Protocol.Value == Protocol.Ssh)
            {
                yield return new PaletteCommand($"Browse files on {name}", group, () => ConnectAsync(entry, files: true), name)
                {
                    Keywords = ["sftp", "transfer"],
                };
            }
        }
    }

    IEnumerable<PaletteCommand> WindowCommands()
    {
        const string group = "Orrery";
        string command = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        yield return new PaletteCommand("Settings", group, Run(OpenSettings)) { Shortcut = $"{command}+,", Keywords = ["preferences", "options"] };
        yield return new PaletteCommand("Show or hide the side panel", group, Run(ToggleSidePanel)) { Shortcut = $"{command}+B", Keywords = ["toggle", "connections", "pane"] };
        yield return new PaletteCommand("Show or hide the properties pane", group, Run(ToggleInspector)) { Shortcut = $"{command}+Alt+B", Keywords = ["toggle", "settings", "inspector"] };
        yield return new PaletteCommand("New connection", group, Run(() => OnNewConnectionClick(null, new RoutedEventArgs())), null) { Keywords = ["add", "create"] };
        yield return new PaletteCommand("New folder", group, Run(() => OnNewFolderClick(null, new RoutedEventArgs()))) { Keywords = ["add", "create"] };
        yield return new PaletteCommand("Local terminal", group, Run(() => OpenLocalTerminal())) { Shortcut = $"{command}+Shift+T", Keywords = ["shell", "console", "powershell", "bash"] };
        if (OperatingSystem.IsWindows())
        {
            yield return new PaletteCommand("Local terminal as administrator", group, Run(() => OpenLocalTerminal(elevated: true)))
            {
                Keywords = ["admin", "elevated", "elevate", "uac", "sudo", "powershell", "run as"],
            };
        }
        yield return new PaletteCommand("Recordings", group, Run(() => ShowRecordings())) { Keywords = ["playback", "videos"] };
        yield return new PaletteCommand("Import connections…", group, ChooseImportFileAsync) { Keywords = ["mremoteng", "ssh", "config"] };
        yield return new PaletteCommand("Open Remote Desktop file…", group, Run(() => OnOpenRdpFileClick(null, new RoutedEventArgs()))) { Keywords = ["rdp"] };
        yield return new PaletteCommand("Defaults for all connections…", group, Run(() => OnEditDefaultsClick(null, new RoutedEventArgs()))) { Keywords = ["inherit", "settings"] };
        if (_credentials is VaultFileStore { IsLocked: false })
        {
            yield return new PaletteCommand("Lock the vault", group, Run(() => OnLockVaultClick(null, new RoutedEventArgs()))) { Keywords = ["credentials", "passwords"] };
        }

        if (_updates is not null)
        {
            yield return new PaletteCommand("Check for updates", group, CheckForUpdatesAsync) { Keywords = ["upgrade", "version", "new", "release"] };
            yield return new PaletteCommand("What's new", group, _updates.ShowWhatsNewAsync) { Keywords = ["changelog", "release notes", "version", "changes"] };
        }
    }

    static Func<Task> Run(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };

    async Task CopyTextAsync(string text, string notice)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            ShowNotice(notice);
        }
    }

    /// <summary>A connection's saved password on the clipboard for half a minute, unlocking the vault first if need be.</summary>
    async Task CopyPasswordAsync(ConnectionEntry entry, Guid credentialId)
    {
        if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, this))
        {
            return;
        }

        if (await _credentials.GetAsync(credentialId) is not { } credential)
        {
            await Dialogs.ShowMessageAsync(this, "No password to copy", $"The credential {entry.Name} uses no longer exists.");
            return;
        }

        if (credential.Kind != CredentialKind.Password)
        {
            await Dialogs.ShowMessageAsync(this, "No password to copy", $"{entry.Name} logs in with an SSH key, \"{credential.Name}\", not a password.");
            return;
        }

        if (!await SecretClipboard.CopyAsync(this, credential.Secret))
        {
            await Dialogs.ShowMessageAsync(this, "Couldn't copy the password", "Another program is holding the clipboard. Try again in a moment.");
            return;
        }

        StorageLog.LogInformation("Copied the password of credential \"{Credential}\" for \"{Name}\"", credential.Name, entry.Name);
        ShowNotice($"Copied the password for {entry.Name}. It's cleared from the clipboard in {SecretClipboard.ClearAfter.TotalSeconds:0} seconds.");
    }
}
