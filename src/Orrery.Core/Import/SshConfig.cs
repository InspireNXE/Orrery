using System.Text;

namespace Orrery.Core.Import;

/// <summary>Where an SSH config looks for things: the home folder, the local user and what relative Includes start from.</summary>
internal sealed record SshConfigEnvironment(string Home, string LocalUser, string IncludeBase)
{
    /// <summary>
    /// This user's. OpenSSH reads relative Includes from ~/.ssh; a file picked from somewhere else (a backup, a
    /// copied-out folder) uses its own folder instead, so its neighbors are found.
    /// </summary>
    public static SshConfigEnvironment For(string configPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var user = Environment.UserName;
        var folder = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? string.Empty;
        if (home.Length == 0)
        {
            return new SshConfigEnvironment(string.Empty, user, folder);
        }

        var sshDir = Path.Combine(home, ".ssh");
        return new SshConfigEnvironment(home, user, IsInside(sshDir, folder) ? sshDir : folder);
    }

    static bool IsInside(string parent, string path)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return path.Equals(parent, comparison)
            || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
    }
}

/// <summary>One line of a config: its keyword (lower case), arguments, and where it was.</summary>
internal sealed record SshDirective(string Keyword, IReadOnlyList<string> Args, string File, int Line)
{
    public string Where => $"{File}, line {Line}";

    public string? First => Args.Count > 0 ? Args[0] : null;
}

/// <summary>What the config says for one name, the way OpenSSH reads it: the first value of each keyword wins.</summary>
internal sealed class SshHostSettings
{
    public SshDirective? HostName, User, Port, ProxyJump, ProxyCommand, LocalCommand, KnownHostsCommand;

    /// <summary>The first LocalForward, RemoteForward and DynamicForward, if any.</summary>
    public List<SshDirective> Forwards { get; } = [];

    /// <summary>Every IdentityFile that applies; unlike the others, they add up.</summary>
    public List<SshDirective> IdentityFiles { get; } = [];
}

/// <summary>
/// A parsed OpenSSH client config: its Host blocks in file order, with Includes followed. Nothing is run and no
/// file but the config and what it includes is opened. The file is untrusted, so sizes, depth and counts are
/// capped, and warnings never repeat what a line said.
/// </summary>
internal sealed class SshConfig
{
    public const long MaxFileBytes = 2L * 1024 * 1024;
    public const long MaxTotalBytes = 8L * 1024 * 1024;
    public const int MaxFiles = 200;
    public const int MaxIncludeDepth = 16;
    public const int MaxLineLength = 8 * 1024;
    public const int MaxBlocks = 5_000;
    public const int MaxPatterns = 10_000;
    public const int MaxDirectives = 100_000;
    public const int MaxHosts = 1_000;
    public const int MaxNameLength = 255;

    // Folder entries looked at while expanding one wildcard in an Include.
    const int MaxGlobEntries = 10_000;
    const int MaxResolves = 20_000;
    const int MaxIdentityFilesPerBlock = 16;

