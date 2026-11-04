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

    /// <summary>
    /// Where a terminal's commands come from and what fills in their placeholders: its saved connection, or for a
    /// local shell or a quick connect (<see cref="Entry"/> null), the defaults for all connections and the address
    /// it was opened to.
    /// </summary>
    sealed record CommandSource(ConnectionEntry? Entry, string Name, string Host, string? Username, IReadOnlyList<ResolvedCommand> Commands)
    {
        public string Expand(string text) => SavedCommands.Expand(text, Host, Username, Name);

        /// <summary>Where a command goes, for the ask-first prompt: the host, and the folder path for a saved connection.</summary>
        public string Where => Entry is { Path.Length: > 0 } entry ? $"{Host} · {entry.Path}" : Host;
    }

    CommandSource CommandSourceFor(SessionTab tab)
    {
        if (EntryFor(tab) is { } entry)
        {
            var user = tab.Session.Target.Username is { Length: > 0 } name ? name : SettingsResolver.Resolve(entry).Username.Value;
            return new CommandSource(entry, entry.Name, entry.Host, user, SavedCommands.For(entry));
        }

        var target = tab.Session.Target;
        return new CommandSource(null, tab.Origin.Title, target.Host, target.Username, SavedCommands.ForDefaults(_tree.Root));
    }

    /// <summary>
    /// The Commands submenu, with New command… under any saved ones so there's a way in before the first, and Save
    /// selection as command, for any terminal.
    /// </summary>
    IEnumerable<Control> SavedCommandMenuItems(SessionTab tab, TerminalView terminal)
    {
        bool connected = tab.Session.State == SessionState.Connected;
        var source = CommandSourceFor(tab);
        var menu = new MenuItem { Header = "Commands", Tag = SavedCommandsMenuTag };
        foreach (var command in source.Commands)
        {
            // The ellipsis says a window comes first, as for any menu item that asks.
            var item = MenuItemFor(command.Confirm && command.Command.PressEnter ? $"{command.Command.Name}…" : command.Command.Name,
                async () => await SendSavedCommandAsync(tab, command, command.Command.PressEnter), enabled: connected);
            ToolTip.SetTip(item, command.Command.Text);
            menu.Items.Add(item);
        }

        if (source.Commands.Count > 0)
        {
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(MenuItemFor("New command…", async () => await NewCommandAsync(tab, terminal, seed: null)));
        yield return menu;

        yield return MenuItemFor("Save selection as command…", async () => await NewCommandAsync(tab, terminal, terminal.SelectedText?.Trim()), enabled: terminal.HasSelection);
    }

    /// <summary>The current terminal's saved commands for the palette, each with its text under its name, and Shift+Enter to type it without Enter.</summary>
    IEnumerable<PaletteCommand> SavedCommandPaletteCommands(SessionTab tab)
    {
        if (tab.View is not TerminalView terminal)
        {
            yield break;
        }

        var source = CommandSourceFor(tab);
        string group = $"Saved commands · {tab.Origin.Title}";
        yield return new PaletteCommand("New saved command…", group, () => NewCommandAsync(tab, terminal, seed: null))
        {
            Keywords = ["add", "save", "command", "snippet", source.Name],
        };

        if (tab.Session.State != SessionState.Connected)
        {
            yield break;
        }

        foreach (var command in source.Commands)
        {
            yield return new PaletteCommand(command.Command.Name, group, () => SendSavedCommandAsync(tab, command, command.Command.PressEnter))
            {
                Detail = source.Expand(command.Command.Text),
                AltRun = command.Command.PressEnter ? () => SendSavedCommandAsync(tab, command, pressEnter: false) : null,
                AltHint = command.Command.PressEnter ? "types it without Enter" : null,
                Keywords = ["send", "run", "command", command.From.Parent is null ? "defaults" : command.From.Name],
            };
        }
    }

    /// <summary>
    /// Types a saved command into the tab's terminal. When it asks first and would run (Enter pressed), the prompt
    /// shows it and where it goes; typed without Enter, nothing runs until the user presses it, so it doesn't ask.
    /// </summary>
    internal async Task SendSavedCommandAsync(SessionTab tab, ResolvedCommand command, bool pressEnter)
    {
        if (!_tabs.Contains(tab) || tab.View is not TerminalView terminal)
        {
            return;
        }

        var source = CommandSourceFor(tab);
        var text = source.Expand(command.Command.Text);
        if (command.Confirm && pressEnter)
        {
            var owner = TopLevel.GetTopLevel(terminal) as Window ?? this;
            if (!await new ConfirmCommandWindow(command.Command.Name, text, source.Name, source.Where, pressEnter).ShowDialog<bool>(owner))
            {
                terminal.Focus();
                return;
            }
        }

        // The session may have dropped while the prompt was up.
        if (tab.Session.State != SessionState.Connected)
        {
            ShowNotice($"{source.Name} isn't connected, so \"{command.Command.Name}\" wasn't sent.");
            return;
        }

        terminal.SendCommand(text, pressEnter);
        terminal.Focus();
        CommandLog.LogInformation("Sent the saved command \"{Command}\" to \"{Name}\"{Enter}", command.Command.Name, source.Name, pressEnter ? "" : " without Enter");
    }

    /// <summary>
    /// Where a new command from a terminal can be saved: its connection alone, each folder it's in, or every
    /// connection. A terminal without a saved connection can only use the defaults.
    /// </summary>
    List<CommandPlace> CommandPlacesFor(SessionTab tab)
    {
        var places = new List<CommandPlace>();
        if (EntryFor(tab) is { } entry)
        {
            places.Add(new CommandPlace($"{entry.Name} only", entry));
            places.AddRange(entry.Ancestors().Where(f => f.Parent is not null).Select(f => new CommandPlace($"Everything in {f.Name}", f)));
        }

        places.Add(new CommandPlace("Every connection, and terminals without one", _tree.Root));
        return places;
    }

    /// <summary>Opens the command editor for a new command from the tab, starting from <paramref name="seed"/> (a selection) if given.</summary>
    async Task NewCommandAsync(SessionTab tab, TerminalView terminal, string? seed)
    {
        var owner = TopLevel.GetTopLevel(terminal) as Window ?? this;
        var window = new SavedCommandWindow(null, CommandPlacesFor(tab)) { Seed = seed };
        if (await window.ShowDialog<SavedCommand?>(owner) is not { } saved || window.ChosenPlace is not { } place || _tree.Find(place.Node.Id) is null)
        {
            return;
        }

        (place.Node.Commands ??= []).Add(saved);
        _tree.NotifyChanged();
        _properties.CommandsChanged(place.Node);
        string where = place.Node.Parent is null ? "the defaults for all connections" : place.Node.Name;
        ShowNotice($"Saved \"{saved.Name}\" on {where}. It's under Commands in the session's menu and in Ctrl+K.");
        if (_tabs.Contains(tab))
        {
            terminal.Focus();
        }
    }
}
