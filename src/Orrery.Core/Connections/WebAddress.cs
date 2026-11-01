namespace Orrery.Core.Connections;

/// <summary>
/// A web connection's address. Its host may carry a path after it ("router.lan/admin"), kept with the host so the tree,
/// quick connect and history show one address. The port comes from the connection's settings, as for any protocol,
/// unless the host gives one ("router.lan:8443/admin").
/// </summary>
public static class WebAddress
{
    static readonly char[] PathStart = ['/', '?', '#'];

    /// <summary>The host without any path, query or fragment after it: what's checked and shown as the host.</summary>
    public static string HostOf(string host)
    {
        var trimmed = host.Trim();
        int cut = trimmed.IndexOfAny(PathStart);
        return cut < 0 ? trimmed : trimmed[..cut];
    }

    /// <summary>What follows the host: the path, query and fragment, or empty.</summary>
    public static string PathOf(string host)
    {
        var trimmed = host.Trim();
        int cut = trimmed.IndexOfAny(PathStart);
        return cut < 0 ? string.Empty : trimmed[cut..];
    }

    /// <summary>
    /// The page to open: http or https only, at <paramref name="host"/> and <paramref name="port"/>, with the path the
    /// host carries. Null when <paramref name="protocol"/> isn't a web one or the host isn't something to open.
    /// </summary>
    public static Uri? Url(Protocol protocol, string? host, int port)
    {
        if (!ProtocolInfo.IsWeb(protocol) || string.IsNullOrWhiteSpace(host) || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return null;
        }

        // A name with user details in it ("user@host") would open somewhere else than it seems to.
        var authority = HostOf(host);
        if (authority.Contains('@') || !HostAddress.TryParse(authority, port, out var name, out var chosenPort))
        {
            return null;
        }

        var scheme = protocol == Protocol.Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var path = PathOf(host);
        var text = $"{scheme}://{(name.Contains(':') ? $"[{name}]" : name)}"
            + (chosenPort == ProtocolInfo.DefaultPort(protocol) ? string.Empty : $":{chosenPort}")
            + (path.StartsWith('/') ? path : "/" + path);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == scheme && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;
    }

    /// <summary>
    /// Reads a typed address that starts with http:// or https:// into a connection's parts: the protocol, the host with
    /// any path after it, and the port (the scheme's own unless one is given).
    /// </summary>
    public static bool TryParseUrl(string text, out Protocol protocol, out string host, out int port)
    {
        (protocol, host, port) = (Protocol.Http, string.Empty, 0);
        var trimmed = text.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            (protocol, trimmed) = (Protocol.Https, trimmed["https://".Length..]);
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["http://".Length..];
        }
        else
        {
            return false;
        }

        var authority = HostOf(trimmed);
        if (!HostAddress.TryParse(authority, ProtocolInfo.DefaultPort(protocol), out var name, out port) || authority.Contains('@'))
        {
            return false;
        }

        var path = PathOf(trimmed);
        host = (name.Contains(':') ? $"[{name}]" : name) + (path == "/" ? string.Empty : path);
        return Url(protocol, host, port) is not null;
    }
}
