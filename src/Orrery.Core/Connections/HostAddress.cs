namespace Orrery.Core.Connections;

public static class HostAddress
{
    /// <summary>Accepts "host", "host:port", "[ipv6]" and "[ipv6]:port". A bare IPv6 address has no port.</summary>
    public static bool TryParse(string? input, int defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = defaultPort;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string? portText = null;
        if (text.StartsWith('['))
        {
            int end = text.IndexOf(']');
            if (end < 0) return false;
            host = text[1..end];
            if (end + 1 < text.Length)
            {
                if (text[end + 1] != ':') return false;
                portText = text[(end + 2)..];
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            int colon = text.IndexOf(':');
            host = text[..colon];
            portText = text[(colon + 1)..];
        }
        else
        {
            host = text;
        }

        if (portText is not null && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
        {
            return false;
        }

        return host.Length > 0 && !host.Any(char.IsWhiteSpace);
    }
}
