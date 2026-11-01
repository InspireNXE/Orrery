using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;

namespace Orrery.Core.Import;

/// <summary>
/// Imports an OpenSSH client config (<c>~/.ssh/config</c>). Each Host name without wildcards becomes an SSH
/// connection, with the settings OpenSSH would use for it: the first value of each keyword wins, so a
/// <c>Host *</c> block at the end gives defaults. Written from the documented file format.
/// <para>
/// The file is untrusted. Nothing in it is run: ProxyCommand, LocalCommand and KnownHostsCommand are reported
/// and left out, and Match blocks aren't evaluated. The first IdentityFile becomes the connection's Key file
/// setting, a path; the key file is never opened by the import.
/// </para>
/// <para>
/// A ProxyJump chain becomes Orrery's jump hosts: each connection names one jump host, which names the one
/// before it. A hop that is already a connection (the same machine, login and route) is reused; any other
/// becomes a connection of its own, in the same folder.
/// </para>
/// </summary>
public static class SshConfigImporter
{
    const string SourcePrefix = "sshconfig:";
    const int MaxUserLength = 256;
    const int MaxNamesListed = 5;

    static readonly ILogger Logger = Log.For("Storage");

    /// <summary>Where OpenSSH keeps the user's config.</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>A file named like an SSH config (<c>config</c>, <c>ssh_config</c>, no extension or <c>.conf</c>), for spotting a dropped file.</summary>
    public static bool LooksLikeConfig(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return name.StartsWith("ssh_config", StringComparison.OrdinalIgnoreCase)
            || extension.Length == 0
            || extension.Equals(".conf", StringComparison.OrdinalIgnoreCase);
    }

    /// <exception cref="ImportException">The file is too big, isn't text, or has more hosts than Orrery imports at once.</exception>
    public static ImportResult ImportFile(string path) => ImportFile(path, SshConfigEnvironment.For(path));

    internal static ImportResult ImportFile(string path, SshConfigEnvironment env)
    {
        var config = SshConfig.Load(path, env);
        var result = new Builder(config, env, Path.GetFileName(path)).Build();
        Logger.LogInformation("Read SSH config: {Files} file(s), {Hosts} host name(s), {Match} Match block(s) left out",
            config.FilesRead, config.Aliases.Count, config.MatchBlocks);
        return result;
    }

    /// <summary>One machine as the config describes it: where to connect, as whom.</summary>
    sealed record HostInfo(string Name, string Host, int Port, string? User, string? KeyFile, int MoreKeyFiles)
    {
        /// <summary>Two hops with the same key are the same connection.</summary>
        public string Key => $"{Host.ToLowerInvariant()}|{Port}|{User}";
    }

    sealed record Plan(string Alias, HostInfo Info, ConnectionEntry Entry, List<HostInfo> Chain);

