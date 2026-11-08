using System.Text.RegularExpressions;

namespace Orrery.Core.Connections;

/// <summary>Which password a saved command types when, once sent, a password prompt shows.</summary>
public enum CommandPassword
{
    /// <summary>None: a prompt is left for the user.</summary>
    None,

    /// <summary>The password of the credential the connection logs in with, as <c>sudo</c> wants.</summary>
    Login,

    /// <summary>The password of the credential in <see cref="SavedCommand.PasswordCredentialId"/>, such as root's for <c>su -</c>.</summary>
    Credential,
}

/// <summary>
/// A command saved on a connection or folder, sent to its terminal when picked from the Ctrl+K palette or the
/// session's menu, and by itself once connected only when it says so (<see cref="WhenConnected"/>).
/// </summary>
public sealed class SavedCommand
{
    /// <summary>What it's listed as, such as "Tail the nginx error log".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// What's typed into the terminal. <c>{host}</c>, <c>{username}</c> and <c>{name}</c> are filled in from the
    /// connection when it's sent. Several lines are pasted as one, as a paste would be.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Press Enter after the text, so it runs. Off leaves it at the prompt to finish or check.</summary>
    public bool PressEnter { get; set; } = true;

    /// <summary>Ask before sending it, showing the command and where it goes.</summary>
    public bool Confirm { get; set; }

    /// <summary>
    /// Sent by itself each time a terminal of a saved connection connects, reconnects included, after the commands of
    /// the folders around it. One that asks first still asks.
    /// </summary>
    public bool WhenConnected { get; set; }

    /// <summary>
    /// The password to type if, once it's sent, the line the cursor is on is a password prompt; the prompt has to show
    /// within a few seconds, and the password is typed once, never again if it's refused. Only with Enter pressed.
    /// </summary>
    public CommandPassword Password { get; set; }

    /// <summary>The credential whose password is typed, for <see cref="CommandPassword.Credential"/>.</summary>
    public Guid? PasswordCredentialId { get; set; }

    /// <summary>
    /// Shared by the copies of one command saved on several places, so editing it edits them all; null for a command
    /// saved in one place. A version of Orrery that doesn't know it keeps the copies as separate commands.
    /// </summary>
    public Guid? LinkId { get; set; }

    /// <summary>Whether it answers a password prompt at all.</summary>
    public bool AnswersPassword => PressEnter && (Password == CommandPassword.Login || Password == CommandPassword.Credential && PasswordCredentialId is not null);

    public SavedCommand Clone() => new()
    {
        Name = Name,
        Text = Text,
        PressEnter = PressEnter,
        Confirm = Confirm,
        WhenConnected = WhenConnected,
        Password = Password,
        PasswordCredentialId = PasswordCredentialId,
        LinkId = LinkId,
    };
}

/// <summary>A command a connection can send, where it was saved, and whether sending it asks first.</summary>
public sealed record ResolvedCommand(SavedCommand Command, ConnectionNode From, bool Confirm);

/// <summary>One copy of a command and the connection, folder or defaults (the tree's top) it's saved on.</summary>
public sealed record CommandCopy(ConnectionNode Owner, SavedCommand Command);

public static partial class SavedCommands
{
    /// <summary>Longest command text kept: enough for a short script, not a file.</summary>
    public const int MaxLength = 4000;

    /// <summary>Every copy of <paramref name="command"/> (on <paramref name="owner"/>) in the tree, in tree order: itself alone unless it's linked.</summary>
    public static IReadOnlyList<CommandCopy> Copies(ConnectionFolder root, ConnectionNode owner, SavedCommand command) =>
        command.LinkId is not { } link
            ? [new CommandCopy(owner, command)]
            : [.. root.Descendants().Prepend(root).SelectMany(n => (n.Commands ?? []).Where(c => c.LinkId == link).Select(c => new CommandCopy(n, c)))];

    /// <summary>
    /// Every command in the tree, the copies of a linked one together, in the order they're first met: the defaults'
    /// first, then the tree's.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<CommandCopy>> All(ConnectionFolder root)
    {
        var sets = new List<List<CommandCopy>>();
        var linked = new Dictionary<Guid, List<CommandCopy>>();
        foreach (var node in root.Descendants().Prepend(root))
        {
            foreach (var command in node.Commands ?? [])
            {
                var copy = new CommandCopy(node, command);
                if (command.LinkId is not { } link)
                {
                    sets.Add([copy]);
                }
                else if (linked.TryGetValue(link, out var set))
                {
                    set.Add(copy);
                }
                else
                {
                    linked[link] = set = [copy];
                    sets.Add(set);
                }
            }
        }

        return sets;
    }

