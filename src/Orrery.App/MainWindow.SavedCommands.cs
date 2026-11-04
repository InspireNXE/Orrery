using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using Orrery.App.Connections;
using Orrery.App.Controls;
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
            if (!await new ConfirmCommandWindow(command.Command.Name, text, source.Name, source.Where, pressEnter, await PasswordLineAsync(command, source)).ShowDialog<bool>(owner))
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
        if (!pressEnter || !command.Command.AnswersPassword)
        {
            return true;
        }

        // Sent, but any prompt is left to the user.
        if (SavedCommands.WhyNoPassword(command, source.Entry) is { } why)
        {
            CommandLog.LogInformation("\"{Command}\" on \"{Name}\" won't type a password: {Why}", command.Command.Name, source.Name, why);
            Warn(tab, $"\"{command.Command.Name}\" didn't type a password on {source.Name}: {why}. Type it yourself if it asks.");
            return true;
        }

        return await AnswerPasswordAsync(tab, terminal, command.Command, source, before);
    }

    /// <summary>For the ask-first prompt: which password a command types if asked, or null when it types none here.</summary>
    async Task<string?> PasswordLineAsync(ResolvedCommand command, CommandSource source)
    {
        if (SavedCommands.WhyNoPassword(command, source.Entry) is not null)
        {
            return null;
        }

        if (command.Command.Password == CommandPassword.Login)
        {
            return $"If it asks for a password, Orrery types {source.Name}'s login password.";
        }

        // The vault isn't unlocked just to name it: the prompt says which credential once it can.
        var name = !_credentials.IsLocked && command.Command.PasswordCredentialId is { } id
            ? (await _credentials.ListAsync()).FirstOrDefault(c => c.Id == id)?.Name
            : null;
        return name is null
            ? "If it asks for a password, Orrery types a saved credential's password."
            : $"If it asks for a password, Orrery types the password of \"{name}\".";
    }

    /// <summary>How long a command has to bring up its password prompt, and the shell to come back after it's answered.</summary>
    internal static TimeSpan PasswordWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a command sent when connected waits for the shell's prompt before it's typed anyway.</summary>
    internal static TimeSpan ShellWait { get; set; } = TimeSpan.FromSeconds(15);

    bool Live(SessionTab tab) => _tabs.Contains(tab) && tab.Session.State == SessionState.Connected;

    /// <summary>
    /// Says what a command typing a password did or didn't do in a bar over its session that stays until dismissed,
    /// and in the status line too, since the tab may not be the one showing.
    /// </summary>
    void Warn(SessionTab tab, string text, NoticeKind kind = NoticeKind.Warning)
    {
        tab.Warning.Show(text, kind);
        ShowNotice(tab, text);
    }

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
            Warn(tab, $"No password prompt came after \"{command.Name}\" on {source.Name}, so no password was typed.");
            return false;
        }

        if (await CommandPasswordAsync(tab, command, source, TopLevel.GetTopLevel(terminal) as Window ?? this) is not { } credential)
        {
            return false;
        }

        // The vault may have asked to be unlocked meanwhile: the prompt has to be the one still showing.
        var prompt = terminal.CursorLine();
        if (!Live(tab) || TerminalPrompt.Classify(prompt) != TerminalPromptKind.Password)
        {
            Warn(tab, $"The password prompt after \"{command.Name}\" on {source.Name} went away while the vault was unlocked, so no password was typed.");
            return false;
        }

        // The login password only where it's that login's: not for another user the session logged in as, nor at a
        // prompt that asks for someone else's.
        if (command.Password == CommandPassword.Login
            && SavedCommands.WhyNotLogin(credential.Username, source.Username, TerminalPrompt.PromptUser(prompt)) is { } wrongUser)
        {
            CommandLog.LogWarning("Didn't type a password after \"{Command}\" on \"{Name}\": {Why}", command.Name, source.Name, wrongUser);
            Warn(tab, $"No password was typed after \"{command.Name}\" on {source.Name}: {wrongUser}. Type it yourself if it's right.");
            return false;
        }

        bool shownBefore = terminal.ScreenText().Contains(credential.Secret, StringComparison.Ordinal);
        long typed = terminal.Received;
        terminal.SendSecret(credential.Secret);
        CommandLog.LogInformation("Answered the password prompt after \"{Command}\" on \"{Name}\" with credential \"{Credential}\"",
            command.Name, source.Name, credential.Name);

        var after = await TerminalPromptWatch.WaitAsync(terminal, PasswordWait, typed, () => !Live(tab));

        // A real password prompt doesn't show what's typed. One that did (a program printing "Password:" and reading
        // the line) has put the password on screen, in the scrollback, and in a recording if there is one.
        if (Live(tab) && !shownBefore && terminal.ScreenText().Contains(credential.Secret, StringComparison.Ordinal))
        {
            CommandLog.LogWarning("The password typed after \"{Command}\" on \"{Name}\" showed on screen: the prompt wasn't hiding it", command.Name, source.Name);
            Warn(tab, $"Security: the password from \"{credential.Name}\" showed on screen after \"{command.Name}\" on {source.Name}, so that prompt "
                + $"wasn't a real password prompt. It's in the scrollback{(tab.IsRecording ? " and in this session's recording" : "")}. Change that password.",
                NoticeKind.Error);
            return false;
        }

        if (after == TerminalPromptKind.Password)
        {
            CommandLog.LogWarning("The password for \"{Command}\" on \"{Name}\" wasn't taken; not typed again", command.Name, source.Name);
            Warn(tab, $"{source.Name} didn't take the password from \"{credential.Name}\", so it wasn't typed again. Type it yourself, or check the credential.");
            return false;
        }

        return Live(tab);
    }

    /// <summary>
    /// The credential whose password a command types: the connection's login, or the one the command names. Null, having
    /// said why, when there's none, it isn't a password, or the vault stays locked.
    /// </summary>
    async Task<Credential?> CommandPasswordAsync(SessionTab tab, SavedCommand command, CommandSource source, Window owner)
    {
        Guid? id = command.Password == CommandPassword.Credential ? command.PasswordCredentialId
            : source.Entry is { } entry ? SettingsResolver.Resolve(entry).CredentialId.Value
            : null;
        if (id is null)
        {
            Warn(tab, $"{source.Name} has no saved login whose password \"{command.Name}\" could type, so none was typed.");
            return null;
        }

        if (!await VaultUnlockWindow.EnsureUnlockedAsync(_credentials, owner,
                $"{source.Name} is asking for a password after \"{command.Name}\". Unlock the vault for Orrery to type it, or cancel to type it yourself."))
        {
            Warn(tab, $"The vault stayed locked, so no password was typed after \"{command.Name}\" on {source.Name}. Type it yourself.", NoticeKind.Info);
            return null;
        }

        if (await _credentials.GetAsync(id.Value) is not { Kind: CredentialKind.Password } credential)
        {
            Warn(tab, $"\"{command.Name}\" answers with a credential that's gone or isn't a password, so none was typed.");
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
            var shown = await TerminalPromptWatch.WaitAsync(terminal, ShellWait, stop: () => !Live(tab));
            if (shown == TerminalPromptKind.Password)
            {
                Warn(tab, $"{entry.Name} is asking for a password Orrery wasn't set to type, so the commands it sends when connected weren't sent.");
                CommandLog.LogInformation("Stopped sending \"{Name}\"'s commands at \"{Command}\": a password prompt is showing", entry.Name, command.Command.Name);
                return;
            }

            // Not one that types a password, though: typed at a prompt Orrery doesn't know (Telnet's login:), it's
            // anyone's guess what reads it and the password after it.
            if (shown != TerminalPromptKind.Shell && command.Command.AnswersPassword)
            {
                Warn(tab, $"{entry.Name} didn't show a shell prompt, so \"{command.Command.Name}\", which types a password, wasn't sent, nor the commands after it.");
                CommandLog.LogInformation("Stopped sending \"{Name}\"'s commands at \"{Command}\": no shell prompt for a command that types a password", entry.Name, command.Command.Name);
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
    /// Where a new command from a terminal starts out ticked: its connection, or for a terminal without a saved
    /// connection, the defaults (the only place it can send from). Any others can be ticked as well.
    /// </summary>
    IReadOnlyList<ConnectionNode> CommandPlacesFor(SessionTab tab) => EntryFor(tab) is { } entry ? [entry] : [_tree.Root];

    /// <summary>
    /// The side panel's Commands page and the editor it opens in the right-hand pane. A new command starts on the
    /// connection or folder selected in the tree, and Send goes to the current terminal when it has the command.
    /// </summary>
    (CommandsPanel, CommandEditorPane) CreateCommandsPage()
    {
        var panel = new CommandsPanel(_tree);
        var editor = new CommandEditorPane(_tree, _credentials, CommandSendTargetFor);
        panel.Chosen += (_, at) =>
        {
            editor.Show(at);
            RevealInspector();
        };
        panel.NewRequested += (_, _) =>
        {
            panel.Select(null);
            editor.Show(null, SelectedNode);
            RevealInspector();
        };
        editor.Changed += (_, nodes) =>
        {
            foreach (var node in nodes)
            {
                _properties.CommandsChanged(node);
            }

            panel.Select(editor.Shown?.Command);
        };

        // A command deleted or moved elsewhere (from a connection's settings, say) isn't left open to save back.
        _tree.Changed += (_, _) =>
        {
            if (editor.Shown is { } shown && shown.Copies.Any(c => c.Owner.Commands?.Contains(c.Command) != true))
            {
                editor.Clear();
            }
        };
        return (panel, editor);
    }

    /// <summary>Send in the Commands page's editor: the current tab, when it's a connected terminal that has the command.</summary>
    CommandSendTarget? CommandSendTargetFor(CommandAt at)
    {
        if (_currentTab is not { View: TerminalView } tab || tab.Session.State != SessionState.Connected
            || CommandSourceFor(tab).Commands.FirstOrDefault(c => at.Has(c.Command)) is not { } resolved)
        {
            return null;
        }

        return new CommandSendTarget($"Send to {tab.Origin.Title}", () => SendSavedCommandAsync(tab, resolved, resolved.Command.PressEnter));
    }

    /// <summary>Opens the command editor for a new command from the tab, starting from <paramref name="seed"/> (a selection) if given.</summary>
    async Task NewCommandAsync(SessionTab tab, TerminalView terminal, string? seed)
    {
        var owner = TopLevel.GetTopLevel(terminal) as Window ?? this;
        var window = new SavedCommandWindow(null, _tree, CommandPlacesFor(tab), _credentials) { Seed = seed };
        if (await window.ShowDialog<SavedCommand?>(owner) is not { } saved)
        {
            return;
        }

        var places = window.ChosenPlaces.Where(p => p == _tree.Root || _tree.Find(p.Id) is not null).ToList();
        if (places.Count == 0)
        {
            return;
        }

        SavedCommands.Save([], saved, places);
        _tree.NotifyChanged();
        foreach (var place in places)
        {
            _properties.CommandsChanged(place);
        }

        string where = places.Count > 1 ? $"{places.Count} places" : places[0].Parent is null ? "the defaults for all connections" : places[0].Name;
        ShowNotice($"Saved \"{saved.Name}\" on {where}. It's under Commands in the session's menu and in Ctrl+K.");
        if (_tabs.Contains(tab))
        {
            terminal.Focus();
        }
    }
}
