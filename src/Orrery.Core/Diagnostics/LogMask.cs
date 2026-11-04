using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Orrery.Core.Storage;

namespace Orrery.Core.Diagnostics;

/// <summary>What a value in a log line is, which decides how it's hidden and what its tag starts with.</summary>
public enum LogMaskKind
{
    /// <summary>A host name or address: host-3f9a2c, or ip-81d0e4 for an IP address. Loopback and broadcast addresses stay.</summary>
    Host,

    /// <summary>A login name: user-19c2aa.</summary>
    User,

    /// <summary>A name someone gave: a connection, credential, saved command or tab title. name-7b01f3.</summary>
    Name,

    /// <summary>A file or folder: path-0c4e1d.orrec, keeping the extension. Files in the data folder show as &lt;data&gt;\...</summary>
    Path,

    /// <summary>A web address: the scheme and port stay, the host is hidden as a host is and anything after it is dropped.</summary>
    Web,

    /// <summary>A routing token or cookie a server handed out: token-5a5e90.</summary>
    Token,

    /// <summary>A network card's hardware address: mac-2b77c1.</summary>
    Mac,
}

/// <summary>A value Orrery has hidden in the log, and the tag it shows as.</summary>
public sealed record LogMaskMatch(LogMaskKind Kind, string Value, string Tag);

/// <summary>
/// Hides names and addresses in log lines behind short tags, so a log can be shared (in a GitHub issue, say) without
/// saying which servers, people or files it was about. A value gets the same tag every time, so a log still shows
/// that two lines are about one host. Tags are keyed by a secret kept in the data folder, never in the log, so they
/// can't be turned back by trying values (every IPv4 address, say). <see cref="Lookup"/> turns a tag back, on this
/// computer only, for values seen since Orrery started.
/// </summary>
public sealed partial class LogMask
{
    public const string KeyFileName = "log-mask.key";
    const int KeyLength = 32;
    const int MaxRemembered = 5000;
    const int ShortestInText = 3;
    const string DataFolder = "<data>";

    readonly byte[] _key;
    readonly string? _dataDir;
    readonly string? _home;
    readonly object _gate = new();
    readonly Dictionary<string, List<LogMaskMatch>> _byTag = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _inText = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _quotedInText = new(StringComparer.OrdinalIgnoreCase);
    Finder? _finder;

    public LogMask(byte[] key, string? dataDir = null, string? home = null)
    {
        _key = key;
        _dataDir = string.IsNullOrEmpty(dataDir) ? null : System.IO.Path.TrimEndingDirectorySeparator(dataDir);
        _home = string.IsNullOrEmpty(home) ? null : System.IO.Path.TrimEndingDirectorySeparator(home);
    }

    /// <summary>
    /// The mask for <paramref name="location"/>: its key is read from the data folder, or made and saved there owner-only.
    /// A key that can't be saved still works for this run; tags then differ from one run to the next.
    /// </summary>
    public static LogMask For(DataLocation location)
    {
        var path = location.FileIn(KeyFileName);
        byte[]? key = null;
        try
        {
            key = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        if (key is not { Length: KeyLength })
        {
            key = RandomNumberGenerator.GetBytes(KeyLength);
            try
            {
                using var file = new FileStream(path, PrivateStorage.Private(new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write }));
                file.Write(key);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        var mask = new LogMask(key, location.DataDir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        mask.Learn(LogMaskKind.User, Environment.UserName);
        mask.Learn(LogMaskKind.Host, Environment.MachineName);
        return mask;
    }

    /// <summary>True for text shaped like a tag, such as host-3f9a2c or path-0c4e1d.orrec.</summary>
    public static bool IsTag(string text) => TagPattern().IsMatch(text.Trim());

    /// <summary>Remembers a value so it's hidden wherever it turns up in a line, even in a library's error message.</summary>
    public void Learn(LogMaskKind kind, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _ = Mask(kind, value);
        }
    }

    /// <summary><paramref name="value"/> as the log shows it: a tag, or the value itself when it says nothing about anyone.</summary>
    public string Mask(LogMaskKind kind, string value) => Mask(kind, value, remember: true);

    // Remembering makes a value hidden in free text from then on, and lets Lookup turn its tag back.
    string Mask(LogMaskKind kind, string value, bool remember)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return value;
        }

        return kind switch
        {
            LogMaskKind.Host => MaskHost(trimmed, remember),
            LogMaskKind.Path => MaskPath(trimmed, remember),
            LogMaskKind.Web => MaskWeb(trimmed),
            LogMaskKind.Name => Hidden(kind, trimmed, remember, quoted: true),
            _ => Hidden(kind, trimmed, remember),
        };
    }