    /// <summary>
    /// Saves <paramref name="command"/> on each of <paramref name="places"/>, in place of <paramref name="old"/>'s copies:
    /// where one was, it's replaced where it was listed; a new place adds it at the end; a place no longer chosen
    /// loses it. Copies on more than one place share a link. Returns the copies saved, in the order of the places.
    /// </summary>
    public static IReadOnlyList<CommandCopy> Save(IReadOnlyList<CommandCopy> old, SavedCommand command, IReadOnlyList<ConnectionNode> places)
    {
        Guid? link = places.Count > 1 ? old.Select(c => c.Command.LinkId).FirstOrDefault(id => id is not null) ?? Guid.NewGuid() : null;
        Delete(old.Where(c => !places.Contains(c.Owner)));
        var saved = new List<CommandCopy>();
        foreach (var place in places.Distinct())
        {
            var copy = command.Clone();
            copy.LinkId = link;
            var list = place.Commands ??= [];
            int at = old.FirstOrDefault(c => c.Owner == place) is { } was ? list.IndexOf(was.Command) : -1;
            if (at >= 0)
            {
                list[at] = copy;
            }
            else
            {
                list.Add(copy);
            }

            saved.Add(new CommandCopy(place, copy));
        }

        return saved;
    }

    /// <summary>Takes each copy off its place, leaving no empty list behind.</summary>
    public static void Delete(IEnumerable<CommandCopy> copies)
    {
        foreach (var (owner, command) in copies.ToList())
        {
            if (owner.Commands is { } list && list.Remove(command) && list.Count == 0)
            {
                owner.Commands = null;
            }
        }
    }

    /// <summary>
    /// Why <paramref name="command"/> won't answer a password prompt on <paramref name="entry"/> (null for a local shell
    /// or a quick connect), or null when it may. Only a saved connection gets a password: a typed-in address could be
    /// any machine. And a chosen credential's password never comes from the defaults for all connections, which reach
    /// every server; one saved on a folder reaches only the connections in it.
    /// </summary>
    public static string? WhyNoPassword(ResolvedCommand command, ConnectionEntry? entry) =>
        !command.Command.AnswersPassword ? "it doesn't answer password prompts"
        : entry is null ? "this isn't a saved connection"
        : command.Command.Password == CommandPassword.Credential && command.From.Parent is null ? "a chosen credential's password isn't typed from the defaults for all connections"
        : null;

    /// <summary>
    /// The connections a command saved on <paramref name="owner"/> reaches with its password, for the editor's warning:
    /// null for a single connection, otherwise what to say about the folder or the defaults.
    /// </summary>
    public static string? PasswordReach(ConnectionNode owner) => owner switch
    {
        ConnectionEntry => null,
        ConnectionFolder { Parent: null } => "every saved connection",
        _ => $"every connection in {owner.Name}",
    };

    /// <summary>
    /// Whether a command saved on <paramref name="owner"/> reaches a Telnet or serial connection, where a password it
    /// types crosses the line unencrypted.
    /// </summary>
    public static bool ReachesUnencrypted(ConnectionNode owner) =>
        (owner is ConnectionFolder folder ? folder.Descendants().OfType<ConnectionEntry>() : [(ConnectionEntry)owner])
            .Any(e => ProtocolTraits.Of(SettingsResolver.Resolve(e).Protocol.Value) is { Kind: SessionKind.Terminal, Encryption: WireEncryption.Never });

    /// <summary>
    /// Why the login password shouldn't be typed at this prompt, or null when it may: the login credential is for
    /// another user than the session logged in as, or the prompt asks for another user's password. Either way it
    /// would be the wrong password, given to a prompt that didn't ask for it.
    /// </summary>
    public static string? WhyNotLogin(string? credentialUser, string? sessionUser, string? promptUser) =>
        credentialUser is { Length: > 0 } c && sessionUser is { Length: > 0 } s && !string.Equals(c, s, StringComparison.Ordinal)
            ? $"its login credential is for {c}, but the session logged in as {s}"
        : promptUser is { Length: > 0 } p && (sessionUser ?? credentialUser) is { Length: > 0 } u && !string.Equals(p, u, StringComparison.Ordinal)
            ? $"the prompt asks for {p}'s password, not {u}'s"
        : null;

