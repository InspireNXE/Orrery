using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;

namespace Orrery.Core.Import;

public sealed class ImportException(string message) : Exception(message);

/// <summary>The file has a master password and none (or the wrong one) was given.</summary>
public sealed class MasterPasswordRequiredException(bool wrongPassword)
    : Exception(wrongPassword ? "That master password doesn't open this file." : "This file is protected with a master password.")
{
    public bool WrongPassword { get; } = wrongPassword;
}

/// <summary>An import ready to add: the folder of imported items, the credentials to save, and what happened.</summary>
public sealed record ImportResult(ConnectionFolder Root, IReadOnlyList<Credential> Credentials, ImportReport Report)
{
    /// <summary>How many imported connections already exist in <paramref name="tree"/> from an earlier import.</summary>
    public int CountAlreadyIn(ConnectionTree tree)
    {
        var existing = ExistingSourceIds(tree);
        return Root.Descendants().OfType<ConnectionEntry>().Count(e => e.ImportSourceId is { } id && existing.Contains(id));
    }

    /// <summary>
    /// Drops connections already imported into <paramref name="tree"/>, then folders left empty that were
    /// imported before too, and credentials nothing uses any more.
    /// </summary>
    public ImportResult WithoutAlreadyIn(ConnectionTree tree)
    {
        var existing = ExistingSourceIds(tree);
        var importTree = new ConnectionTree(Root);

        // A jump host that's dropped as already imported is still what its users go through: point them at the copy in the tree.
        var already = Root.Descendants().OfType<ConnectionEntry>().Where(e => e.ImportSourceId is { } id && existing.Contains(id)).ToList();
        var inTree = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in tree.Root.Descendants())
        {
            inTree.TryAdd(OrreryImporter.SourcePrefix + node.Id, node.Id);
            if (node.ImportSourceId is { } source)
            {
                inTree.TryAdd(source, node.Id);
            }
        }

        var moved = already.Where(e => inTree.ContainsKey(e.ImportSourceId!)).ToDictionary(e => e.Id, e => inTree[e.ImportSourceId!]);
        foreach (var node in Root.Descendants().Where(n => n.Settings.JumpHostId is { } jump && moved.ContainsKey(jump)))
        {
            node.Settings.JumpHostId = moved[node.Settings.JumpHostId!.Value];
        }

        foreach (var entry in already)
        {
            importTree.Remove(entry);
        }

        // Deepest first, so a folder emptied by removing its subfolders goes too.
        foreach (var folder in Root.Descendants().OfType<ConnectionFolder>().Reverse().ToList())
        {
            if (folder.Children.Count == 0 && folder.ImportSourceId is { } id && existing.Contains(id))
            {
                importTree.Remove(folder);
            }
        }

