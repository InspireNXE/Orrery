using Orrery.Core.Connections;

namespace Orrery.Core.Import;

/// <summary>
/// What an imported file sets up that gives the remote side a way into this computer: shared drives and folders, and
/// SSH forwards and proxies that listen beyond this computer or open a port on the server. They're imported as written,
/// since they're usually the user's own, but named in the preview so a file from someone else can't set them quietly.
/// </summary>
public static class ImportReview
{
    /// <summary>One warning per connection in <paramref name="root"/> that shares or forwards something.</summary>
    public static IReadOnlyList<string> Warnings(ConnectionFolder root)
    {
        var warnings = new List<string>();
        foreach (var entry in root.Descendants().OfType<ConnectionEntry>())
        {
            var settings = SettingsResolver.Resolve(entry);
            var name = $"\"{entry.Name}\" ({entry.Host})";
            var protocol = settings.Protocol.Value;
            if (protocol == Protocol.Rdp)
            {
                if (settings.ShareAllDrives.Value)
                {
                    warnings.Add($"{name} shares all of this computer's drives with the remote computer when it connects.");
                }
                else if (settings.SharedFolders.Value is { Count: > 0 } folders)
                {
                    warnings.Add($"{name} shares {Folders(folders)} with the remote computer when it connects.");
                }
            }

            if (protocol == Protocol.Ssh)
            {
                foreach (var forward in entry.PortForwards ?? [])
                {
                    if (forward.Kind == ForwardKind.Remote || !IsLoopback(forward.EffectiveListenAddress))
                    {
                        warnings.Add($"{name} opens a port forward when it connects: {forward.Describe()}.");
                    }
                }

                if (settings.SocksProxy.Value is { Length: > 0 } socks && PortForward.TryParseSocks(socks, out _) is { } proxy
                    && !IsLoopback(proxy.EffectiveListenAddress))
                {
                    warnings.Add($"{name} opens a SOCKS proxy that other computers can reach: {proxy.Describe()}.");
                }
            }
        }

        return ImportReport.Shorten(warnings);
    }

    /// <summary>Every saved command in <paramref name="root"/>, with the folder or connection it's on.</summary>
    public static IReadOnlyList<(ConnectionNode Owner, SavedCommand Command)> Commands(ConnectionFolder root) =>
        root.Descendants().Prepend(root).SelectMany(n => (n.Commands ?? []).Select(c => (n, c))).ToList();

    /// <summary>Takes every saved command out of <paramref name="root"/>, for an import that leaves them behind.</summary>
    public static void DropCommands(ConnectionFolder root)
    {
        foreach (var node in root.Descendants().Prepend(root))
        {
            node.Commands = null;
        }
    }

    /// <summary><paramref name="result"/> with <see cref="Warnings"/> added to its report's.</summary>
    public static ImportResult WithWarnings(ImportResult result) => Warnings(result.Root) is { Count: > 0 } added
        ? result with { Report = result.Report with { Warnings = ImportReport.Shorten([.. result.Report.Warnings, .. added]) } }
        : result;

    static string Folders(IReadOnlyList<string> folders) => folders.Count == 1
        ? $"the folder {Short(folders[0])}"
        : $"{folders.Count} folders ({string.Join(", ", folders.Take(3).Select(Short))}{(folders.Count > 3 ? ", …" : "")})";

    static string Short(string text) => text.Length <= 120 ? text : text[..117] + "…";

    static bool IsLoopback(string address) =>
        address is "localhost" || (System.Net.IPAddress.TryParse(address.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip));
}
