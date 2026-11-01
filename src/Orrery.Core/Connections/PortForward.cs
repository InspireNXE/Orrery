using System.Globalization;

namespace Orrery.Core.Connections;

public enum ForwardKind
{
    /// <summary>A port on this computer reaches a host and port from the server's side (ssh -L).</summary>
    Local,

    /// <summary>A port on the server reaches a host and port from this computer's side (ssh -R).</summary>
    Remote,

    /// <summary>A SOCKS proxy on this computer that connects out from the server (ssh -D).</summary>
    Dynamic,
}

/// <summary>
/// One SSH port forward. The listening side binds to loopback unless <see cref="ListenAddress"/> says
/// otherwise. Written one per line, OpenSSH style: <c>L 8080:db:5432</c>, <c>R 9000:localhost:3000</c>,
/// <c>D 1080</c>, with an optional listen address first (<c>L 0.0.0.0:8080:db:5432</c>).
/// </summary>
public sealed record PortForward(ForwardKind Kind, int ListenPort, string? DestinationHost = null, int DestinationPort = 0, string? ListenAddress = null)
{
    public const string Loopback = "127.0.0.1";

    public string EffectiveListenAddress => string.IsNullOrEmpty(ListenAddress) ? Loopback : ListenAddress;

    public override string ToString()
    {
        var listen = string.IsNullOrEmpty(ListenAddress) ? ListenPort.ToString(CultureInfo.InvariantCulture) : $"{ListenAddress}:{ListenPort}";
        return Kind switch
        {
            ForwardKind.Dynamic => $"D {listen}",
            _ => $"{(Kind == ForwardKind.Local ? 'L' : 'R')} {listen}:{DestinationHost}:{DestinationPort}",
        };
    }

    /// <summary>What it does, for the session's opening lines, e.g. "127.0.0.1:8080 → db:5432 (from the server)".</summary>
    public string Describe() => Kind switch
    {
        ForwardKind.Local => $"{EffectiveListenAddress}:{ListenPort} here → {DestinationHost}:{DestinationPort} from the server",
        ForwardKind.Remote => $"{EffectiveListenAddress}:{ListenPort} on the server → {DestinationHost}:{DestinationPort} from here",
        _ => $"SOCKS proxy on {EffectiveListenAddress}:{ListenPort}",
    };

    /// <summary>Reads one line in the form <see cref="ToString"/> writes. Null, with <paramref name="error"/> set, when it doesn't parse.</summary>
    public static PortForward? TryParse(string line, out string? error)
    {
        error = null;
        var text = line.Trim();
        if (text.Length < 3 || !char.IsWhiteSpace(text[1]))
        {
            error = $"\"{text}\" should start with L, R or D and a space.";
            return null;
        }

        ForwardKind? kind = char.ToUpperInvariant(text[0]) switch
        {
            'L' => ForwardKind.Local,
            'R' => ForwardKind.Remote,
            'D' => ForwardKind.Dynamic,
            _ => null,
        };
        if (kind is null)
        {
            error = $"\"{text}\" should start with L, R or D.";
            return null;
        }

        var parts = text[2..].Trim().Split(':');
        string? listenAddress = null;
        if (kind == ForwardKind.Dynamic)
        {
            if (parts.Length == 2)
            {
                listenAddress = parts[0];
            }
            else if (parts.Length != 1)
            {
                error = $"\"{text}\": a SOCKS proxy takes a port, or an address and a port, like D 1080.";
                return null;
            }

            return ParsePort(parts[^1], out var socksPort) && ValidAddress(listenAddress)
                ? new PortForward(ForwardKind.Dynamic, socksPort, ListenAddress: listenAddress)
                : Fail(text, out error);
        }

        if (parts.Length == 4)
        {
            listenAddress = parts[0];
            parts = parts[1..];
        }

        if (parts.Length != 3 || !ParsePort(parts[0], out var listenPort) || !ParsePort(parts[2], out var destinationPort)
            || !ValidHost(parts[1]) || !ValidAddress(listenAddress))
        {
            error = $"\"{text}\" should look like {kind.Value.ToString()[0]} 8080:host:80.";
            return null;
        }

        return new PortForward(kind.Value, listenPort, parts[1], destinationPort, listenAddress);
    }

    /// <summary>Reads every non-blank line. Null, with <paramref name="error"/> set for the first bad one, if any doesn't parse.</summary>
    public static List<PortForward>? TryParseAll(string? text, out string? error)
    {
        error = null;
        var forwards = new List<PortForward>();
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParse(line, out error) is not { } forward)
            {
                return null;
            }

            forwards.Add(forward);
        }

        return forwards;
    }

    public static string FormatAll(IEnumerable<PortForward>? forwards) => string.Join("\n", (forwards ?? []).Select(f => f.ToString()));

    /// <summary>
    /// Reads a folder's SOCKS proxy setting: a port, or an address and a port ("1080", "127.0.0.1:1080"), with or
    /// without the D in front. Null, with <paramref name="error"/> set, when it doesn't parse.
    /// </summary>
    public static PortForward? TryParseSocks(string text, out string? error)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 2 && char.ToUpperInvariant(trimmed[0]) == 'D' && char.IsWhiteSpace(trimmed[1]))
        {
            trimmed = trimmed[2..].Trim();
        }

        if (TryParse("D " + trimmed, out _) is { } forward)
        {
            error = null;
            return forward;
        }

        error = $"The SOCKS proxy \"{text.Trim()}\" should be a port, like 1080, or an address and a port, like 127.0.0.1:1080.";
        return null;
    }

    /// <summary>
    /// A connection's own port forwards, plus the SOCKS proxy its folder asks for, unless one of its own already
    /// listens on that port.
    /// </summary>
    public static List<PortForward> WithSocks(IReadOnlyList<PortForward>? own, string? socksProxy)
    {
        var forwards = own?.ToList() ?? [];
        if (socksProxy is { Length: > 0 } && TryParseSocks(socksProxy, out _) is { } socks
            && !forwards.Any(f => f.Kind != ForwardKind.Remote && f.ListenPort == socks.ListenPort))
        {
            forwards.Add(socks);
        }

        return forwards;
    }

    static PortForward? Fail(string text, out string? error)
    {
        error = $"\"{text}\" should look like D 1080.";
        return null;
    }

    static bool ParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    static bool ValidHost(string host) => host.Length > 0 && !host.Any(char.IsWhiteSpace);

    static bool ValidAddress(string? address) => address is null || ValidHost(address);
}
