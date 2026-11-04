using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Orrery.App.Connections;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;

namespace Orrery.App;

// Saved commands: sent to a terminal from its menu or the Ctrl+K palette, asking first when the command or its folder says to.
public partial class MainWindow
{
    static readonly ILogger CommandLog = Log.For("Commands");

    /// <summary>Marks the session menu's Commands submenu, which the palette lists its own way (with each command's text).</summary>
    internal const string SavedCommandsMenuTag = "saved-commands";

    /// <summary>The saved connection a tab was opened from, or null for a quick connect or a local shell.</summary>
    ConnectionEntry? EntryFor(SessionTab tab) => tab.Origin.EntryId is { } id ? _tree.Find(id) as ConnectionEntry : null;

    /// <summary>The Commands submenu and Save selection as command, for a terminal opened from a saved connection.</summary>
    IEnumerable<Control> SavedCommandMenuItems(SessionTab tab, TerminalView terminal)
    {
        if (EntryFor(tab) is not { } entry)
        {
            yield break;
        }

        bool connected = tab.Session.State == SessionState.Connected;
        var commands = SavedCommands.For(entry);
        if (commands.Count > 0)
        {
            var menu = new MenuItem { Header = "Commands", Tag = SavedCommandsMenuTag };
            foreach (var command in commands)
            {
                // The ellipsis says a window comes first, as for any menu item that asks.
                var item = MenuItemFor(command.Confirm && command.Command.PressEnter ? $"{command.Command.Name}…" : command.Command.Name,
                    async () => await SendSavedCommandAsync(tab, command, command.Command.PressEnter), enabled: connected);
                ToolTip.SetTip(item, command.Command.Text);
                menu.Items.Add(item);
            }

            yield return menu;
        }

        yield return MenuItemFor("Save selection as command…", async () => await SaveSelectionAsCommandAsync(tab, entry, terminal), enabled: terminal.HasSelection);
    }

    /// <summary>The current terminal's saved commands for the palette, each with its text under its name, and Shift+Enter to type it without Enter.</summary>
    IEnumerable<PaletteCommand> SavedCommandPaletteCommands(SessionTab tab)
    {
        if (tab.View is not TerminalView || tab.Session.State != SessionState.Connected || EntryFor(tab) is not { } entry)
        {
            yield break;
        }

        string group = $"Saved commands · {tab.Origin.Title}";
        foreach (var command in SavedCommands.For(entry))
        {
            var text = SavedCommands.Expand(command.Command.Text, entry, UsernameFor(tab, entry));
            yield return new PaletteCommand(command.Command.Name, group, () => SendSavedCommandAsync(tab, command, command.Command.PressEnter))
            {
                Detail = text,
                AltRun = command.Command.PressEnter ? () => SendSavedCommandAsync(tab, command, pressEnter: false) : null,
                AltHint = command.Command.PressEnter ? "types it without Enter" : null,
                Keywords = ["send", "run", "command", command.From.Name],
            };
        }
    }

    static string? UsernameFor(SessionTab tab, ConnectionEntry entry) =>
        tab.Session.Target.Username is { Length: > 0 } user ? user : SettingsResolver.Resolve(entry).Username.Value;

    /// <summary>
    /// Types a saved command into the tab's terminal. When it asks first and would run (Enter pressed), the prompt
    /// shows it and where it goes; typed without Enter, nothing runs until the user presses it, so it doesn't ask.
    /// </summary>
    internal async Task SendSavedCommandAsync(SessionTab tab, ResolvedCommand command, bool pressEnter)
    {
        if (!_tabs.Contains(tab) || tab.View is not TerminalView terminal || EntryFor(tab) is not { } entry)
        {
            return;
        }

        var text = SavedCommands.Expand(command.Command.Text, entry, UsernameFor(tab, entry));
        if (command.Confirm && pressEnter)
        {
            var where = string.IsNullOrEmpty(entry.Path) ? entry.Host : $"{entry.Host} · {entry.Path}";
            var owner = TopLevel.GetTopLevel(terminal) as Window ?? this;
            if (!await new ConfirmCommandWindow(command.Command.Name, text, entry.Name, where, pressEnter).ShowDialog<bool>(owner))
            {
                terminal.Focus();
                return;
            }
        }

        // The session may have dropped while the prompt was up.
        if (tab.Session.State != SessionState.Connected)
        {
            ShowNotice($"{entry.Name} isn't connected, so \"{command.Command.Name}\" wasn't sent.");
            return;
        }

        terminal.SendCommand(text, pressEnter);
        terminal.Focus();
        CommandLog.LogInformation("Sent the saved command \"{Command}\" to \"{Name}\"{Enter}", command.Command.Name, entry.Name, pressEnter ? "" : " without Enter");
    }

    /// <summary>Saves the terminal's selection as a new command on the tab's connection.</summary>
    async Task SaveSelectionAsCommandAsync(SessionTab tab, ConnectionEntry entry, TerminalView terminal)
    {
        var owner = TopLevel.GetTopLevel(terminal) as Window ?? this;
        var window = new SavedCommandWindow(null, entry.Name) { Seed = terminal.SelectedText?.Trim() };
        if (await window.ShowDialog<SavedCommand?>(owner) is not { } saved || _tree.Find(entry.Id) is null)
        {
            return;
        }

        (entry.Commands ??= []).Add(saved);
        _tree.NotifyChanged();
        _properties.CommandsChanged(entry);
        ShowNotice($"Saved \"{saved.Name}\" on {entry.Name}. It's under Commands in the session's menu and in Ctrl+K.");
        if (_tabs.Contains(tab))
        {
            terminal.Focus();
        }
    }
}