    static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["hostname"] = "HostName", ["user"] = "User", ["port"] = "Port", ["identityfile"] = "IdentityFile", ["proxyjump"] = "ProxyJump",
        ["proxycommand"] = "ProxyCommand", ["localcommand"] = "LocalCommand", ["knownhostscommand"] = "KnownHostsCommand",
    };

    sealed class Builder(SshConfig config, SshConfigEnvironment env, string sourceName)
    {
        readonly ConnectionTree _tree = new(new ConnectionFolder { Name = sourceName });
        readonly MRemoteNgImporter.ReportLines _skipped = new();
        readonly MRemoteNgImporter.ReportLines _warnings = new();
        readonly HashSet<string> _warned = new(StringComparer.Ordinal);
        readonly Dictionary<string, (SshDirective Directive, List<string> Names)> _commands = new(StringComparer.Ordinal);
        readonly Dictionary<string, ConnectionEntry> _entries = new(StringComparer.Ordinal);
        readonly List<string> _jumpOnly = [];
        int _withManyKeys, _withForwards;

        public ImportResult Build()
        {
            foreach (var warning in config.Warnings)
            {
                _warnings.Add(warning);
            }

            var plans = new List<Plan>();
            foreach (var alias in config.Aliases)
            {
                if (Describe(alias, null, null, out var problem) is not { } info)
                {
                    _skipped.Add($"{alias}: {problem}");
                    continue;
                }

                var cfg = config.Resolve(alias);
                _withManyKeys += info.MoreKeyFiles > 0 ? 1 : 0;
                _withForwards += cfg.Forwards.Count > 0 ? 1 : 0;

                var chain = Chain(info, [info.Key], out problem);
                if (chain is null)
                {
                    Warn(null, $"{alias}: {problem} It was imported without a jump host.");
                    chain = [];
                }

                var entry = NewEntry(alias, info, SourcePrefix + sourceName + ":" + alias.ToLowerInvariant(), null);
                _entries.TryAdd(StructuralKey(info, chain), entry);
                plans.Add(new Plan(alias, info, entry, chain));
            }

            // Primary connections exist now, so a hop that is one of them is found rather than made twice.
            foreach (var plan in plans)
            {
                ConnectionEntry? previous = null;
                string route = string.Empty;
                foreach (var hop in plan.Chain)
                {
                    var key = hop.Key + "<" + route;
                    if (!_entries.TryGetValue(key, out var jump))
                    {
                        jump = NewEntry(hop.Name + " (jump host)", hop, SourcePrefix + sourceName + ":jump:" + Hash(key),
                            "Added for a ProxyJump in the SSH config.");
                        jump.Settings.JumpHostId = previous?.Id;
                        _entries[key] = jump;
                        _jumpOnly.Add(jump.Name);
                    }

                    previous = jump;
                    route = key;
                }

                if (previous is not null)
                {
                    plan.Entry.Settings.JumpHostId = previous.Id;
                }
            }

            return Finish();
        }

        ImportResult Finish()
        {
            if (config.MatchBlocks > 0)
            {
                _skipped.Add($"{config.MatchBlocks} Match block{(config.MatchBlocks == 1 ? "" : "s")}: Orrery doesn't evaluate Match rules, so what is set inside "
                    + (config.MatchBlocks == 1 ? "it was" : "them was") + " left out.");
            }

            if (config.PatternOnly.Count > 0)
            {
                var shown = config.PatternOnly.Take(MaxNamesListed).Select(Trim).ToList();
                var more = config.PatternOnly.Count > MaxNamesListed ? $" and {config.PatternOnly.Count - MaxNamesListed} more" : "";
                _skipped.Add($"Host patterns without a single name ({string.Join("; ", shown)}{more}): they only give settings to the hosts they match, so no connection was made.");
            }

            foreach (var (directive, names) in _commands.Values)
            {
                _skipped.Add($"{Keywords[directive.Keyword]} ({directive.Where}) is never run. It applies to {List(names)}.");
            }

            if (_jumpOnly.Count > 0)
            {
                _warnings.Add($"Added {_jumpOnly.Count} jump host connection{(_jumpOnly.Count == 1 ? "" : "s")} that ProxyJump uses and the config doesn't list as a Host: {List(_jumpOnly)}.");
            }

            if (_withManyKeys > 0)
            {
                _warnings.Add($"{_withManyKeys} connection{(_withManyKeys == 1 ? " has" : "s have")} more than one IdentityFile. Orrery keeps one key file per connection, the first.");
            }

            var unsupported = new List<string>();
            if (_withForwards > 0)
            {
                unsupported.Add($"Port forwards (LocalForward, RemoteForward, DynamicForward): {_withForwards} connection{(_withForwards == 1 ? "" : "s")}");
            }

            var entries = _tree.Root.Descendants().OfType<ConnectionEntry>().Count();
            var protocols = new Dictionary<string, int>();
            if (entries > 0)
            {
                protocols[ProtocolInfo.Label(Protocol.Ssh)] = entries;
            }

            var report = new ImportReport(sourceName, 0, entries, 0, 0, protocols, _skipped.ToList(), unsupported, _warnings.ToList(), From: "SSH config");
            return new ImportResult(_tree.Root, [], report);
        }

        // ---- Describing a host ----

        /// <summary>What the config makes of <paramref name="name"/>; null, with <paramref name="problem"/>, when it can't be a connection.</summary>
        HostInfo? Describe(string name, string? user, int? port, out string problem)
        {
            problem = string.Empty;
            var cfg = config.Resolve(name);

            var host = name;
            if (cfg.HostName is { First: { } rawHost })
            {
                host = Expand(rawHost, cfg.HostName, new() { ['h'] = name });
            }

            if (!IsUsableHost(host))
            {
                problem = cfg.HostName is null ? "the name isn't a usable host name" : "HostName isn't a usable host name";
                return null;
            }

            int resolvedPort = port ?? PortOf(cfg.Port) ?? 22;
            var resolvedUser = user ?? UserOf(cfg.User);
            Track(cfg.ProxyCommand, name);
            Track(cfg.LocalCommand, name);
            Track(cfg.KnownHostsCommand, name);

            // The first IdentityFile is the key file; Orrery keeps one per connection.
            string? keyFile = null;
            int moreKeyFiles = 0;
            foreach (var directive in cfg.IdentityFiles)
            {
                if (directive.First is not { Length: > 0 } file || file.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (keyFile is not null)
                {
                    moreKeyFiles++;
                    continue;
                }

                var path = Expand(file, directive, new()
                {
                    ['d'] = env.Home, ['h'] = host, ['n'] = name, ['p'] = resolvedPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ['r'] = resolvedUser ?? env.LocalUser, ['u'] = env.LocalUser,
                });
                path = ExpandHome(path);
                if (path.Contains("${", StringComparison.Ordinal))
                {
                    Warn(directive, $"{directive.Where}: IdentityFile uses an environment variable, which Orrery doesn't expand, so it was left as written.");
                }

                keyFile = path;
            }

            return new HostInfo(name, host, resolvedPort, resolvedUser, keyFile, moreKeyFiles);
        }

        int? PortOf(SshDirective? directive)
        {
            if (directive?.First is not { } text)
            {
                return null;
            }

            if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
            {
                return port;
            }

            Warn(directive, $"{directive.Where}: the port isn't a number from 1 to 65535, so 22 is used.");
            return null;
        }

        string? UserOf(SshDirective? directive)
        {
            if (directive?.First is not { Length: > 0 } user)
            {
                return null;
            }

            if (user.Length > MaxUserLength)
            {
                Warn(directive, $"{directive.Where}: the user name is longer than {MaxUserLength} characters, so it was left out.");
                return null;
            }

            return user;
        }

        /// <summary>Keeps a list of names short in a report line.</summary>
        static string List(IReadOnlyList<string> names) =>
            string.Join(", ", names.Take(MaxNamesListed).Select(Trim)) + (names.Count > MaxNamesListed ? $" and {names.Count - MaxNamesListed} more" : "");

        static string Trim(string text) => text.Length <= 60 ? text : text[..60] + "…";

        void Track(SshDirective? directive, string name)
        {
            if (directive is not { First: { } value } || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var key = directive.Where + "/" + directive.Keyword;
            if (!_commands.TryGetValue(key, out var seen))
            {
                _commands[key] = seen = (directive, []);
            }

            if (!seen.Names.Contains(name))
            {
                seen.Names.Add(name);
            }
        }

        void Warn(SshDirective? directive, string text)
        {
            if (_warned.Add((directive?.Where ?? string.Empty) + "/" + text))
            {
                _warnings.Add(text);
            }
        }

        // ---- Tokens and paths ----

        /// <summary>Replaces %% and the tokens in <paramref name="known"/>; any other is left as written and noted.</summary>
        string Expand(string value, SshDirective directive, Dictionary<char, string> known)
        {
            if (!value.Contains('%'))
            {
                return value;
            }

            var sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '%' || i + 1 == value.Length)
                {
                    sb.Append(value[i]);
                    continue;
                }

                char token = value[++i];
                if (token == '%')
                {
                    sb.Append('%');
                }
                else if (known.TryGetValue(token, out var replacement))
                {
                    sb.Append(replacement);
                }
                else
                {
                    sb.Append('%').Append(token);
                    Warn(directive, $"{directive.Where}: {Keywords.GetValueOrDefault(directive.Keyword, directive.Keyword)} uses %{token}, which Orrery doesn't expand, so it was left as written.");
                }
            }

            return sb.ToString();
        }

        /// <summary>A leading ~ becomes the home folder. ~name (another user's) is left alone.</summary>
        string ExpandHome(string path)
        {
            if (env.Home.Length == 0 || !(path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)))
            {
                return path;
            }

            try
            {
                return Path.GetFullPath(Path.Combine(env.Home, path.Length > 2 ? path[2..] : string.Empty));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        static bool IsUsableHost(string host) =>
            host.Length is > 0 and <= SshConfig.MaxNameLength
            && host[0] != '-'
            && host.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && "/\\@\"'`;|&$<>(){}".IndexOf(c) < 0
                // Format characters such as right-to-left overrides could make a name read as another.
                && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format);

        // ---- ProxyJump ----

        /// <summary>
        /// The hops to go through to reach <paramref name="info"/>, outermost first. A first hop's own ProxyJump
        /// comes before it, so the list is the whole route; each hop's jump host is the one before it. Null, with
        /// <paramref name="problem"/>, when the route can't be built.
        /// </summary>
        List<HostInfo>? Chain(HostInfo info, HashSet<string> visiting, out string problem)
        {
            problem = string.Empty;
            var jump = config.Resolve(info.Name).ProxyJump;
            if (jump?.First is not { } text || text.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            var hops = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (hops.Length == 0)
            {
                return [];
            }

            if (hops.Length > SettingsResolver.MaxJumpHosts)
            {
                problem = $"ProxyJump ({jump.Where}) lists more than {SettingsResolver.MaxJumpHosts} jump hosts, which is as many as Orrery follows.";
                return null;
            }

            var route = new List<HostInfo>();
            for (int i = 0; i < hops.Length; i++)
            {
                if (!TryParseHop(hops[i], out var name, out var user, out var port))
                {
                    problem = $"ProxyJump ({jump.Where}) has a jump host Orrery can't read.";
                    return null;
                }

                if (Describe(name, user, port, out var hopProblem) is not { } hop)
                {
                    problem = $"ProxyJump ({jump.Where}) has a jump host that isn't usable: {hopProblem}.";
                    return null;
                }

                if (!visiting.Add(hop.Key))
                {
                    problem = $"ProxyJump ({jump.Where}) leads back to \"{Trim(name)}\", so the jump hosts loop.";
                    return null;
                }

                try
                {
                    // Only the first hop is reached by its own config's route; the others by the hop before.
                    if (i == 0)
                    {
                        if (Chain(hop, visiting, out problem) is not { } inner)
                        {
                            return null;
                        }

                        route.AddRange(inner);
                    }
                }
                finally
                {
                    visiting.Remove(hop.Key);
                }

                route.Add(hop);
            }

            if (route.Count > SettingsResolver.MaxJumpHosts)
            {
                problem = $"ProxyJump ({jump.Where}) goes through more than {SettingsResolver.MaxJumpHosts} jump hosts in all, which is as many as Orrery follows.";
                return null;
            }

            return route;
        }

        /// <summary>Reads <c>[ssh://][user@]host[:port]</c>.</summary>
        static bool TryParseHop(string text, out string host, out string? user, out int? port)
        {
            host = string.Empty;
            user = null;
            port = null;
            if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                text = text[6..];
            }

            text = text.TrimEnd('/');
            int at = text.LastIndexOf('@');
            if (at >= 0)
            {
                user = text[..at];
                text = text[(at + 1)..];
                if (user.Length is 0 or > MaxUserLength || user.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                {
                    return false;
                }
            }

            if (!HostAddress.TryParse(text, 0, out host, out var parsed))
            {
                return false;
            }

            port = parsed == 0 ? null : parsed;
            return true;
        }

        /// <summary>Names a machine together with the route to it: the same machine by another route is another connection.</summary>
        static string StructuralKey(HostInfo info, List<HostInfo> chain)
        {
            string route = string.Empty;
            foreach (var hop in chain)
            {
                route = hop.Key + "<" + route;
            }

            return info.Key + "<" + route;
        }

        // ---- Making connections ----

        ConnectionEntry NewEntry(string name, HostInfo info, string sourceId, string? note)
        {
            var entry = new ConnectionEntry
            {
                Name = name,
                Host = info.Host,
                Notes = note,
                ImportSourceId = sourceId,
            };
            entry.Settings.Protocol = Protocol.Ssh;
            if (info.KeyFile is not null)
            {
                entry.Settings.KeyFile = info.KeyFile;
            }

            if (info.Port != 22)
            {
                entry.Settings.Port = info.Port;
            }

            if (info.User is not null)
            {
                entry.Settings.Username = info.User;
            }

            _tree.Add(entry, _tree.Root);
            return entry;
        }

        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }
}
