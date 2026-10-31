using System.Text;
using System.Xml.Linq;
using Orrery.Core.Connections;
using Orrery.Core.Import;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Import;

public class MRemoteNgImporterTests
{
    static readonly XNamespace Mrng = "http://mremoteng.org";

    static ImportResult ImportFixture(string name) =>
        MRemoteNgImporter.ImportFile(Path.Combine(AppContext.BaseDirectory, "Import", "Fixtures", name));

    static ImportResult Import(XDocument document, string? masterPassword = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString()));
        return MRemoteNgImporter.Import(stream, "test.xml", masterPassword);
    }

    static ConnectionEntry Entry(ImportResult result, string name) =>
        result.Root.Descendants().OfType<ConnectionEntry>().Single(e => e.Name == name);

    // ---- A real mRemoteNG 2.6 export, anonymised ----

    [Fact]
    public void Real_export_imports_every_folder_and_connection()
    {
        var result = ImportFixture("mremoteng-2.6-no-passwords.xml");

        Assert.Equal(3, result.Report.Folders);
        Assert.Equal(16, result.Report.Connections);
        Assert.Equal(5, result.Report.Protocols["RDP"]);
        Assert.Equal(10, result.Report.Protocols["SSH"]);
        Assert.Equal(1, result.Report.Protocols["TELNET"]);
        Assert.Empty(result.Credentials);
        Assert.Empty(result.Report.Skipped);
    }

    [Fact]
    public void Folders_do_not_pass_their_unused_rdp_settings_to_ssh_hosts()
    {
        var result = ImportFixture("mremoteng-2.6-no-passwords.xml");

        // Nothing in the file inherits, so the folders carry nothing...
        Assert.All(result.Root.Descendants().OfType<ConnectionFolder>(), f =>
        {
            Assert.Null(f.Settings.Protocol);
            Assert.Null(f.Settings.Port);
            Assert.Null(f.Settings.Username);
        });

        // ...and every connection resolves to exactly what mRemoteNG had.
        foreach (var entry in result.Root.Descendants().OfType<ConnectionEntry>())
        {
            var resolved = SettingsResolver.Resolve(entry);
            Assert.Equal(ProtocolInfo.DefaultPort(resolved.Protocol.Value), resolved.Port.Value);
        }
    }

    [Fact]
    public void Usernames_and_domains_carry_over_and_ids_are_kept_for_reimport()
    {
        var result = ImportFixture("mremoteng-2.6-no-passwords.xml");
        var rdp = result.Root.Descendants().OfType<ConnectionEntry>()
            .First(e => SettingsResolver.Resolve(e).Protocol.Value == Protocol.Rdp && e.Settings.Domain is not null);

        Assert.Equal("Administrator", SettingsResolver.Resolve(rdp).Username.Value);
        Assert.Equal("EXAMPLE", SettingsResolver.Resolve(rdp).Domain.Value);
        Assert.All(result.Root.Descendants(), n => Assert.StartsWith("mremoteng:", n.ImportSourceId));
    }

    [Fact]
    public void Settings_orrery_cannot_use_yet_are_reported()
    {
        var result = ImportFixture("mremoteng-2.6-no-passwords.xml");
        Assert.Contains("Connect to console session: 1 connection", result.Report.NotYetSupported);
        Assert.Contains("# mRemoteNG import", result.Report.ToMarkdown(DateTimeOffset.Now));
    }

    // ---- Generated files ----

    static XElement Node(string name, string type, params object[] content) =>
        new("Node", new XAttribute("Name", name), new XAttribute("Type", type), new XAttribute("Id", Guid.NewGuid()), content);

    static XAttribute[] Conn(string protocol, string port, string host = "host.example") =>
        [new("Protocol", protocol), new("Port", port), new("Hostname", host)];

    static XDocument File(object nodes, string password = MRemoteNgCrypto.DefaultPassword, bool fullFile = false, string version = "2.6")
    {
        var marker = MRemoteNgCrypto.Encrypt(password == MRemoteNgCrypto.DefaultPassword
            ? MRemoteNgCrypto.NotProtectedMarker : MRemoteNgCrypto.ProtectedMarker, password, 1000);

        object content = nodes;
        if (fullFile)
        {
            var inner = string.Concat(((IEnumerable<XElement>)nodes).Select(n => n.ToString()));
            content = MRemoteNgCrypto.Encrypt(inner, password, 1000);
        }

        return new XDocument(new XElement(Mrng + "Connections",
            new XAttribute(XNamespace.Xmlns + "mrng", Mrng),
            new XAttribute("Name", "Connections"),
            new XAttribute("EncryptionEngine", "AES"),
            new XAttribute("BlockCipherMode", "GCM"),
            new XAttribute("KdfIterations", "1000"),
            new XAttribute("FullFileEncryption", fullFile ? "true" : "false"),
            new XAttribute("Protected", marker),
            new XAttribute("ConfVersion", version),
            content));
    }

    static string Pw(string plaintext, string key = MRemoteNgCrypto.DefaultPassword) => MRemoteNgCrypto.Encrypt(plaintext, key, 1000);

    static XElement[] InheritingTree(string key = MRemoteNgCrypto.DefaultPassword) =>
    [
        Node("Prod", "Container",
            new XAttribute("Protocol", "RDP"), new XAttribute("Port", "3390"),
            new XAttribute("Username", "admin"), new XAttribute("Domain", "CORP"), new XAttribute("Password", Pw("Sup3r-Secret!", key)),
            Node("web-01", "Connection", Conn("RDP", "3389"),
                new XAttribute("InheritPort", "true"), new XAttribute("InheritUsername", "true"),
                new XAttribute("InheritDomain", "true"), new XAttribute("InheritPassword", "true")),
            Node("web-02", "Connection", Conn("RDP", "3389"),
                new XAttribute("InheritPort", "true"), new XAttribute("InheritUsername", "true"),
                new XAttribute("InheritDomain", "true"), new XAttribute("InheritPassword", "true")),
            Node("db-01", "Connection", Conn("SSH2", "22"),
                new XAttribute("Username", "ops"), new XAttribute("Password", Pw("other", key)))),
        Node("old-box", "Connection", Conn("RAW", "9000")),
    ];

    [Fact]
    public void Inherited_settings_land_on_the_folder_and_resolve_the_same()
    {
        var result = Import(File(InheritingTree()));
        var prod = Assert.Single(result.Root.Children.OfType<ConnectionFolder>());
        var web01 = Entry(result, "web-01");
        var db = Entry(result, "db-01");

        Assert.Equal(3390, prod.Settings.Port);
        Assert.Null(web01.Settings.Port);
        Assert.Equal(3390, SettingsResolver.Resolve(web01).Port.Value);
        Assert.Equal("CORP", SettingsResolver.Resolve(web01).Domain.Value);

        // db-01 doesn't inherit, so it overrides what the folder now provides.
        Assert.Equal(Protocol.Ssh, SettingsResolver.Resolve(db).Protocol.Value);
        Assert.Equal(22, SettingsResolver.Resolve(db).Port.Value);
        Assert.Equal("ops", SettingsResolver.Resolve(db).Username.Value);
        Assert.Null(SettingsResolver.Resolve(db).Domain.Value);
    }

    [Fact]
    public void Clipboard_sharing_is_imported_where_it_differs_from_the_default()
    {
        var result = Import(File(new[]
        {
            Node("Locked down", "Container", new XAttribute("RedirectClipboard", "false"),
                Node("inherits", "Connection", Conn("RDP", "3389"), new XAttribute("InheritRedirectClipboard", "true")),
                Node("own", "Connection", Conn("RDP", "3389"), new XAttribute("RedirectClipboard", "true"))),
            Node("plain", "Connection", Conn("RDP", "3389"), new XAttribute("RedirectClipboard", "true")),
        }));

        var folder = Assert.Single(result.Root.Children.OfType<ConnectionFolder>());
        Assert.False(folder.Settings.ShareClipboard);
        Assert.Null(Entry(result, "inherits").Settings.ShareClipboard);
        Assert.False(SettingsResolver.Resolve(Entry(result, "inherits")).ShareClipboard.Value);
        Assert.True(Entry(result, "own").Settings.ShareClipboard);
        Assert.Null(Entry(result, "plain").Settings.ShareClipboard); // Same as the default: nothing stored.
        Assert.True(SettingsResolver.Resolve(Entry(result, "plain")).ShareClipboard.Value);
    }

    [Fact]
    public void Visual_effects_and_vnc_compression_become_a_display_quality()
    {
        static XAttribute[] Effects(bool wallpaper, bool themes, bool smoothing, bool composition) =>
        [
            new("DisplayWallpaper", wallpaper), new("DisplayThemes", themes),
            new("EnableFontSmoothing", smoothing), new("EnableDesktopComposition", composition),
        ];

        var result = Import(File(new[]
        {
            Node("Slow site", "Container", Effects(false, false, false, false),
                Node("inherits", "Connection", Conn("RDP", "3389"), Effects(true, true, true, true),
                    new XAttribute("InheritDisplayWallpaper", "true"), new XAttribute("InheritDisplayThemes", "true"),
                    new XAttribute("InheritEnableFontSmoothing", "true"), new XAttribute("InheritEnableDesktopComposition", "true")),
                Node("everything on", "Connection", Conn("RDP", "3389"), Effects(true, true, true, true))),
            Node("mixed", "Connection", Conn("RDP", "3389"), Effects(true, true, true, false)),
            Node("no effects listed", "Connection", Conn("RDP", "3389")),
            Node("squeezed vnc", "Connection", Conn("VNC", "5900"), new XAttribute("VNCCompression", "Comp9")),
            Node("plain vnc", "Connection", Conn("VNC", "5900"), new XAttribute("VNCCompression", "CompNone")),
        }));

        DisplayQuality Quality(string name) => SettingsResolver.Resolve(Entry(result, name)).DisplayQuality.Value;
        var folder = Assert.Single(result.Root.Children.OfType<ConnectionFolder>());
        Assert.Equal(DisplayQuality.Fastest, folder.Settings.DisplayQuality);
        Assert.Null(Entry(result, "inherits").Settings.DisplayQuality);
        Assert.Equal(DisplayQuality.Fastest, Quality("inherits"));
        Assert.Equal(DisplayQuality.Best, Quality("everything on"));
        Assert.Null(Entry(result, "mixed").Settings.DisplayQuality); // Balanced is the default: nothing stored.
        Assert.Equal(DisplayQuality.Balanced, Quality("mixed"));
        Assert.Null(Entry(result, "no effects listed").Settings.DisplayQuality);
        Assert.Equal(DisplayQuality.Fastest, Quality("squeezed vnc"));
        Assert.Equal(DisplayQuality.Balanced, Quality("plain vnc"));
    }

    [Fact]
    public void Passwords_become_saved_credentials_with_duplicates_merged()
    {
        var result = Import(File(InheritingTree()));

        Assert.Equal(2, result.Credentials.Count);
        Assert.Equal(1, result.Report.PasswordsMerged);
        var shared = result.Credentials.Single(c => c.Secret == "Sup3r-Secret!");
        Assert.Equal("CORP\\admin", shared.Name);
        Assert.Equal(shared.Id, Entry(result, "web-01").Settings.CredentialId);
        Assert.Equal(shared.Id, Entry(result, "web-02").Settings.CredentialId);
        Assert.DoesNotContain("Sup3r-Secret!", result.Report.ToMarkdown(DateTimeOffset.Now));
    }

    [Fact]
    public void Unsupported_protocols_are_skipped_and_listed()
    {
        var result = Import(File(InheritingTree()));
        Assert.Contains(result.Report.Skipped, s => s.Contains("old-box") && s.Contains("RAW"));
        Assert.Equal(3, result.Report.Connections);
    }

    [Fact]
    public void Master_password_is_required_and_checked()
    {
        var file = File(InheritingTree("hunter22"), password: "hunter22");

        Assert.False(Assert.Throws<MasterPasswordRequiredException>(() => Import(file)).WrongPassword);
        Assert.True(Assert.Throws<MasterPasswordRequiredException>(() => Import(file, "nope")).WrongPassword);
        Assert.Equal(2, Import(file, "hunter22").Credentials.Count);
    }

    [Fact]
    public void Full_file_encryption_is_supported()
    {
        var result = Import(File(InheritingTree(), fullFile: true));
        Assert.Equal(3, result.Report.Connections);
    }

    [Fact]
    public void Older_formats_and_other_files_are_refused_with_a_reason()
    {
        Assert.Contains("older mRemoteNG", Assert.Throws<ImportException>(() => Import(File(InheritingTree(), version: "2.5"))).Message);
        Assert.Contains("isn't an mRemoteNG", Assert.Throws<ImportException>(() => Import(new XDocument(new XElement("Other")))).Message);
    }

    [Fact]
    public void Reimporting_skips_what_is_already_there_and_its_credentials()
    {
        var file = File(InheritingTree());
        var tree = ConnectionTree.Empty();
        tree.AddFrom(Import(file).Root, tree.Root, includeSourceFolder: true);

        var again = Import(file);
        Assert.Equal(3, again.CountAlreadyIn(tree));

        var filtered = again.WithoutAlreadyIn(tree);
        Assert.Empty(filtered.Root.Descendants());
        Assert.Empty(filtered.Credentials);
    }

    [Fact]
    public void Adding_an_import_at_top_level_moves_its_contents_and_notifies_once()
    {
        var tree = ConnectionTree.Empty();
        int changes = 0;
        tree.Changed += (_, _) => changes++;

        tree.AddFrom(Import(File(InheritingTree())).Root, tree.Root, includeSourceFolder: false);

        Assert.Equal(1, changes);
        Assert.Equal("Prod", Assert.Single(tree.Root.Children).Name);
        Assert.Equal(3390, SettingsResolver.Resolve(tree.AllConnections().First(e => e.Name == "web-01")).Port.Value);
    }

    [Fact]
    public void Dtds_are_rejected()
    {
        const string evil = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY a \"aaaa\">]><x>&a;</x>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(evil));
        Assert.Throws<ImportException>(() => MRemoteNgImporter.Import(stream, "evil.xml"));
    }

    // ---- Hostile files: each fails with ImportException and nothing else ----

    /// <summary>A file whose root says what it's told; no password marker means mRemoteNG's default key.</summary>
    static XDocument Crafted(object content, string kdfIterations = "1000", bool fullFile = false)
    {
        var file = File(Array.Empty<XElement>());
        file.Root!.SetAttributeValue("KdfIterations", kdfIterations);
        file.Root.SetAttributeValue("Protected", "");
        file.Root.SetAttributeValue("FullFileEncryption", fullFile ? "true" : "false");
        file.Root.Add(content);
        return file;
    }

    static XElement Nested(int containers)
    {
        var node = Node("leaf", "Connection", Conn("RDP", "3389"));
        for (int i = containers; i > 0; i--)
        {
            node = Node($"level {i}", "Container", node);
        }

        return node;
    }

    public static TheoryData<XAttribute> NamespacedNames() => new()
    {
        new XAttribute(XNamespace.Xmlns + "Name", "urn:x"),
        new XAttribute(XNamespace.Get("urn:x") + "Name", "other"),
    };

    [Theory]
    [MemberData(nameof(NamespacedNames))]
    public void A_namespaced_attribute_with_a_setting_name_is_ignored(XAttribute extra)
    {
        var result = Import(Crafted(Node("n", "Connection", Conn("RDP", "3389"), extra)));
        Assert.Equal("n", Assert.Single(result.Root.Children).Name);
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY a \"aaaa\">]><Node Name=\"&a;\" Type=\"Connection\" />")]
    [InlineData("<Node Name=\"unclosed\" Type=\"Connection\"")]
    public void An_encrypted_body_that_is_not_readable_xml_is_refused(string body)
    {
        var file = Crafted(MRemoteNgCrypto.Encrypt(body, MRemoteNgCrypto.DefaultPassword, 1000), fullFile: true);
        Assert.Throws<ImportException>(() => Import(file));
    }

    [Fact]
    public void Folders_nested_deeper_than_orrery_allows_are_refused()
    {
        Assert.Throws<ImportException>(() => Import(Crafted(Nested(61))));
    }

    [Fact]
    public void Folders_nested_to_the_limit_still_import()
    {
        var result = Import(Crafted(Nested(59)));
        Assert.Equal(59, result.Report.Folders);
        Assert.Equal(1, result.Report.Connections);
    }

    [Fact]
    public void Thousands_of_nested_folders_are_refused_before_anything_recurses()
    {
        Assert.Throws<ImportException>(() => Import(Crafted(Nested(2_000))));
    }

    [Fact]
    public void A_file_with_too_many_nodes_is_refused()
    {
        var nodes = Enumerable.Range(0, 21_000).Select(i => new XElement("Node",
            new XAttribute("Name", $"n{i}"), new XAttribute("Type", "Connection"), new XAttribute("Protocol", "RDP"), new XAttribute("Hostname", "h")));
        Assert.Throws<ImportException>(() => Import(Crafted(nodes)));
    }

    [Fact]
    public void A_key_derivation_cost_beyond_any_real_file_is_refused()
    {
        Assert.Throws<ImportException>(() => Import(Crafted(Node("n", "Connection", Conn("RDP", "3389")), kdfIterations: "2147483647")));
    }

    [Fact]
    public void A_file_with_too_many_distinct_passwords_is_refused()
    {
        var nodes = Enumerable.Range(0, 5_001).Select(i => Node($"n{i}", "Connection", Conn("RDP", "3389"),
            new XAttribute("Username", $"user{i}"), new XAttribute("Password", MRemoteNgCrypto.Encrypt($"pw{i}", MRemoteNgCrypto.DefaultPassword, 1))));
        Assert.Throws<ImportException>(() => Import(Crafted(nodes, kdfIterations: "1")));
    }

    [Fact]
    public void Long_report_lists_keep_the_first_500_lines_and_count_the_rest()
    {
        var nodes = Enumerable.Range(0, 600).Select(i => Node($"raw{i}", "Connection", Conn("RAW", "9000")));
        var skipped = Import(Crafted(nodes)).Report.Skipped;

        Assert.Equal(501, skipped.Count);
        Assert.Contains("raw499", skipped[499]);
        Assert.Contains("100 more", skipped[500]);
    }

    [Fact]
    public void Credentials_with_the_same_name_get_numbered_without_clashing()
    {
        var result = Import(Crafted(new[]
        {
            Node("a", "Connection", Conn("RDP", "3389"), new XAttribute("Username", "u"), new XAttribute("Password", Pw("one"))),
            Node("b", "Connection", Conn("RDP", "3389"), new XAttribute("Username", "u (2)"), new XAttribute("Password", Pw("two"))),
            Node("c", "Connection", Conn("RDP", "3389"), new XAttribute("Username", "u"), new XAttribute("Password", Pw("three"))),
            Node("d", "Connection", Conn("RDP", "3389"), new XAttribute("Username", "u"), new XAttribute("Password", Pw("four"))),
        }));

        Assert.Equal(["u", "u (2)", "u (3)", "u (4)"], result.Credentials.Select(c => c.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Decrypting_stops_once_the_whole_import_would_cost_too_much()
    {
        var nodes = Enumerable.Range(0, 20).Select(i => Node($"n{i}", "Connection", Conn("RDP", "3389"),
            new XAttribute("Username", $"user{i}"), new XAttribute("Password", Pw($"pw{i}"))));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Crafted(nodes).ToString()));

        // Twenty passwords at 1000 rounds each need 20,000; allow half.
        var e = Assert.Throws<ImportException>(() => MRemoteNgImporter.Import(stream, "test.xml", masterPassword: null, kdfBudget: 10_000));
        Assert.Contains("too long to decrypt", e.Message);
        Assert.True(MRemoteNgImporter.MaxTotalKdfIterations >= 4_000L * 50_000);
    }
}
