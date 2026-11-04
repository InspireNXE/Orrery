using System.Text.RegularExpressions;

namespace Orrery.Core.Connections;

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

    public SavedCommand Clone() => new() { Name = Name, Text = Text, PressEnter = PressEnter, Confirm = Confirm, WhenConnected = WhenConnected };
}

/// <summary>A command a connection can send, where it was saved, and whether sending it asks first.</summary>
public sealed record ResolvedCommand(SavedCommand Command, ConnectionNode From, bool Confirm);

public static partial class SavedCommands
{
    /// <summary>Longest command text kept: enough for a short script, not a file.</summary>
    public const int MaxLength = 4000;

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
