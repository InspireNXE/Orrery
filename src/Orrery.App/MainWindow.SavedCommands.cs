using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Orrery.App.Connections;
using Orrery.App.Credentials;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;
using Orrery.Core.Terminal;

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
                AltHint = command.Command.PressEnter ? "type only" : null,
                Keywords = ["send", "run", "saved", "command", command.From.Parent is null ? "defaults" : command.From.Name],
            };
        }
    }

    /// <summary>
    /// Types a saved command into the tab's terminal. When it asks first and would run (Enter pressed), the prompt
    /// shows it and where it goes; typed without Enter, nothing runs until the user presses it, so it doesn't ask.
    /// </summary>
    /// <param name="focus">Put the focus in the terminal after; off for those sent by themselves, which may be in a tab not shown.</param>
    internal async Task<bool> SendSavedCommandAsync(SessionTab tab, ResolvedCommand command, bool pressEnter, bool focus = true)
    {
        if (!_tabs.Contains(tab) || tab.View is not TerminalView terminal)
        {
            return false;
        }

        var source = CommandSourceFor(tab);
        var text = source.Expand(command.Command.Text);
        if (command.Confirm && pressEnter)
        {
            var owner = TopLevel.GetTopLevel(terminal) as Window ?? this;
            if (!await new ConfirmCommandWindow(command.Command.Name, text, source.Name, source.Where, pressEnter).ShowDialog<bool>(owner))
            {
                if (focus)
                {
                    terminal.Focus();
                }

                return false;
            }
        }

        // The session may have dropped while the prompt was up.
        if (tab.Session.State != SessionState.Connected)
        {
            ShowNotice($"{source.Name} isn't connected, so \"{command.Command.Name}\" wasn't sent.");
            return false;
        }

        long before = terminal.Received;
        terminal.SendCommand(text, pressEnter);
        if (focus)
        {
            terminal.Focus();
        }

        CommandLog.LogInformation("Sent the saved command \"{Command}\" to \"{Name}\"{Enter}", command.Command.Name, source.Name, pressEnter ? "" : " without Enter");
        return !(pressEnter && command.Command.AnswersPassword) || await AnswerPasswordAsync(tab, terminal, command.Command, source, before);
    }

    /// <summary>How long a command has to bring up its password prompt, and the shell to come back after it's answered.</summary>
    internal static TimeSpan PasswordWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a command sent when connected waits for the shell's prompt before it's typed anyway.</summary>
    internal static TimeSpan ShellWait { get; set; } = TimeSpan.FromSeconds(15);

    bool Live(SessionTab tab) => _tabs.Contains(tab) && tab.Session.State == SessionState.Connected;

    /// <summary>
    /// After a command that answers a password prompt: types the password once, if a password prompt is what shows.
    /// A shell prompt instead (sudo didn't ask) is fine; no prompt at all, a password that can't be had, or the prompt
    /// coming back after the answer stops there, with nothing more typed. True when it's safe to carry on.
    /// </summary>
    async Task<bool> AnswerPasswordAsync(SessionTab tab, TerminalView terminal, SavedCommand command, CommandSource source, long before)
    {
        var shown = await TerminalPromptWatch.WaitAsync(terminal, PasswordWait, before, () => !Live(tab));
        if (!Live(tab))
        {
            return false;
        }

        if (shown == TerminalPromptKind.Shell)
        {
            CommandLog.LogInformation("\"{Command}\" on \"{Name}\" didn't ask for a password, so none was typed", command.Name, source.Name);
            return true;
        }

        if (shown == TerminalPromptKind.None)
        {
            CommandLog.LogWarning("No password prompt came after \"{Command}\" on \"{Name}\", so none was typed", command.Name, source.Name);
            ShowNotice($"No password prompt came after \"{command.Name}\" on {source.Name}, so none was typed.");
            return false;
        }

        if (await CommandPasswordAsync(command, source, TopLevel.GetTopLevel(terminal) as Window ?? this) is not { } credential)
        {
            return false;
        }

        // The vault may have asked to be unlocked meanwhile: the prompt has to be the one still showing.
        if (!Live(tab) || TerminalPrompt.Classify(terminal.CursorLine()) != TerminalPromptKind.Password)
        {
            ShowNotice($"The password prompt after \"{command.Name}\" on {source.Name} went away, so no password was typed.");
            return false;
        }

        long typed = terminal.Received;
        terminal.SendSecret(credential.Secret);
        CommandLog.LogInformation("Answered the password prompt after \"{Command}\" on \"{Name}\" with credential \"{Credential}\"",
            command.Name, source.Name, credential.Name);

        if (await TerminalPromptWatch.WaitAsync(terminal, PasswordWait, typed, () => !Live(tab)) == TerminalPromptKind.Password)
        {
            CommandLog.LogWarning("The password for \"{Command}\" on \"{Name}\" wasn't taken; not typed again", command.Name, source.Name);
            ShowNotice($"{source.Name} didn't take the password from \"{credential.Name}\". Type it yourself, or check the credential.");
            return false;
        }

        return Live(tab);
    }

    /// <summary>
    /// The credential whose password a command types: the connection's login, or the one the command names. Null, having
    /// said why, when there's none, it isn't a password, or the vault stays locked.
    /// </summary>
    async Task<Credential?> CommandPasswordAsync(SavedCommand command, CommandSource source, Window owner)
    {
        Guid? id = command.Password == CommandPassword.Credential ? command.PasswordCredentialId
            : source.Entry is { } entry ? SettingsResolver.Resolve(entry).CredentialId.Value
            : null;
        if (id is null)
        {
            ShowNotice($"{source.Name} has no saved login whose password \"{command.Name}\" could type, so none was typed.");
            return null;
        }

        if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, owner))
        {
            return null;
        }

        if (await _credentials.GetAsync(id.Value) is not { Kind: CredentialKind.Password } credential)
        {
            ShowNotice($"\"{command.Name}\" answers with a credential that's gone or isn't a password, so none was typed.");
            return null;
        }

        return credential;
    }

    /// <summary>
    /// Sends the commands a saved connection's terminal sends by itself once connected, in order. One that asks first
    /// asks; turning it down, or the session dropping, stops the rest, since a later one may count on it. A local
    /// shell or a quick connect sends none.
    /// </summary>
    async Task SendWhenConnectedAsync(SessionTab tab)
    {
        if (tab.View is not TerminalView terminal || EntryFor(tab) is not { } entry || SavedCommands.WhenConnected(entry) is not { Count: > 0 } commands)
        {
            return;
        }

        CommandLog.LogInformation("Sending {Count} saved command(s) to \"{Name}\" now that it's connected", commands.Count, entry.Name);
        foreach (var command in commands)
        {
            // Each waits for the shell, so it isn't read by the one before (sudo taking it for the password). One whose
            // prompt doesn't come in time (one Orrery doesn't know) is typed anyway, as the shell reads ahead; a password
            // prompt nobody answered stops them.
            if (await TerminalPromptWatch.WaitAsync(terminal, ShellWait, stop: () => !Live(tab)) == TerminalPromptKind.Password)
            {
                ShowNotice($"{entry.Name} is asking for a password, so its commands weren't sent.");
                CommandLog.LogInformation("Stopped sending \"{Name}\"'s commands at \"{Command}\": a password prompt is showing", entry.Name, command.Command.Name);
                return;
            }

            if (!await SendSavedCommandAsync(tab, command, command.Command.PressEnter, focus: false))
            {
                CommandLog.LogInformation("Stopped sending \"{Name}\"'s commands at \"{Command}\"", entry.Name, command.Command.Name);
                return;
            }
        }
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
        var window = new SavedCommandWindow(null, CommandPlacesFor(tab), _credentials) { Seed = seed };
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