    /// <summary>
    /// Free text, such as an error message, with what it shouldn't say taken out: values seen before (named ones only in
    /// quotes), the data and home folders, web and email addresses, IP and hardware addresses.
    /// </summary>
    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var finder = CurrentFinder();
        if (finder.Known is { } known)
        {
            text = known.Replace(text, m => finder.Tags.TryGetValue(m.Value, out var tag) ? tag : m.Value);
        }

        if (finder.Quoted is { } quoted)
        {
            text = quoted.Replace(text, m => finder.QuotedTags.TryGetValue(m.Value, out var tag) ? tag : m.Value);
        }

        if (_dataDir is not null)
        {
            text = text.Replace(_dataDir, DataFolder, StringComparison.OrdinalIgnoreCase);
        }

        if (_home is not null)
        {
            text = text.Replace(_home, "~", StringComparison.OrdinalIgnoreCase);
        }

        text = UrlPattern().Replace(text, m =>
        {
            var url = m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '\'', '"');
            return MaskWeb(url) + m.Value[url.Length..];
        });
        text = EmailPattern().Replace(text, m => $"{Unless(m.Groups["user"].Value, LogMaskKind.User)}@{Unless(m.Groups["host"].Value, LogMaskKind.Host)}");
        text = Ipv4Pattern().Replace(text, m => m.Value.Split('.').All(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture) <= 255) ? MaskHost(m.Value) : m.Value);
        text = Ipv6Pattern().Replace(text, m => IPAddress.TryParse(m.Value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 ? MaskHost(m.Value) : m.Value);
        return MacPattern().Replace(text, m => Mask(LogMaskKind.Mac, m.Value));
    }

    /// <summary>
    /// What a tag stands for, from values seen since Orrery started; or, given a value, the tags it would show as.
    /// </summary>
    public IReadOnlyList<LogMaskMatch> Lookup(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        if (TagPattern().Match(text) is { Success: true } tag)
        {
            lock (_gate)
            {
                return _byTag.TryGetValue(tag.Groups["tag"].Value, out var matches) ? [.. matches] : [];
            }
        }

        return [.. new[] { LogMaskKind.Host, LogMaskKind.User, LogMaskKind.Name, LogMaskKind.Path }
            .Select(kind => new LogMaskMatch(kind, text, Mask(kind, text, remember: false)))
            .Where(match => match.Tag != text)];
    }

    string MaskHost(string host, bool remember = true)
    {
        if (IsOpen(host) || IsTag(host))
        {
            return host;
        }

        // An address with its port: the port is worth keeping.
        if (HostAndPortPattern().Match(host) is { Success: true } withPort)
        {
            var bracketed = withPort.Groups["bracket"].Success;
            var name = bracketed ? withPort.Groups["bracket"].Value : withPort.Groups["host"].Value;
            var shown = MaskHost(name, remember);
            return (bracketed && shown == name ? $"[{shown}]" : shown) + ":" + withPort.Groups["port"].Value;
        }

        return Hidden(LogMaskKind.Host, host, remember);
    }

    string MaskPath(string path, bool remember)
    {
        if (_dataDir is not null && path.StartsWith(_dataDir, StringComparison.OrdinalIgnoreCase)
            && (path.Length == _dataDir.Length || path[_dataDir.Length] is '/' or '\\'))
        {
            return DataFolder + path[_dataDir.Length..];
        }

        var extension = System.IO.Path.GetExtension(path);
        if (extension.Length > 8 || !extension.Skip(1).All(char.IsAsciiLetterOrDigit))
        {
            extension = string.Empty;
        }

        return Hidden(LogMaskKind.Path, path, remember, suffix: extension);
    }

    string MaskWeb(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.IsFile)
        {
            return MaskHost(text);
        }

        var host = uri.IdnHost;
        if (IsOrrerysOwn(host) || IsOpen(host))
        {
            return text;
        }

        var shown = new StringBuilder(uri.Scheme).Append("://");
        if (uri.UserInfo.Length > 0)
        {
            shown.Append(Mask(LogMaskKind.User, Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]))).Append('@');
        }

        shown.Append(MaskHost(host));
        if (!uri.IsDefaultPort)
        {
            shown.Append(':').Append(uri.Port);
        }

        if (uri.PathAndQuery != "/" || uri.Fragment.Length > 0)
        {
            shown.Append("/...");
        }
        else if (text.EndsWith('/'))
        {
            shown.Append('/');
        }

        return shown.ToString();
    }

    // Part of an email address or user@host, unless an earlier step already made it a tag.
    string Unless(string part, LogMaskKind kind) => IsTag(part) ? part : Mask(kind, part);

    // A name is hidden in free text only in quotes, as Orrery's own messages give one: names are often everyday words.
    string Hidden(LogMaskKind kind, string value, bool remember, bool quoted = false, string suffix = "")
    {
        var prefix = Prefix(kind, value);
        var normalized = kind is LogMaskKind.Host ? value.ToLowerInvariant()
            : kind is LogMaskKind.Mac ? new string(value.Where(char.IsAsciiHexDigit).Select(char.ToLowerInvariant).ToArray())
            : value;
        var hash = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(prefix + "\n" + normalized));
        var tag = prefix + "-" + Convert.ToHexStringLower(hash, 0, 3);
        var shown = tag + suffix;
        if (!remember)
        {
            return shown;
        }

        lock (_gate)
        {
            if (_byTag.Count < MaxRemembered || _byTag.ContainsKey(tag))
            {
                var matches = _byTag.TryGetValue(tag, out var list) ? list : _byTag[tag] = [];
                if (!matches.Any(m => m.Kind == kind && string.Equals(m.Value, value, StringComparison.OrdinalIgnoreCase)))
                {
                    matches.Add(new LogMaskMatch(kind, value, shown));
                }
            }

            var words = quoted ? _quotedInText : _inText;
            if (value.Length >= ShortestInText && words.Count < MaxRemembered && words.TryAdd(value, shown))
            {
                _finder = null;
            }
        }

        return shown;
    }

    static string Prefix(LogMaskKind kind, string value) => kind switch
    {
        LogMaskKind.Host => IPAddress.TryParse(value, out _) ? "ip" : "host",
        LogMaskKind.User => "user",
        LogMaskKind.Name => "name",
        LogMaskKind.Path => "path",
        LogMaskKind.Token => "token",
        LogMaskKind.Mac => "mac",
        _ => "web",
    };

    // Addresses that say nothing about anyone's network.
    static bool IsOpen(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address)
            && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast));

    // Where updates come from; hiding them would only make an update problem harder to read.
    static bool IsOrrerysOwn(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    Finder CurrentFinder()
    {
        lock (_gate)
        {
            return _finder ??= new Finder(Pattern(_inText.Keys, quoted: false), new Dictionary<string, string>(_inText, StringComparer.OrdinalIgnoreCase),
                Pattern(_quotedInText.Keys, quoted: true), new Dictionary<string, string>(_quotedInText, StringComparer.OrdinalIgnoreCase));
        }
    }

    // Longest first, so a full path is replaced before a folder name inside it. Not part of a longer word or name.
    static Regex? Pattern(IEnumerable<string> values, bool quoted)
    {
        var alternatives = string.Join("|", values.OrderByDescending(v => v.Length).Select(Regex.Escape));
        if (alternatives.Length == 0)
        {
            return null;
        }

        var pattern = quoted ? $"(?<=[\"'])(?:{alternatives})(?=[\"'])" : $"(?<![\\w-])(?:{alternatives})(?![\\w-])";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    sealed record Finder(Regex? Known, Dictionary<string, string> Tags, Regex? Quoted, Dictionary<string, string> QuotedTags);

    [GeneratedRegex(@"^(?<tag>(?:host|ip|user|name|path|token|mac|web)-[0-9a-f]{6})(?:\.[A-Za-z0-9]{1,7})?$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^(?:\[(?<bracket>[0-9A-Fa-f:.]+)\]|(?<host>[^:\[\]]+)):(?<port>\d{1,5})$")]
    private static partial Regex HostAndPortPattern();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.-]*://[^\s<>""]+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(?<![\w.+-])(?<user>[\w.+-]+)@(?<host>[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\w.])\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?![\w]|\.\d)")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![\w:.])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])")]
    private static partial Regex Ipv6Pattern();

    [GeneratedRegex(@"(?<![\w:-])[0-9A-Fa-f]{2}([:-])[0-9A-Fa-f]{2}(?:\1[0-9A-Fa-f]{2}){4}(?![\w:-])")]
    private static partial Regex MacPattern();
}