    /// <summary>
    /// What <paramref name="entry"/> can send: its own commands, then each folder's up to the top, nearest first. A
    /// name already taken nearer hides the one further out. A guest takes its folders', not its host's: the host is
    /// another machine. Each asks first when it says to, or when Confirm every command is on for the connection.
    /// </summary>
    public static IReadOnlyList<ResolvedCommand> For(ConnectionEntry entry) =>
        Collect(entry.Ancestors().Prepend<ConnectionNode>(entry), SettingsResolver.Resolve(entry).ConfirmCommands.Value);

    /// <summary>
    /// What <paramref name="entry"/> sends by itself once connected, in order: the defaults' first, then each folder's
    /// inwards, then its own, each in the order it's listed, so a folder's set-up comes before a connection's own. A
    /// command hidden by a nearer one of the same name isn't sent, whether or not the nearer one is sent.
    /// </summary>
    public static IReadOnlyList<ResolvedCommand> WhenConnected(ConnectionEntry entry)
    {
        var nearestFirst = entry.Ancestors().Prepend<ConnectionNode>(entry).ToList();
        return [.. For(entry).Where(c => c.Command.WhenConnected).OrderByDescending(c => nearestFirst.IndexOf(c.From))];
    }

    /// <summary>
    /// What a terminal opened without a saved connection can send, such as a local shell or a quick connect: the
    /// commands on the defaults for all connections (<paramref name="root"/>), asking first as the defaults say.
    /// </summary>
    public static IReadOnlyList<ResolvedCommand> ForDefaults(ConnectionFolder root) => Collect([root], root.Settings.ConfirmCommands ?? false);

    static List<ResolvedCommand> Collect(IEnumerable<ConnectionNode> nearestFirst, bool confirmAll)
    {
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var found = new List<ResolvedCommand>();
        foreach (var node in nearestFirst)
        {
            foreach (var command in node.Commands ?? [])
            {
                if (!string.IsNullOrWhiteSpace(command.Text) && names.Add(command.Name.Trim()))
                {
                    found.Add(new ResolvedCommand(command, node, command.Confirm || confirmAll));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The text with <c>{host}</c>, <c>{username}</c> and <c>{name}</c> filled in. Anything else in braces is left as
    /// it is, so a shell's own <c>${var}</c> and <c>{a,b}</c> come through.
    /// </summary>
    public static string Expand(string text, ConnectionEntry entry, string? username) => Expand(text, entry.Host, username, entry.Name);

    /// <summary>The same, for a terminal without a saved connection: the host and name are what it was opened as.</summary>
    public static string Expand(string text, string host, string? username, string name) =>
        Placeholder().Replace(text, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "host" => host,
            "username" => username ?? string.Empty,
            "name" => name,
            _ => match.Value,
        });

    /// <summary>
    /// Whether the text looks like it deletes, stops or overwrites something, so the editor ticks Confirm for it. A
    /// hint, not a guard: plenty of harm doesn't look like this, and the box can be unticked.
    /// </summary>
    public static bool LooksDestructive(string text) => Destructive().IsMatch(text);

    // Not after $, so a shell's ${host} stays the shell's.
    [GeneratedRegex(@"(?<!\$)\{(host|username|name)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    [GeneratedRegex(
        @"\brm\s+(-\w*[rf]|--recursive|--force)|\bdd\s+.*\bof=|\bmkfs\b|\bfdisk\b|\bwipefs\b|\bshred\b|\b(shutdown|reboot|halt|poweroff)\b"
        + @"|\binit\s+[06]\b|\bkill(all)?\s+-(9|KILL)\b|\bsystemctl\s+(stop|disable|mask|restart|kill)\b|\bservice\s+\S+\s+(stop|restart)\b"
        + @"|\b(DROP|TRUNCATE)\s+(TABLE|DATABASE|SCHEMA)\b|\bDELETE\s+FROM\b|\bgit\s+(push\s+.*(-f\b|--force)|reset\s+--hard|clean\s+-\w*f)"
        + @"|\bdocker\s+(rm|rmi|system\s+prune|volume\s+rm)\b|\bkubectl\s+delete\b|\bterraform\s+(destroy|apply)\b"
        + @"|\b(Remove-Item|Stop-Computer|Restart-Computer|Format-Volume|Clear-Disk|Stop-Service)\b|>\s*/dev/sd",
        RegexOptions.IgnoreCase)]
    private static partial Regex Destructive();
}
