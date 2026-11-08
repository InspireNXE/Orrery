using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Orrery.Core.Import;

namespace Orrery.Core.Export;

public enum ExportFormat
{
    /// <summary>Orrery's own file, which Orrery imports back with every setting.</summary>
    Orrery,

    /// <summary>mRemoteNG's confCons.xml (ConfVersion 2.6).</summary>
    MRemoteNg,

    /// <summary>One row per connection, for spreadsheets and inventories.</summary>
    Csv,
}

/// <summary>
/// A finished export: the file's text, what's in it, and what didn't carry over. <paramref name="Secrets"/>
/// counts the saved credentials an Orrery file carries, or the connections a confCons.xml has a password for.
/// </summary>
public sealed record ExportResult(string Content, int Folders, int Connections, IReadOnlyList<string> Warnings, int Secrets = 0);

/// <summary>
/// Saved passwords and keys to put in an export, and the password protecting them: an Orrery file is
/// encrypted with it as a whole (<see cref="SealedExport"/>), and a confCons.xml uses it as mRemoteNG's
/// master password. Only <paramref name="Credentials"/> (by id) are included.
/// </summary>
public sealed record ExportSecrets(string Password, IReadOnlyDictionary<Guid, Credential> Credentials)
{
    /// <summary>Argon2id cost for the Orrery file; tests make it cheap.</summary>
    internal SealedExport.KdfSettings? Kdf { get; init; }

    /// <summary>Keeps the password out of logs and crash reports.</summary>
    public override string ToString() => $"ExportSecrets {{ Credentials = {Credentials.Count} }}";
}

/// <summary>
/// Writes a connection, a folder or the whole tree to a file. Passwords and keys are left out unless the
/// export is given <see cref="ExportSecrets"/>, and then only encrypted. Without them, a connection that
/// uses a saved credential gets that credential's username and domain instead.
/// </summary>
public static class ConnectionExporter
{
    /// <summary>Marks an Orrery export file, so it isn't mistaken for connections.json or anything else.</summary>
    internal const string OrreryFormatName = "orrery-connections";
    /// <summary>The newest schema the importer reads. Files are written with the oldest that holds them, as connections.json is.</summary>
    internal const int OrrerySchemaVersion = 6;

