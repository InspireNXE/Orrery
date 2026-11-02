namespace Orrery.Core.Network;

/// <summary>
/// A connection's management page, such as a firewall's or a server's out-of-band console, opened in the browser from
/// the connection's right-click menu.
/// </summary>
public static class ManagementUrl
{
    public const int MaxLength = 2048;

    /// <summary>
    /// Reads a web address, adding <c>https://</c> when it has no scheme (<c>10.20.0.1:8443</c>). Only http and https
    /// are taken, so the menu can't be made to run a program or open a file.
    /// </summary>
    public static bool TryNormalize(string? text, out string url)
    {
        url = string.Empty;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        url = value;
        return true;
    }
}