    // The keywords the import uses; everything else is ignored as soon as it's read.
    static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "hostname", "user", "port", "identityfile", "proxyjump", "proxycommand", "localcommand", "knownhostscommand",
        "localforward", "remoteforward", "dynamicforward",
    };

    sealed class Block
    {
        public bool IsMatch;
        public List<(string Pattern, bool Negated)> Patterns = [];
        public Dictionary<string, SshDirective> First = new(StringComparer.Ordinal);
        public List<SshDirective> IdentityFiles = [];

        public bool Matches(string name)
        {
            bool found = false;
            foreach (var (pattern, negated) in Patterns)
            {
                if (!GlobMatch(pattern, name))
                {
                    continue;
                }

                if (negated)
                {
                    return false;
                }

                found = true;
            }

            return found;
        }
    }

    readonly SshConfigEnvironment _env;
    readonly List<Block> _blocks = [new Block { Patterns = [("*", false)] }];
    readonly Dictionary<string, SshHostSettings> _resolved = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _aliasSet = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _warnings = [];
    readonly List<string> _stack = [];
    long _totalBytes;
    int _patterns, _directives;

    SshConfig(SshConfigEnvironment env) => _env = env;

    /// <summary>Concrete host names (no wildcards) that a Host line matches, in file order.</summary>
    public List<string> Aliases { get; } = [];

    /// <summary>Host lines made only of patterns, which give defaults but aren't connections.</summary>
    public List<string> PatternOnly { get; } = [];

    public int MatchBlocks { get; private set; }

    public int FilesRead { get; private set; }

    /// <summary>Problems reading the file itself, in plain words and without quoting any line.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    public static SshConfig Load(string path, SshConfigEnvironment env)
    {
        var config = new SshConfig(env) { FilesRead = 1 };
        config.ReadFile(path, 0, config._blocks[0]);
        return config;
    }

    public SshHostSettings Resolve(string name)
    {
        if (_resolved.TryGetValue(name, out var cached))
        {
            return cached;
        }

        if (_resolved.Count >= MaxResolves)
        {
            throw new ImportException("This config names more hosts, through ProxyJump, than Orrery imports at once.");
        }

        var settings = new SshHostSettings();
        foreach (var block in _blocks)
        {
            if (block.IsMatch || !block.Matches(name))
            {
                continue;
            }

            var first = block.First;
            settings.HostName ??= first.GetValueOrDefault("hostname");
            settings.User ??= first.GetValueOrDefault("user");
            settings.Port ??= first.GetValueOrDefault("port");
            settings.ProxyJump ??= first.GetValueOrDefault("proxyjump");
            settings.ProxyCommand ??= first.GetValueOrDefault("proxycommand");
            settings.LocalCommand ??= first.GetValueOrDefault("localcommand");
            settings.KnownHostsCommand ??= first.GetValueOrDefault("knownhostscommand");
            foreach (var keyword in new[] { "localforward", "remoteforward", "dynamicforward" })
            {
                if (first.TryGetValue(keyword, out var forward) && !settings.Forwards.Any(f => f.Keyword == keyword))
                {
                    settings.Forwards.Add(forward);
                }
            }

            settings.IdentityFiles.AddRange(block.IdentityFiles);
        }

        _resolved[name] = settings;
        return settings;
    }

    // ---- Reading ----

    void ReadFile(string path, int depth, Block current)
    {
        var text = ReadText(path, depth);
        if (text is null)
        {
            return;
        }

        var fileName = Path.GetFileName(path);
        // The files being read right now, so an Include that leads back to one of them is caught by name.
        _stack.Add(Path.GetFullPath(path));
        try
        {
            ReadLines(text, depth, fileName, current);
        }
        finally
        {
            _stack.RemoveAt(_stack.Count - 1);
        }
    }

    void ReadLines(string text, int depth, string fileName, Block block)
    {
        int lineNumber = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var trimmed = line.AsSpan().Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            if (trimmed.Length > MaxLineLength)
            {
                Warn($"{fileName}, line {lineNumber}: the line is longer than Orrery reads, so it was skipped.");
                continue;
            }

            if (!TrySplit(trimmed.ToString(), out var keyword, out var args))
            {
                Warn($"{fileName}, line {lineNumber}: couldn't be read (an unclosed quote), so it was skipped.");
                continue;
            }

            switch (keyword)
            {
                case "host":
                    block = StartHostBlock(args, fileName, lineNumber);
                    break;
                case "match":
                    MatchBlocks++;
                    block = AddBlock(new Block { IsMatch = true });
                    break;
                case "include":
                    foreach (var pattern in args)
                    {
                        Include(pattern, depth, block, fileName, lineNumber);
                    }

                    break;
                default:
                    if (!block.IsMatch && Known.Contains(keyword) && args.Count > 0)
                    {
                        Record(block, new SshDirective(keyword, args, fileName, lineNumber));
                    }

                    break;
            }
        }
    }

    void Record(Block block, SshDirective directive)
    {
        if (++_directives > MaxDirectives)
        {
            throw new ImportException($"This config has more than {MaxDirectives:N0} settings Orrery uses, more than it imports at once.");
        }

        if (directive.Keyword == "identityfile")
        {
            if (block.IdentityFiles.Count < MaxIdentityFilesPerBlock)
            {
                block.IdentityFiles.Add(directive);
            }
        }
        else
        {
            block.First.TryAdd(directive.Keyword, directive);
        }
    }

    Block AddBlock(Block block)
    {
        if (_blocks.Count > MaxBlocks)
        {
            throw new ImportException($"This config has more than {MaxBlocks:N0} Host and Match blocks, more than Orrery imports at once.");
        }

        _blocks.Add(block);
        return block;
    }

    Block StartHostBlock(IReadOnlyList<string> args, string fileName, int lineNumber)
    {
        var block = new Block();
        foreach (var arg in args)
        {
            bool negated = arg.StartsWith('!');
            var pattern = negated ? arg[1..] : arg;
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern.Length > MaxNameLength)
            {
                Warn($"{fileName}, line {lineNumber}: a host name longer than {MaxNameLength} characters was ignored.");
                continue;
            }

            if (++_patterns > MaxPatterns)
            {
                throw new ImportException($"This config lists more than {MaxPatterns:N0} host patterns, more than Orrery imports at once.");
            }

            block.Patterns.Add((pattern, negated));
        }

        if (block.Patterns.Count == 0)
        {
            Warn($"{fileName}, line {lineNumber}: a Host line with no names, so its settings apply to nothing.");
        }

        AddBlock(block);

        var concrete = block.Patterns.Where(p => !p.Negated && !HasWildcard(p.Pattern)).Select(p => p.Pattern).ToList();
        if (concrete.Count == 0)
        {
            if (block.Patterns.Any(p => !p.Negated))
            {
                PatternOnly.Add(string.Join(' ', block.Patterns.Where(p => !p.Negated).Select(p => p.Pattern)));
            }

            return block;
        }

        foreach (var alias in concrete)
        {
            // "Host web !web" matches nothing; only names the line really matches become connections.
            if (block.Matches(alias) && _aliasSet.Add(alias))
            {
                if (Aliases.Count >= MaxHosts)
                {
                    throw new ImportException($"This config names more than {MaxHosts:N0} hosts, more than Orrery imports at once.");
                }

                Aliases.Add(alias);
            }
        }

        return block;
    }

    static bool HasWildcard(string pattern) => pattern.AsSpan().IndexOfAny('*', '?') >= 0;

    void Warn(string text)
    {
        if (_warnings.Count < ImportReport.MaxListedLines)
        {
            _warnings.Add(text);
        }
    }

    // ---- Include ----

    void Include(string pattern, int depth, Block block, string fileName, int lineNumber)
    {
        string where = $"{fileName}, line {lineNumber}";
        if (depth + 1 > MaxIncludeDepth)
        {
            Warn($"{where}: Include is nested more than {MaxIncludeDepth} files deep, so it was not followed.");
            return;
        }

        if (pattern.Contains("${", StringComparison.Ordinal))
        {
            Warn($"{where}: an Include that uses an environment variable wasn't followed.");
            return;
        }

        var expanded = ExpandIncludePath(pattern, out bool unsupported);
        if (unsupported)
        {
            Warn($"{where}: an Include that uses a path form Orrery doesn't expand wasn't followed.");
            return;
        }

        var files = Glob(expanded);
        if (files.Count == 0)
        {
            Warn($"{where}: an Include matched no files.");
            return;
        }

        foreach (var file in files)
        {
            var full = Path.GetFullPath(file);
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (_stack.Any(s => s.Equals(full, comparison)))
            {
                Warn($"{where}: Include of {Path.GetFileName(full)} loops back to a file already being read, so it was skipped.");
                continue;
            }

            if (++FilesRead > MaxFiles)
            {
                throw new ImportException($"This config includes more than {MaxFiles} files, more than Orrery imports at once.");
            }

            ReadFile(full, depth + 1, block);
        }
    }

    string ExpandIncludePath(string pattern, out bool unsupported)
    {
        unsupported = false;
        var sb = new StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] != '%' || i + 1 == pattern.Length)
            {
                sb.Append(pattern[i]);
                continue;
            }

            switch (pattern[++i])
            {
                case '%': sb.Append('%'); break;
                case 'd': sb.Append(_env.Home); break;
                case 'u': sb.Append(_env.LocalUser); break;
                default:
                    unsupported = true;
                    break;
            }
        }

        var path = sb.ToString();
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = _env.Home + path[1..];
        }
        else if (path.StartsWith('~'))
        {
            // ~someone: another user's home isn't looked up.
            unsupported = true;
        }

        return Path.IsPathRooted(path) ? path : Path.Combine(_env.IncludeBase, path);
    }

    /// <summary>The regular files a path with * and ? in it names, in name order; a path without any is itself.</summary>
    static List<string> Glob(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var parts = path[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var candidates = new List<string> { root.Length > 0 ? root : "." };
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            var next = new List<string>();
            foreach (var folder in candidates)
            {
                if (!HasWildcard(parts[i]))
                {
                    next.Add(Path.Combine(folder, parts[i]));
                    continue;
                }

                try
                {
                    int looked = 0;
                    foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                    {
                        if (++looked > MaxGlobEntries)
                        {
                            break;
                        }

                        // Like glob(3), * doesn't match a leading dot.
                        if ((last || entry is DirectoryInfo) && GlobMatch(parts[i], entry.Name, caseSensitive: OperatingSystem.IsLinux())
                            && (!entry.Name.StartsWith('.') || parts[i].StartsWith('.')))
                        {
                            next.Add(entry.FullName);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }

            candidates = next.Take(MaxGlobEntries).ToList();
        }

        return candidates.Where(IsRegularFile).Order(StringComparer.Ordinal).ToList();
    }

    static bool IsRegularFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            // Reading these would not end (or would block): they're devices, not files.
            if (!OperatingSystem.IsWindows())
            {
                var full = Path.GetFullPath(path);
                return !(full.StartsWith("/dev/", StringComparison.Ordinal) || full.StartsWith("/proc/", StringComparison.Ordinal)
                    || full.StartsWith("/sys/", StringComparison.Ordinal));
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // ---- Files ----

    /// <summary>The file's text, or null (with a warning) when an included file can't be used. The first file is held to a stricter standard.</summary>
    string? ReadText(string path, int depth)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxFileBytes + 1 - buffer.Length))) > 0)
            {
                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (depth == 0)
            {
                throw;
            }

            Warn($"{Path.GetFileName(path)} couldn't be read ({e.GetType().Name}), so it was skipped.");
            return null;
        }

        if (bytes.Length > MaxFileBytes)
        {
            return Refuse(depth, path, $"{Path.GetFileName(path)} is larger than {MaxFileBytes / (1024 * 1024)} MB, more than Orrery reads from an SSH config.");
        }

        _totalBytes += bytes.Length;
        if (_totalBytes > MaxTotalBytes)
        {
            throw new ImportException($"This config and the files it includes add up to more than {MaxTotalBytes / (1024 * 1024)} MB, more than Orrery reads at once.");
        }

        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else
        {
            if (Array.IndexOf(bytes, (byte)0) >= 0)
            {
                return Refuse(depth, path, $"{Path.GetFileName(path)} doesn't look like a text file.");
            }

            text = new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
        }

        return text;
    }

    string? Refuse(int depth, string path, string message)
    {
        if (depth == 0)
        {
            throw new ImportException(message);
        }

        Warn(message + " It was skipped.");
        return null;
    }

    // ---- Line syntax ----

    /// <summary>
    /// Splits "Keyword value value", "Keyword=value" or "Keyword = value" as OpenSSH does: double quotes group,
    /// a backslash escapes a quote or backslash inside them, and a # outside quotes starts a comment.
    /// </summary>
    internal static bool TrySplit(string line, out string keyword, out List<string> args)
    {
        args = [];
        int i = 0;
        while (i < line.Length && line[i] is not (' ' or '\t' or '='))
        {
            i++;
        }

        keyword = line[..i].ToLowerInvariant();
        while (i < line.Length && line[i] is ' ' or '\t')
        {
            i++;
        }

        if (i < line.Length && line[i] == '=')
        {
            i++;
        }

        var current = new StringBuilder();
        bool inToken = false, quoted = false;
        for (; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\')
                {
                    current.Append(line[++i]);
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is ' ' or '\t')
            {
                if (inToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else if (c == '#' && !inToken)
            {
                break;
            }
            else if (c == '"')
            {
                quoted = inToken = true;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (quoted)
        {
            return false;
        }

        if (inToken)
        {
            args.Add(current.ToString());
        }

        return keyword.Length > 0;
    }

    /// <summary>OpenSSH's pattern match: * is any run of characters, ? is one. Case doesn't matter for host names.</summary>
    internal static bool GlobMatch(string pattern, string text, bool caseSensitive = false)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || string.Compare(pattern, p, text, t, 1, comparison) == 0))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