    public static ExportFormat? FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" => ExportFormat.Orrery,
        ".xml" => ExportFormat.MRemoteNg,
        ".csv" => ExportFormat.Csv,
        _ => null,
    };

    /// <param name="node">What to export; the tree's root exports everything, including its defaults.</param>
    /// <param name="credentials">Saved credentials by id, for their usernames; null when they couldn't be read (a locked vault).</param>
    /// <param name="secrets">Passwords and keys to include, encrypted; null leaves them out. Not for CSV.</param>
    public static ExportResult Export(ConnectionNode node, ExportFormat format, IReadOnlyDictionary<Guid, CredentialSummary>? credentials,
        DateTimeOffset now, ExportSecrets? secrets = null)
    {
        if (secrets is not null && format == ExportFormat.Csv)
        {
            throw new ArgumentException("A CSV export can't carry passwords or keys.", nameof(secrets));
        }

        var warnings = new List<string>();
        var snapshot = Snapshot(node, credentials, secrets, keepCredentials: format == ExportFormat.Orrery, warnings);
        var tree = snapshot.Tree;
        int folders = tree.Root.Descendants().OfType<ConnectionFolder>().Count();
        // A serial connection isn't written to a confCons.xml, so it isn't counted there.
        int connections = tree.AllConnections().Count(e => format != ExportFormat.MRemoteNg || InMRemoteNg(e));
        var content = format switch
        {
            ExportFormat.Orrery => WriteOrrery(snapshot, now, secrets),
            ExportFormat.MRemoteNg => WriteMRemoteNg(snapshot, secrets, warnings),
            ExportFormat.Csv => WriteCsv(tree),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        int included = secrets is null ? 0
            : format == ExportFormat.Orrery ? snapshot.Credentials.Count
            : snapshot.ConnectionSecrets.Values.Count(c => c.Kind == CredentialKind.Password);
        return new ExportResult(content, folders, connections, warnings, included);
    }

    /// <param name="Credentials">Saved credentials an Orrery file carries whole, in the order first used.</param>
    /// <param name="ConnectionSecrets">Each connection's saved credential, by connection id, for a confCons.xml with secrets.</param>
    sealed record ExportSnapshot(ConnectionTree Tree, IReadOnlyList<Credential> Credentials, IReadOnlyDictionary<Guid, Credential> ConnectionSecrets);

    /// <summary>
    /// A detached copy to write out. Its root is the tree's root (with the defaults) when exporting
    /// everything, else a plain folder holding the exported node, which takes the values it inherited.
    /// Saved credentials become their username and domain, except those an Orrery file with secrets carries
    /// whole (<paramref name="keepCredentials"/>); jump hosts outside the export are dropped.
    /// </summary>
    static ExportSnapshot Snapshot(ConnectionNode node, IReadOnlyDictionary<Guid, CredentialSummary>? credentials, ExportSecrets? secrets,
        bool keepCredentials, List<string> warnings)
    {
        var copy = Copy(node);
        ConnectionFolder root;
        if (node.Parent is null && copy is ConnectionFolder whole)
        {
            root = whole;
        }
        else
        {
            // A guest exported on its own becomes a plain connection, keeping what it took from its host.
            if (node is ConnectionEntry { GuestOf: { } host })
            {
                copy.Settings.FillFrom(host.Settings, InheritableSettings.PassedToGuests);
            }

            // A host's guests skip it for what they keep, so they take those from its folders themselves.
            foreach (var ancestor in node.Ancestors())
            {
                copy.Settings.FillFrom(ancestor.Settings);
                foreach (var guest in (copy as ConnectionEntry)?.Guests ?? [])
                {
                    guest.Settings.FillFrom(ancestor.Settings, InheritableSettings.KeptByGuests);
                }
            }

            root = new ConnectionFolder { Name = "Connections" };
            root.ChildList.Add(copy);
        }

        var tree = new ConnectionTree(root);

        // Read before saved credentials become usernames below, which takes the ids away.
        var connectionSecrets = new Dictionary<Guid, Credential>();
        if (secrets is not null && !keepCredentials)
        {
            foreach (var entry in tree.AllConnections())
            {
                if (SettingsResolver.Resolve(entry).CredentialId.Value is { } id && secrets.Credentials.TryGetValue(id, out var credential))
                {
                    connectionSecrets[entry.Id] = credential;
                }
            }
        }

        var carried = new Dictionary<Guid, Credential>();
        var all = tree.Root.Descendants().Prepend(tree.Root).ToList();
        var ids = all.Select(n => n.Id).ToHashSet();
        int unknownCredentials = 0;
        foreach (var item in all)
        {
            var settings = item.Settings;
            if (keepCredentials && settings.CredentialId is { } keptId && secrets?.Credentials.GetValueOrDefault(keptId) is { } kept)
            {
                carried.TryAdd(keptId, kept);
            }
            else if (settings.CredentialId is { } credentialId && credentialId != InheritableSettings.NoCredential)
            {
                if (credentials?.GetValueOrDefault(credentialId) is { } summary)
                {
                    settings.Username = summary.Username ?? string.Empty;
                    settings.Domain = summary.Domain ?? string.Empty;
                }
                else
                {
                    unknownCredentials++;
                }

                settings.CredentialId = null;
            }

            if (settings.JumpHostId is { } jumpId && jumpId != InheritableSettings.NoJumpHost && !ids.Contains(jumpId))
            {
                settings.JumpHostId = null;
                warnings.Add($"{Describe(item)}: its jump host isn't part of the export, so it was left out.");
            }
        }

        if (unknownCredentials > 0)
        {
            warnings.Add(credentials is null
                ? $"{Count(unknownCredentials, "folder or connection", "folders or connections")} use a saved credential, but the vault was locked, so their usernames aren't included."
                : $"{Count(unknownCredentials, "folder or connection", "folders or connections")} use a saved credential that no longer exists, so their usernames aren't included.");
        }

        return new ExportSnapshot(tree, carried.Values.ToList(), connectionSecrets);
    }

    /// <summary>A deep copy that keeps ids, so jump hosts inside the export still point at the right connection.</summary>
    static ConnectionNode Copy(ConnectionNode node)
    {
        ConnectionNode copy = node switch
        {
            ConnectionEntry entry => new ConnectionEntry { Id = entry.Id, Host = entry.Host, MacAddress = entry.MacAddress, WakeBroadcast = entry.WakeBroadcast, ManagementUrl = entry.ManagementUrl, StartFolder = entry.StartFolder,
                RunAsAdministrator = entry.RunAsAdministrator, PortForwards = entry.PortForwards?.ToList() },
            ConnectionFolder folder => new ConnectionFolder { Id = folder.Id, Expanded = folder.Expanded },
            _ => throw new NotSupportedException(node.GetType().Name),
        };
        copy.Name = node.Name;
        copy.Notes = node.Notes;
        copy.ImportSourceId = node.ImportSourceId;
        copy.Settings = node.Settings.Clone();
        copy.Commands = node.Commands?.Select(c => c.Clone()).ToList();
        if (node is ConnectionFolder source && copy is ConnectionFolder target)
        {
            foreach (var child in source.Children)
            {
                target.ChildList.Add(Copy(child));
            }
        }
        else if (node is ConnectionEntry { IsHost: true } original && copy is ConnectionEntry host)
        {
            host.GuestsCollapsed = original.GuestsCollapsed;
            host.GuestList = original.Guests.Select(g => (ConnectionEntry)Copy(g)).ToList();
        }

        return copy;
    }

    static string Describe(ConnectionNode node) => node.Path is { Length: > 0 } path ? $"{path} / {node.Name}" : node.Name;

    /// <summary>The start of a sentence turned into the middle of one, leaving acronyms such as SSH alone.</summary>
    static string Lowered(string text) =>
        text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    // ---- Orrery ----

    /// <param name="Credentials">Saved credentials, secrets included; only ever written inside a sealed file.</param>
    internal sealed record OrreryFile(string Format, int SchemaVersion, DateTimeOffset Exported, ConnectionFolder Root, IReadOnlyList<Credential>? Credentials = null);

    static string WriteOrrery(ExportSnapshot snapshot, DateTimeOffset now, ExportSecrets? secrets)
    {
        var root = snapshot.Tree.Root;
        var file = new OrreryFile(OrreryFormatName, ConnectionStore.SchemaFor(root), now, root, secrets is null ? null : snapshot.Credentials);
        if (secrets is null)
        {
            return JsonSerializer.Serialize(file, ConnectionStore.JsonOptions);
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(file, ConnectionStore.JsonOptions);
        try
        {
            return SealedExport.Seal(plaintext, secrets.Password, secrets.Kdf);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // ---- mRemoteNG ----

    const string MRemoteNgNamespace = "http://mremoteng.org";
    const int MRemoteNgKdfIterations = 1000;
    /// <summary>
    /// For a file with passwords: the most mRemoteNG offers itself, since its PBKDF2 is the only thing slowing a guess at
    /// the master password. It reads the cost from the file. Each encrypted value pays it, so a file with hundreds of
    /// passwords opens a few seconds slower, and Orrery's importer takes up to about 4,000 of them.
    /// </summary>
    const int MRemoteNgProtectedKdfIterations = 50_000;

    /// <summary>
    /// Every attribute a ConfVersion 2.6 node has, with mRemoteNG's defaults, in the order it writes them.
    /// Taken from a real file (the import test fixture); the ones Orrery knows are filled in per node.
    /// </summary>
    static readonly (string Name, string Default)[] MRemoteNgAttributes =
    [
        ("Name", ""), ("Type", "Connection"), ("Descr", ""), ("Icon", "mRemoteNG"), ("Panel", "General"), ("Id", ""),
        ("Username", ""), ("Domain", ""), ("Password", ""), ("Hostname", ""), ("Protocol", "RDP"), ("PuttySession", "Default Settings"),
        ("Port", "3389"), ("ConnectToConsole", "false"), ("UseCredSsp", "true"), ("RenderingEngine", "IE"),
        ("ICAEncryptionStrength", "EncrBasic"), ("RDPAuthenticationLevel", "NoAuth"), ("RDPMinutesToIdleTimeout", "0"),
        ("RDPAlertIdleTimeout", "false"), ("LoadBalanceInfo", ""), ("Colors", "Colors16Bit"), ("Resolution", "FitToWindow"),
        ("AutomaticResize", "true"), ("DisplayWallpaper", "true"), ("DisplayThemes", "true"), ("EnableFontSmoothing", "true"),
        ("EnableDesktopComposition", "true"), ("CacheBitmaps", "true"), ("RedirectDiskDrives", "false"), ("RedirectPorts", "false"),
        ("RedirectPrinters", "false"), ("RedirectSmartCards", "false"), ("RedirectSound", "DoNotPlay"), ("SoundQuality", "Dynamic"),
        ("RedirectKeys", "false"), ("RedirectClipboard", "true"), ("Connected", "false"), ("PreExtApp", ""), ("PostExtApp", ""),
        ("MacAddress", ""), ("UserField", ""), ("ExtApp", ""), ("VNCCompression", "CompNone"), ("VNCEncoding", "EncHextile"),
        ("VNCAuthMode", "AuthVNC"), ("VNCProxyType", "ProxyNone"), ("VNCProxyIP", ""), ("VNCProxyPort", "0"), ("VNCProxyUsername", ""),
        ("VNCProxyPassword", ""), ("VNCColors", "ColNormal"), ("VNCSmartSizeMode", "SmartSAspect"), ("VNCViewOnly", "false"),
        ("RDGatewayUsageMethod", "Never"), ("RDGatewayHostname", ""), ("RDGatewayUseConnectionCredentials", "Yes"),
        ("RDGatewayUsername", ""), ("RDGatewayPassword", ""), ("RDGatewayDomain", ""),
    ];

    /// <summary>Every Inherit… flag a ConfVersion 2.6 node has. Exported nodes carry their own values, so all are false.</summary>
    static readonly string[] MRemoteNgInheritFlags =
    [
        "CacheBitmaps", "Colors", "Description", "DisplayThemes", "DisplayWallpaper", "EnableFontSmoothing", "EnableDesktopComposition",
        "Domain", "Icon", "Panel", "Password", "Port", "Protocol", "PuttySession", "RedirectDiskDrives", "RedirectKeys", "RedirectPorts",
        "RedirectPrinters", "RedirectSmartCards", "RedirectSound", "SoundQuality", "Resolution", "AutomaticResize", "UseConsoleSession",
        "UseCredSsp", "RenderingEngine", "Username", "ICAEncryptionStrength", "RDPAuthenticationLevel", "RDPMinutesToIdleTimeout",
        "RDPAlertIdleTimeout", "LoadBalanceInfo", "PreExtApp", "PostExtApp", "MacAddress", "UserField", "ExtApp", "VNCCompression",
        "VNCEncoding", "VNCAuthMode", "VNCProxyType", "VNCProxyIP", "VNCProxyPort", "VNCProxyUsername", "VNCProxyPassword", "VNCColors",
        "VNCSmartSizeMode", "VNCViewOnly", "RDGatewayUsageMethod", "RDGatewayHostname", "RDGatewayUseConnectionCredentials",
        "RDGatewayUsername", "RDGatewayPassword", "RDGatewayDomain", "RedirectClipboard",
    ];

    static string WriteMRemoteNg(ExportSnapshot snapshot, ExportSecrets? secrets, List<string> warnings)
    {
        var tree = snapshot.Tree;
        int iterations = secrets is null ? MRemoteNgKdfIterations : MRemoteNgProtectedKdfIterations;
        // With passwords, the marker is encrypted with the master password, so mRemoteNG asks for it.
        var marker = secrets is null
            ? MRemoteNgCrypto.Encrypt(MRemoteNgCrypto.NotProtectedMarker, MRemoteNgCrypto.DefaultPassword, iterations)
            : MRemoteNgCrypto.Encrypt(MRemoteNgCrypto.ProtectedMarker, secrets.Password, iterations);
        string PasswordFor(ConnectionNode node) =>
            secrets is not null && snapshot.ConnectionSecrets.TryGetValue(node.Id, out var credential) && credential.Kind == CredentialKind.Password
                ? MRemoteNgCrypto.Encrypt(credential.Secret, secrets.Password, iterations)
                : string.Empty;

        XNamespace ns = MRemoteNgNamespace;
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "Connections",
                new XAttribute(XNamespace.Xmlns + "mrng", MRemoteNgNamespace),
                new XAttribute("Name", "Connections"),
                new XAttribute("Export", "false"),
                new XAttribute("EncryptionEngine", "AES"),
                new XAttribute("BlockCipherMode", "GCM"),
                new XAttribute("KdfIterations", iterations.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("FullFileEncryption", "false"),
                // mRemoteNG checks whether this decrypts with its default password to know if there's a master password.
                new XAttribute("Protected", marker),
                new XAttribute("ConfVersion", "2.6"),
                MRemoteNgNodes(tree.Root.Children, PasswordFor)));

        int keys = snapshot.ConnectionSecrets.Values.Count(c => c.Kind != CredentialKind.Password);
        if (keys > 0)
        {
            warnings.Add($"mRemoteNG has no place for SSH keys, so {Count(keys, "connection's key was", "connections' keys were")} left out.");
        }

        int hosts = tree.AllConnections().Count(e => e.IsHost);
        if (hosts > 0)
        {
            warnings.Add($"mRemoteNG has no hosts, so {Count(hosts, "host was", "hosts were")} written as a folder holding the host and its guests.");
        }

        int serial = tree.AllConnections().Count(e => !InMRemoteNg(e));
        if (serial > 0)
        {
            warnings.Add($"mRemoteNG has no serial connections, so {Count(serial, "was", "were")} left out.");
        }

        var lost = new Dictionary<string, int>();
        void Lost(string what, bool when)
        {
            if (when) lost[what] = lost.GetValueOrDefault(what) + 1;
        }

        foreach (var entry in tree.AllConnections().Where(InMRemoteNg))
        {
            var resolved = SettingsResolver.Resolve(entry);
            Lost("Jump hosts", resolved.JumpHostId.Value is not null);
            Lost("Session recording", resolved.RecordSessions.Value);
            Lost("Saved commands", SavedCommands.For(entry).Count > 0);
            Lost("Port forwards", entry.PortForwards is { Count: > 0 });
            Lost("Wake-on-LAN broadcast addresses", entry.WakeBroadcast is not null);
            Lost("Management URLs", entry.ManagementUrl is not null);
            Lost("Fixed desktop sizes", resolved.DesktopSize.Value is not null);
            // The path stays in an Orrery export, but mRemoteNG has no field for a key file to hold it.
            Lost("SSH key files", resolved.Protocol.Value == Protocol.Ssh && resolved.KeyFile.Value is not null);
            Lost("SSH agent turned off", resolved.Protocol.Value == Protocol.Ssh && !resolved.UseSshAgent.Value);
            Lost("Older SSH algorithms allowed", resolved.Protocol.Value == Protocol.Ssh && resolved.AllowLegacySsh.Value);
            Lost("Reconnect turned off", !resolved.AutoReconnect.Value);
            Lost("Enlarge setting chosen per connection", resolved.EnlargeRemoteDesktop.Value is not null);
            Lost("Where web pages open, chosen per connection", resolved.WebPagesInOrrery.Value is not null);
            Lost("Where Alt+Tab goes, chosen per connection", resolved.SystemShortcuts.Value is not null);
            Lost("Color correction turned off", !resolved.ColorCorrection.Value);
            bool rdp = resolved.Protocol.Value == Protocol.Rdp;
            Lost("Shared folders (only sharing every drive carries over)", rdp && !resolved.ShareAllDrives.Value && resolved.SharedFolders.Value.Count > 0);
            Lost("Full screen over every display or chosen ones", rdp && resolved.FullScreen.Value.On != FullScreenOn.ThisDisplay);
            Lost("SSH keep-alive intervals", resolved.Protocol.Value == Protocol.Ssh && !resolved.KeepAliveSeconds.IsDefault);
            Lost("SOCKS proxies", resolved.Protocol.Value == Protocol.Ssh && resolved.SocksProxy.Value is not null);
        }

        foreach (var (what, count) in lost.OrderByDescending(l => l.Value))
        {
            warnings.Add($"mRemoteNG has no equivalent for {Lowered(what)}: {Count(count, "connection", "connections")}.");
        }

        var output = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            document.Save(writer);
        }

        return output.ToString();
    }

    /// <summary>Whether mRemoteNG can hold the connection: every protocol it has a name for, so not a serial port or a local shell.</summary>
    static bool InMRemoteNg(ConnectionNode node) =>
        node is not ConnectionEntry entry || ProtocolTraits.Of(SettingsResolver.Resolve(entry).Protocol.Value).MRemoteNgNames.Count > 0;

    /// <param name="passwordFor">A connection's encrypted password, or empty.</param>
    static IEnumerable<XElement> MRemoteNgNodes(IEnumerable<ConnectionNode> nodes, Func<ConnectionNode, string> passwordFor) =>
        nodes.Where(n => InMRemoteNg(n) || n is ConnectionEntry { IsHost: true }).Select(n => MRemoteNgNode(n, passwordFor));

    /// <param name="passwordFor">A connection's encrypted password, or empty.</param>
    static XElement MRemoteNgNode(ConnectionNode node, Func<ConnectionNode, string> passwordFor) =>
        node is ConnectionEntry { IsHost: true } host
            ? MRemoteNgNode(host, asFolder: true,
                [.. InMRemoteNg(host) ? [MRemoteNgNode(host, asFolder: false, [], passwordFor)] : Array.Empty<XElement>(), .. MRemoteNgNodes(host.Guests, passwordFor)], passwordFor)
            : MRemoteNgNode(node, asFolder: node is ConnectionFolder,
                MRemoteNgNodes((node as ConnectionFolder)?.Children ?? [], passwordFor).ToList(), passwordFor);

    /// <param name="asFolder">Written as a container. A host is written twice: as a folder named after it, and inside that as the connection.</param>
    static XElement MRemoteNgNode(ConnectionNode node, bool asFolder, List<XElement> children, Func<ConnectionNode, string> passwordFor)
    {
        var resolved = SettingsResolver.Resolve(node);
        var values = new Dictionary<string, string>
        {
            ["Name"] = node.Name,
            ["Type"] = asFolder ? "Container" : "Connection",
            ["Descr"] = node.Notes ?? string.Empty,
            // A host's folder needs an id of its own; derived from the host's, so exporting again gives the same one.
            ["Id"] = (asFolder && node is ConnectionEntry ? HostFolderId(node.Id) : node.Id).ToString(),
            ["Username"] = resolved.Username.Value ?? string.Empty,
            ["Domain"] = resolved.Domain.Value ?? string.Empty,
            ["Password"] = asFolder ? string.Empty : passwordFor(node),
            ["Hostname"] = asFolder ? string.Empty : (node as ConnectionEntry)?.Host ?? string.Empty,
            ["MacAddress"] = asFolder ? string.Empty : (node as ConnectionEntry)?.MacAddress ?? string.Empty,
            // A folder set to a protocol mRemoteNG doesn't have is written as RDP, its default.
            ["Protocol"] = ProtocolTraits.Of(resolved.Protocol.Value).MRemoteNgNames.FirstOrDefault() ?? "RDP",
            ["Port"] = resolved.Port.Value.ToString(CultureInfo.InvariantCulture),
            ["RedirectClipboard"] = resolved.ShareClipboard.Value ? "true" : "false",
            // The visual effects and VNC compression the display quality stands for; importing reads them back the same.
            ["DisplayWallpaper"] = resolved.DisplayQuality.Value != DisplayQuality.Fastest ? "true" : "false",
            ["DisplayThemes"] = resolved.DisplayQuality.Value != DisplayQuality.Fastest ? "true" : "false",
            ["EnableFontSmoothing"] = resolved.DisplayQuality.Value != DisplayQuality.Fastest ? "true" : "false",
            ["EnableDesktopComposition"] = resolved.DisplayQuality.Value == DisplayQuality.Best ? "true" : "false",
            ["VNCCompression"] = resolved.DisplayQuality.Value == DisplayQuality.Fastest ? "Comp9" : "CompNone",
            ["RedirectSound"] = resolved.RemoteAudio.Value == RemoteAudio.PlayHere ? "BringToThisComputer" : "DoNotPlay",
            // mRemoteNG 2.6 can share every drive or none; chosen folders are counted as lost above.
            ["RedirectDiskDrives"] = resolved.ShareAllDrives.Value ? "true" : "false",
            ["ConnectToConsole"] = resolved.AdminSession.Value ? "true" : "false",
            ["VNCViewOnly"] = resolved.ViewOnly.Value ? "true" : "false",
            // mRemoteNG can't shrink without also enlarging, so shrink to fit is written as filling the tab.
            ["VNCSmartSizeMode"] = resolved.Scaling.Value == DesktopScaling.ActualSize ? "SmartSNo" : "SmartSAspect",
        };

        var element = new XElement("Node");
        foreach (var (name, fallback) in MRemoteNgAttributes)
        {
            element.Add(new XAttribute(name, values.GetValueOrDefault(name, fallback)));
            if (name == "Type" && asFolder)
            {
                bool expanded = node is ConnectionFolder folder ? folder.Expanded : !((ConnectionEntry)node).GuestsCollapsed;
                element.Add(new XAttribute("Expanded", expanded ? "true" : "false"));
            }
        }

        foreach (var flag in MRemoteNgInheritFlags)
        {
            element.Add(new XAttribute("Inherit" + flag, "false"));
        }

        element.Add(children);
        return element;
    }

    static Guid HostFolderId(Guid hostId)
    {
        var bytes = hostId.ToByteArray();
        bytes[0] ^= 0xFF;
        return new Guid(bytes);
    }

    /// <summary>So the XML declaration says utf-8, which is how the file is written.</summary>
    sealed class Utf8StringWriter() : StringWriter(CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }

    // ---- CSV ----

    static string WriteCsv(ConnectionTree tree)
    {
        var csv = new StringBuilder();
        csv.Append("Folder,Name,Host,Protocol,Port,Username,Domain,Notes\r\n");
        foreach (var entry in tree.AllConnections())
        {
            var resolved = SettingsResolver.Resolve(entry);
            string[] cells =
            [
                entry.Path, entry.Name, entry.Host, ProtocolInfo.Label(resolved.Protocol.Value),
                resolved.Port.Value.ToString(CultureInfo.InvariantCulture), resolved.Username.Value ?? string.Empty,
                resolved.Domain.Value ?? string.Empty, entry.Notes ?? string.Empty,
            ];
            csv.Append(string.Join(',', cells.Select(CsvCell))).Append("\r\n");
        }

        return csv.ToString();
    }

    /// <summary>
    /// Quotes a cell when it needs it, and puts an apostrophe before one a spreadsheet would run as a
    /// formula (starting with = + - @ or a tab), so a hostile name can't run anything when opened.
    /// </summary>
    internal static string CsvCell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