        var used = Root.Descendants().Select(n => n.Settings.CredentialId).OfType<Guid>().ToHashSet();
        return this with { Credentials = Credentials.Where(c => used.Contains(c.Id)).ToList() };
    }

    // Each node's own id counts too, so an Orrery export brought back into the tree it came from is spotted.
    static HashSet<string> ExistingSourceIds(ConnectionTree tree) =>
        tree.Root.Descendants().Select(n => n.ImportSourceId).OfType<string>()
            .Concat(tree.Root.Descendants().Select(n => OrreryImporter.SourcePrefix + n.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

public sealed record ImportReport(
    string SourceFile,
    int Folders,
    int Connections,
    int CredentialsCreated,
    int PasswordsMerged,
    IReadOnlyDictionary<string, int> Protocols,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> NotYetSupported,
    IReadOnlyList<string> Warnings,
    string From = "mRemoteNG")
{
    /// <summary>Lines each list keeps; the rest are counted in one "and N more" line.</summary>
    public const int MaxListedLines = 500;

    /// <summary>
    /// <paramref name="lines"/> with anything past <see cref="MaxListedLines"/> folded into one "and N more"
    /// line. A list already shortened that way (one line over) comes back as it is.
    /// </summary>
    public static IReadOnlyList<string> Shorten(IReadOnlyList<string> lines) =>
        lines.Count <= MaxListedLines + 1 ? lines : [.. lines.Take(MaxListedLines), More(lines.Count - MaxListedLines)];

    internal static string More(int count) => $"…and {count} more";

    /// <summary>A Markdown summary for the import folder. Never contains secrets.</summary>
    public string ToMarkdown(DateTimeOffset when)
    {
        var md = new StringBuilder();
        md.AppendLine($"# {From} import, {when:yyyy-MM-dd HH:mm}");
        md.AppendLine();
        md.AppendLine($"Source: `{SourceFile}`");
        md.AppendLine();
        md.AppendLine($"- Folders: {Folders}");
        md.AppendLine($"- Connections: {Connections} ({string.Join(", ", Protocols.Select(p => $"{p.Key} {p.Value}"))})");
        md.AppendLine($"- Saved credentials created: {CredentialsCreated} ({PasswordsMerged} duplicate passwords merged)");
        Section(md, "Skipped", Skipped);
        Section(md, "Settings Orrery doesn't support yet (not imported)", NotYetSupported);
        Section(md, "Warnings", Warnings);
        return md.ToString();
    }

    static void Section(StringBuilder md, string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        md.AppendLine();
        md.AppendLine($"## {title}");
        md.AppendLine();
        foreach (var line in lines) md.AppendLine($"- {line}");
    }
}

/// <summary>
/// Imports mRemoteNG's <c>confCons.xml</c> (current format, ConfVersion 2.6). Written from the file format;
/// no mRemoteNG code is used. See docs/design-storage-vault-import.md §4.
/// <para>
/// mRemoteNG gives every node every setting plus an <c>Inherit…</c> flag. Values are first resolved the
/// mRemoteNG way, then written with as few overrides as possible: a folder only gets a value when one
/// of its children inherits it, and a node only stores a value when inheriting would give something
/// else. The resolved result is the same, without a folder's unused "RDP 3389" leaking into SSH hosts.
/// </para>
/// </summary>
public static class MRemoteNgImporter
{
    const string Namespace = "http://mremoteng.org";
    const string SourcePrefix = "mremoteng:";
    const double SupportedConfVersion = 2.6;
    const long MaxFileCharacters = 64L * 1024 * 1024;

    /// <summary>Most folders, connections and other nodes a file may hold.</summary>
    public const int MaxNodes = 20_000;

    /// <summary>Most distinct saved credentials one import may create.</summary>
    public const int MaxCredentials = 5_000;

    /// <summary>Largest <c>KdfIterations</c> accepted; mRemoteNG's own options go up to 50,000.</summary>
    public const int MaxKdfIterations = 1_000_000;

    /// <summary>
    /// Most PBKDF2 rounds one import may run in all (rounds × values decrypted): 4,000 passwords at
    /// mRemoteNG's highest setting, and a few minutes of work for a hostile file.
    /// </summary>
    public const long MaxTotalKdfIterations = 200_000_000;

    static readonly Dictionary<string, Protocol> ProtocolMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RDP"] = Protocol.Rdp,
        ["SSH2"] = Protocol.Ssh,
        ["SSH1"] = Protocol.Ssh,
        ["VNC"] = Protocol.Vnc,
        ["Telnet"] = Protocol.Telnet,
    };

    public static ImportResult ImportFile(string path, string? masterPassword = null)
    {
        using var stream = File.OpenRead(path);
        return Import(stream, Path.GetFileName(path), masterPassword);
    }

    public static ImportResult Import(Stream xml, string sourceName, string? masterPassword = null) =>
        Import(xml, sourceName, masterPassword, MaxTotalKdfIterations);

    /// <summary>As <see cref="Import(Stream, string, string?)"/>, with a smaller decryption budget for tests.</summary>
    internal static ImportResult Import(Stream xml, string sourceName, string? masterPassword, long kdfBudget)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxFileCharacters,
        };

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(xml, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new ImportException($"This isn't a readable XML file: {e.Message}");
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "Connections" || root.Name.NamespaceName != Namespace)
        {
            throw new ImportException("This isn't an mRemoteNG connections file (confCons.xml).");
        }

        var confVersion = double.TryParse((string?)root.Attribute("ConfVersion"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (confVersion < SupportedConfVersion)
        {
            throw new ImportException($"This file is from an older mRemoteNG (format {confVersion.ToString(CultureInfo.InvariantCulture)}). "
                + "Open it in a current mRemoteNG and save it once to upgrade it, then import again.");
        }

        if (!string.Equals((string?)root.Attribute("EncryptionEngine"), "AES", StringComparison.OrdinalIgnoreCase)
            || !string.Equals((string?)root.Attribute("BlockCipherMode"), "GCM", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportException("This file uses an encryption setting Orrery can't read yet (only AES-GCM is supported).");
        }

        int iterations = int.TryParse((string?)root.Attribute("KdfIterations"), out var k) && k > 0 ? k : 1000;
        if (iterations > MaxKdfIterations)
        {
            throw new ImportException($"This file's KdfIterations ({iterations:N0}) is far higher than mRemoteNG uses, so Orrery won't try to decrypt it.");
        }

        var crypto = new Decryptor(iterations, kdfBudget);
        var password = ResolvePassword((string?)root.Attribute("Protected") ?? string.Empty, crypto, masterPassword);

        var nodes = root.Elements("Node");
        if (string.Equals((string?)root.Attribute("FullFileEncryption"), "true", StringComparison.OrdinalIgnoreCase))
        {
            var inner = crypto.TryDecrypt(root.Value.Trim(), password)
                ?? throw new ImportException("The file's contents couldn't be decrypted.");
            try
            {
                using var reader = XmlReader.Create(new StringReader($"<root>{inner}</root>"), settings);
                nodes = XElement.Load(reader).Elements("Node");
            }
            catch (XmlException e)
            {
                throw new ImportException($"The file's decrypted contents aren't readable XML: {e.Message}");
            }
        }

        var topLevel = nodes.ToList();
        CheckSize(topLevel);
        return new Builder(sourceName, password, crypto).Build(topLevel.Select(e => MNode.Parse(e, null)).ToList());
    }

    /// <summary>
    /// Refuses a file with too many nodes, or folders nested deeper than Orrery keeps, before anything walks
    /// it recursively. The import lands in a folder of its own, which takes one level.
    /// </summary>
    static void CheckSize(List<XElement> topLevel)
    {
        int count = 0;
        var pending = new Stack<(XElement Element, int Depth)>(topLevel.Select(e => (e, 1)));
        while (pending.TryPop(out var item))
        {
            if (++count > MaxNodes)
            {
                throw new ImportException($"This file has more than {MaxNodes:N0} folders and connections, more than Orrery imports at once.");
            }

            bool container = string.Equals((string?)item.Element.Attribute("Type"), "Container", StringComparison.OrdinalIgnoreCase);
            if (item.Depth > ConnectionTree.MaxFolderDepth || (container && item.Depth >= ConnectionTree.MaxFolderDepth))
            {
                throw new ImportException($"This file's folders are nested too deeply. Orrery keeps at most {ConnectionTree.MaxFolderDepth} levels, "
                    + "including the folder the import goes into.");
            }

            foreach (var child in item.Element.Elements("Node"))
            {
                pending.Push((child, item.Depth + 1));
            }
        }
    }

    static string ResolvePassword(string protectedMarker, Decryptor crypto, string? masterPassword)
    {
        if (protectedMarker.Length == 0 || crypto.TryDecrypt(protectedMarker, MRemoteNgCrypto.DefaultPassword) is not null)
        {
            return MRemoteNgCrypto.DefaultPassword;
        }

        if (string.IsNullOrEmpty(masterPassword))
        {
            throw new MasterPasswordRequiredException(wrongPassword: false);
        }

        return crypto.TryDecrypt(protectedMarker, masterPassword) is not null
            ? masterPassword
            : throw new MasterPasswordRequiredException(wrongPassword: true);
    }

    /// <summary>Decrypts at the file's KDF cost, and refuses once one import has spent its budget.</summary>
    sealed class Decryptor(int iterations, long budget)
    {
        long _spent;

        public string? TryDecrypt(string value, string password) =>
            MRemoteNgCrypto.TryDecrypt(value, password, iterations, beforeKeyDerivation: () =>
            {
                if (_spent + iterations > budget)
                {
                    throw new ImportException("This file would take too long to decrypt: it holds more encrypted values than Orrery will work through at its KdfIterations setting.");
                }

                _spent += iterations;
            });
    }

    /// <summary>A report list that keeps its first lines and only counts the rest.</summary>
    internal sealed class ReportLines
    {
        readonly List<string> _lines = [];
        int _more;

        public void Add(string line)
        {
            if (_lines.Count < ImportReport.MaxListedLines) _lines.Add(line);
            else _more++;
        }

        public IReadOnlyList<string> ToList() => _more == 0 ? _lines : [.. _lines, ImportReport.More(_more)];
    }

    /// <summary>One mRemoteNG node: its attributes as written, and mRemoteNG's inheritance rules.</summary>
    sealed class MNode
    {
        readonly Dictionary<string, string> _attributes;

        MNode(XElement element, MNode? parent)
        {
            Parent = parent;
            // Settings are plain attributes. Namespace declarations and namespaced attributes can repeat a
            // setting's local name (xmlns:Name, p:Name), so they're left out.
            _attributes = element.Attributes()
                .Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.None)
                .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal);
            Children = element.Elements("Node").Select(e => Parse(e, this)).ToList();
        }

        public static MNode Parse(XElement element, MNode? parent) => new(element, parent);

        public MNode? Parent { get; }
        public List<MNode> Children { get; }
        public bool IsContainer => string.Equals(Raw("Type"), "Container", StringComparison.OrdinalIgnoreCase);
        public string Name => Raw("Name");

        public string Raw(string attribute) => _attributes.GetValueOrDefault(attribute, string.Empty);

        /// <summary>mRemoteNG's flag names don't always match the setting (Descr → InheritDescription).</summary>
        public bool Inherits(string attribute) =>
            string.Equals(Raw("Inherit" + (attribute == "Descr" ? "Description" : attribute)), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>The value mRemoteNG would use: its own, or the parent's when inheriting.</summary>
        public string Effective(string attribute) =>
            Inherits(attribute) && Parent is not null ? Parent.Effective(attribute) : Raw(attribute);

        public string Path => Parent is null ? Name : $"{Parent.Path} / {Name}";
    }

    sealed class Builder(string sourceName, string password, Decryptor crypto)
    {
        readonly ConnectionTree _tree = new(new ConnectionFolder { Name = $"Imported from mRemoteNG ({DateTime.Now:yyyy-MM-dd})" });
        readonly Dictionary<(string, string, string), Credential> _credentials = [];
        readonly HashSet<string> _credentialNames = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> _nextSuffix = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> _protocols = [];
        readonly Dictionary<string, int> _unsupported = [];
        readonly ReportLines _skipped = new();
        readonly ReportLines _warnings = new();
        int _folders, _connections, _merged;

        public ImportResult Build(List<MNode> topLevel)
        {
            foreach (var node in topLevel)
            {
                Add(node, _tree.Root);
            }

            var report = new ImportReport(sourceName, _folders, _connections, _credentials.Count, _merged,
                _protocols, _skipped.ToList(),
                _unsupported.OrderByDescending(u => u.Value).Select(u => $"{u.Key}: {u.Value} connection{(u.Value == 1 ? "" : "s")}").ToList(),
                _warnings.ToList());
            return new ImportResult(_tree.Root, _credentials.Values.ToList(), report);
        }

        void Add(MNode node, ConnectionFolder parent)
        {
            if (node.IsContainer)
            {
                var folder = new ConnectionFolder
                {
                    Name = node.Name,
                    Expanded = !string.Equals(node.Raw("Expanded"), "false", StringComparison.OrdinalIgnoreCase),
                    Notes = Blank(node.Raw("Descr")),
                    ImportSourceId = SourceId(node),
                };
                _tree.Add(folder, parent);
                _folders++;

                // Only what a child actually inherits; mRemoteNG gives folders every setting regardless.
                if (node.Children.Any(c => c.Inherits("Protocol")) && MapProtocol(node.Effective("Protocol")) is { } protocol)
                {
                    SetProtocol(folder, protocol);
                }

                if (node.Children.Any(c => c.Inherits("Port")))
                {
                    SetPort(folder, node);
                }

                if (node.Children.Any(c => c.Inherits("Username")))
                {
                    SetText(folder, node.Effective("Username"), s => s.Username, (s, v) => s.Username = v);
                }

                if (node.Children.Any(c => c.Inherits("Domain")))
                {
                    SetText(folder, node.Effective("Domain"), s => s.Domain, (s, v) => s.Domain = v);
                }

                if (node.Children.Any(c => c.Inherits("RedirectClipboard")))
                {
                    SetClipboard(folder, node);
                }

                if (node.Children.Any(c => QualityAttributes.Any(c.Inherits)))
                {
                    SetQuality(folder, node, MapProtocol(node.Effective("Protocol")) ?? Protocol.Rdp);
                }

                foreach (var child in node.Children)
                {
                    Add(child, folder);
                }

                return;
            }

            var protocolName = node.Effective("Protocol");
            if (MapProtocol(protocolName) is not { } mapped)
            {
                _skipped.Add($"{node.Path}: {protocolName} isn't supported");
                return;
            }

            var entry = new ConnectionEntry
            {
                Name = node.Name,
                Host = node.Raw("Hostname").Trim(),
                Notes = Blank(node.Effective("Descr")),
                ImportSourceId = SourceId(node),
            };
            _tree.Add(entry, parent);
            _connections++;
            _protocols[ProtocolInfo.Label(mapped)] = _protocols.GetValueOrDefault(ProtocolInfo.Label(mapped)) + 1;

            if (protocolName.Equals("SSH1", StringComparison.OrdinalIgnoreCase))
            {
                _warnings.Add($"{node.Path}: uses SSH-1, which is obsolete; imported as SSH (SSH-2).");
            }

            if (entry.Host.Length == 0)
            {
                _warnings.Add($"{node.Path}: has no host name.");
            }

            SetProtocol(entry, mapped);
            SetPort(entry, node);
            SetText(entry, node.Effective("Username"), s => s.Username, (s, v) => s.Username = v);
            SetText(entry, node.Effective("Domain"), s => s.Domain, (s, v) => s.Domain = v);
            SetClipboard(entry, node);
            SetQuality(entry, node, mapped);
            AddCredential(entry, node);
            NoteUnsupported(node, mapped);
        }

        void AddCredential(ConnectionEntry entry, MNode node)
        {
            var encrypted = node.Effective("Password");
            if (encrypted.Length == 0)
            {
                return;
            }

            if (crypto.TryDecrypt(encrypted, password) is not { Length: > 0 } secret)
            {
                _warnings.Add($"{node.Path}: the saved password couldn't be decrypted, so it wasn't imported.");
                return;
            }

            string username = node.Effective("Username").Trim(), domain = node.Effective("Domain").Trim();
            var key = (username.ToLowerInvariant(), domain.ToLowerInvariant(), secret);
            if (_credentials.TryGetValue(key, out var existing))
            {
                _merged++;
            }
            else
            {
                if (_credentials.Count >= MaxCredentials)
                {
                    throw new ImportException($"This file has more than {MaxCredentials:N0} different saved passwords, more than Orrery imports at once.");
                }

                existing = new Credential(Guid.NewGuid(), CredentialName(username, domain), CredentialKind.Password,
                    Blank(username), Blank(domain), secret, Notes: "Imported from mRemoteNG");
                _credentials[key] = existing;
            }

            entry.Settings.CredentialId = existing.Id;
        }

        string CredentialName(string username, string domain)
        {
            var baseName = username.Length == 0 ? "Imported credential" : domain.Length == 0 ? username : $"{domain}\\{username}";
            // Remembers where each base name's numbering got to, so a file of same-named credentials stays linear.
            var name = baseName;
            int n = _nextSuffix.GetValueOrDefault(baseName, 2);
            while (!_credentialNames.Add(name))
            {
                name = $"{baseName} ({n++})";
            }

            _nextSuffix[baseName] = n;
            return name;
        }

        /// <summary>Settings Orrery can't use yet that were actually turned on, counted for the report.</summary>
        void NoteUnsupported(MNode node, Protocol protocol)
        {
            if (protocol != Protocol.Rdp)
            {
                return;
            }

            void Count(string label, bool used)
            {
                if (used) _unsupported[label] = _unsupported.GetValueOrDefault(label) + 1;
            }

            bool On(string attribute) => string.Equals(node.Effective(attribute), "true", StringComparison.OrdinalIgnoreCase);
            Count("RD Gateway", node.Effective("RDGatewayUsageMethod") is { Length: > 0 } g && !g.Equals("Never", StringComparison.OrdinalIgnoreCase));
            Count("Connect to console session", On("ConnectToConsole"));
            Count("Drive redirection", On("RedirectDiskDrives"));
            Count("Printer redirection", On("RedirectPrinters"));
            Count("Smart card redirection", On("RedirectSmartCards"));
            Count("Port redirection", On("RedirectPorts"));
            Count("Remote audio", node.Effective("RedirectSound") is { Length: > 0 } s && !s.Equals("DoNotPlay", StringComparison.OrdinalIgnoreCase));
            Count("Fixed resolution", node.Effective("Resolution") is { Length: > 0 } r
                && !r.Equals("FitToWindow", StringComparison.OrdinalIgnoreCase) && !r.Equals("SmartSize", StringComparison.OrdinalIgnoreCase));
            Count("Load balance info", node.Effective("LoadBalanceInfo").Length > 0);
            Count("External tools", (node.Effective("PreExtApp") + node.Effective("PostExtApp") + node.Effective("ExtApp")).Length > 0);
        }

        void SetProtocol(ConnectionNode node, Protocol protocol)
        {
            node.Settings.Protocol = null;
            if (SettingsResolver.Resolve(node).Protocol.Value != protocol)
            {
                node.Settings.Protocol = protocol;
            }
        }

        void SetPort(ConnectionNode node, MNode source)
        {
            var text = source.Effective("Port");
            if (!int.TryParse(text, out var port) || port is < 1 or > 65535)
            {
                if (text.Length > 0)
                {
                    _warnings.Add($"{source.Path}: port \"{text}\" isn't valid; the default is used.");
                }

                return;
            }

            node.Settings.Port = null;
            if (SettingsResolver.Resolve(node).Port.Value != port)
            {
                node.Settings.Port = port;
            }
        }

        /// <summary>mRemoteNG's RedirectClipboard, stored only where it differs from what's inherited.</summary>
        static void SetClipboard(ConnectionNode node, MNode source)
        {
            if (!bool.TryParse(source.Effective("RedirectClipboard"), out var share))
            {
                return;
            }

            node.Settings.ShareClipboard = null;
            if (SettingsResolver.Resolve(node).ShareClipboard.Value != share)
            {
                node.Settings.ShareClipboard = share;
            }
        }

        /// <summary>The mRemoteNG attributes <see cref="QualityFrom"/> reads.</summary>
        static readonly string[] QualityAttributes =
            ["DisplayWallpaper", "DisplayThemes", "EnableFontSmoothing", "EnableDesktopComposition", "VNCCompression", "VNCColors"];

        /// <summary>
        /// mRemoteNG's visual-effect switches (RDP) or VNC compression and colours as a display quality: every effect
        /// on is Best, none is Fastest, a mix is Balanced. Null when there's nothing to go on, or a VNC connection
        /// doesn't ask for speed (VNC's Best and Balanced are the same).
        /// </summary>
        static DisplayQuality? QualityFrom(MNode source, Protocol protocol)
        {
            if (protocol == Protocol.Vnc)
            {
                return source.Effective("VNCCompression") is "Comp7" or "Comp8" or "Comp9" || source.Effective("VNCColors") == "Col8Bit"
                    ? DisplayQuality.Fastest
                    : null;
            }

            var effects = QualityAttributes[..4].Select(source.Effective).Where(v => bool.TryParse(v, out _)).Select(bool.Parse).ToList();
            return effects.Count == 0 ? null
                : effects.All(on => on) ? DisplayQuality.Best
                : effects.Any(on => on) ? DisplayQuality.Balanced
                : DisplayQuality.Fastest;
        }

        /// <summary>The display quality from <see cref="QualityFrom"/>, stored only where it differs from what's inherited.</summary>
        static void SetQuality(ConnectionNode node, MNode source, Protocol protocol)
        {
            if (QualityFrom(source, protocol) is not { } quality)
            {
                return;
            }

            node.Settings.DisplayQuality = null;
            if (SettingsResolver.Resolve(node).DisplayQuality.Value != quality)
            {
                node.Settings.DisplayQuality = quality;
            }
        }

        /// <summary>Stores a value only if inheriting wouldn't give the same; empty becomes an explicit "none".</summary>
        static void SetText(ConnectionNode node, string value, Func<InheritableSettings, string?> get, Action<InheritableSettings, string?> set)
        {
            var target = Blank(value);
            set(node.Settings, null);
            var inherited = get(SettingsResolverView(node));
            if (!string.Equals(inherited, target, StringComparison.Ordinal))
            {
                set(node.Settings, target ?? string.Empty);
            }
        }

        // The resolved username/domain packed back into settings, so SetText can share one getter.
        static InheritableSettings SettingsResolverView(ConnectionNode node)
        {
            var resolved = SettingsResolver.Resolve(node);
            return new InheritableSettings { Username = resolved.Username.Value, Domain = resolved.Domain.Value };
        }

        static Protocol? MapProtocol(string name) => ProtocolMap.TryGetValue(name, out var p) ? p : null;

        static string? SourceId(MNode node) => node.Raw("Id") is { Length: > 0 } id ? SourcePrefix + id : null;

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
