using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Export;

public class ConnectionExporterTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    static readonly CredentialSummary Admin = new(Guid.NewGuid(), "Admin", CredentialKind.Password, "admin", "CORP", null);

    /// <summary>Defaults on the root, a folder with a saved credential, a jump host and a forward inside it, and one outside.</summary>
    static (ConnectionTree Tree, ConnectionFolder Prod, ConnectionEntry Bastion, ConnectionEntry Db, ConnectionEntry Other) Sample()
    {
        var tree = ConnectionTree.Empty();
        tree.Root.Settings.Port = 3390;
        var prod = new ConnectionFolder { Name = "Prod", Settings = { CredentialId = Admin.Id } };
        var bastion = new ConnectionEntry { Name = "bastion", Host = "bastion.example", Settings = { Protocol = Protocol.Ssh, Port = 22 } };
        var db = new ConnectionEntry
        {
            Name = "db",
            Host = "db.internal",
            Settings = { Protocol = Protocol.Ssh, Port = 22, JumpHostId = bastion.Id },
            PortForwards = [new PortForward(ForwardKind.Local, 5432, "localhost", 5432)],
        };
        var web = new ConnectionEntry { Name = "web", Host = "web.example" };
        var other = new ConnectionEntry { Name = "other", Host = "other.example" };
        tree.Add(prod, tree.Root);
        tree.Add(bastion, prod);
        tree.Add(db, prod);
        tree.Add(web, prod);
        tree.Add(other, tree.Root);
        return (tree, prod, bastion, db, other);
    }

    static Dictionary<Guid, CredentialSummary> Credentials => new() { [Admin.Id] = Admin };

    static ImportResult ImportOrrery(string content) =>
        OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(content)), "export.json");

    [Fact]
    public void An_exported_folder_imports_back_with_its_settings_and_new_ids()
    {
        var (tree, prod, bastion, _, _) = Sample();

        var export = ConnectionExporter.Export(prod, ExportFormat.Orrery, Credentials, Now);
        Assert.Equal(3, export.Connections);
        Assert.Empty(export.Warnings);
        Assert.DoesNotContain(Admin.Id.ToString(), export.Content);

        var imported = ImportOrrery(export.Content);
        Assert.Empty(imported.Credentials);
        var folder = Assert.IsType<ConnectionFolder>(Assert.Single(imported.Root.Children));
        Assert.Equal("Prod", folder.Name);
        Assert.NotEqual(prod.Id, folder.Id);

        var importedTree = new ConnectionTree(imported.Root);
        var web = importedTree.AllConnections().Single(e => e.Name == "web");
        var resolved = SettingsResolver.Resolve(web);
        Assert.Equal(3390, resolved.Port.Value); // from the root's defaults, carried by the exported folder
        Assert.Equal("admin", resolved.Username.Value); // from the saved credential, without the credential
        Assert.Equal("CORP", resolved.Domain.Value);
        Assert.Null(resolved.CredentialId.Value);

        var db = importedTree.AllConnections().Single(e => e.Name == "db");
        var newBastion = importedTree.AllConnections().Single(e => e.Name == "bastion");
        Assert.NotEqual(bastion.Id, newBastion.Id);
        Assert.Equal(newBastion.Id, db.Settings.JumpHostId);
        Assert.Equal(5432, Assert.Single(db.PortForwards!).ListenPort);
    }

    [Fact]
    public void An_export_writes_color_correction_with_the_american_key_and_one_with_the_british_key_still_imports()
    {
        var (_, prod, _, _, _) = Sample();
        prod.Settings.ColorCorrection = false;

        var export = ConnectionExporter.Export(prod, ExportFormat.Orrery, Credentials, Now);
        Assert.Contains("\"colorCorrection\": false", export.Content);
        Assert.DoesNotContain("colour", export.Content, StringComparison.OrdinalIgnoreCase);

        // As builds before the switch to American spelling wrote it.
        var british = export.Content.Replace("\"colorCorrection\"", "\"colourCorrection\"", StringComparison.Ordinal);
        Assert.Contains("\"colourCorrection\": false", british);
        var folder = Assert.IsType<ConnectionFolder>(Assert.Single(ImportOrrery(british).Root.Children));
        Assert.False(folder.Settings.ColorCorrection);
        var web = new ConnectionTree(ImportOrrery(british).Root).AllConnections().Single(e => e.Name == "web");
        Assert.False(SettingsResolver.Resolve(web).ColorCorrection.Value);
    }

    [Fact]
    public void Importing_an_export_into_the_tree_it_came_from_finds_what_is_already_there()
    {
        var (tree, prod, _, _, _) = Sample();
        var imported = ImportOrrery(ConnectionExporter.Export(prod, ExportFormat.Orrery, Credentials, Now).Content);

        Assert.Equal(3, imported.CountAlreadyIn(tree));
        Assert.Empty(imported.WithoutAlreadyIn(tree).Root.Children);
        Assert.Equal(0, imported.CountAlreadyIn(ConnectionTree.Empty()));
    }

    [Fact]
    public void Everything_exported_keeps_the_defaults_and_merging_at_the_top_level_keeps_them_too()
    {
        var (tree, _, _, _, _) = Sample();
        var imported = ImportOrrery(ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, Credentials, Now).Content);
        Assert.Equal(3390, imported.Root.Settings.Port);

        var target = ConnectionTree.Empty();
        target.AddFrom(imported.Root, target.Root, includeSourceFolder: false);
        Assert.Null(target.Root.Settings.Port);
        Assert.Equal(3390, SettingsResolver.Resolve(target.AllConnections().Single(e => e.Name == "other")).Port.Value);
    }

    [Fact]
    public void A_jump_host_outside_the_export_is_left_out_and_reported()
    {
        var (_, _, _, db, _) = Sample();

        var export = ConnectionExporter.Export(db, ExportFormat.Orrery, Credentials, Now);

        Assert.Contains(export.Warnings, w => w.Contains("jump host"));
        var entry = new ConnectionTree(ImportOrrery(export.Content).Root).AllConnections().Single();
        Assert.Null(SettingsResolver.Resolve(entry).JumpHostId.Value);
        Assert.Equal("admin", SettingsResolver.Resolve(entry).Username.Value);
    }

    [Fact]
    public void With_the_vault_locked_usernames_from_saved_credentials_are_left_out_and_reported()
    {
        var (_, prod, _, _, _) = Sample();

        var export = ConnectionExporter.Export(prod, ExportFormat.Orrery, credentials: null, Now);

        Assert.Contains(export.Warnings, w => w.Contains("vault was locked"));
        Assert.DoesNotContain("admin", export.Content);
    }

    [Fact]
    public void Display_quality_survives_an_mremoteng_round_trip()
    {
        var tree = ConnectionTree.Empty();
        foreach (var (name, protocol, quality) in new (string, Protocol, DisplayQuality?)[]
        {
            ("default", Protocol.Rdp, null), ("best", Protocol.Rdp, DisplayQuality.Best), ("fast", Protocol.Rdp, DisplayQuality.Fastest),
            ("fast vnc", Protocol.Vnc, DisplayQuality.Fastest),
        })
        {
            tree.Add(new ConnectionEntry { Name = name, Host = $"{name}.example", Settings = { Protocol = protocol, DisplayQuality = quality } }, tree.Root);
        }

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, Credentials, Now);
        var imported = new ConnectionTree(MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "confCons.xml").Root);

        DisplayQuality Quality(string name) => SettingsResolver.Resolve(imported.AllConnections().Single(e => e.Name == name)).DisplayQuality.Value;
        Assert.Equal(DisplayQuality.Balanced, Quality("default"));
        Assert.Equal(DisplayQuality.Best, Quality("best"));
        Assert.Equal(DisplayQuality.Fastest, Quality("fast"));
        Assert.Equal(DisplayQuality.Fastest, Quality("fast vnc"));
    }

    [Fact]
    public void An_mremoteng_export_imports_back_into_orrery()
    {
        var (tree, _, _, _, _) = Sample();

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, Credentials, Now);
        Assert.Contains(export.Warnings, w => w.Contains("jump hosts") && w.Contains("1 connection"));
        Assert.Contains(export.Warnings, w => w.Contains("port forwards"));

        var imported = MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "confCons.xml");
        Assert.Empty(imported.Report.Warnings);
        Assert.Empty(imported.Credentials);
        var importedTree = new ConnectionTree(imported.Root);
        var db = SettingsResolver.Resolve(importedTree.AllConnections().Single(e => e.Name == "db"));
        Assert.Equal(Protocol.Ssh, db.Protocol.Value);
        Assert.Equal(22, db.Port.Value);
        Assert.Equal("admin", db.Username.Value);
        var other = SettingsResolver.Resolve(importedTree.AllConnections().Single(e => e.Name == "other"));
        Assert.Equal(Protocol.Rdp, other.Protocol.Value);
        Assert.Equal(3390, other.Port.Value);
        Assert.Equal("Prod", Assert.IsType<ConnectionFolder>(importedTree.Root.Children[0]).Name);
    }

    [Fact]
    public void A_csv_export_has_a_row_per_connection_and_defuses_formulas()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Site, A" };
        tree.Add(folder, tree.Root);
        tree.Add(new ConnectionEntry { Name = "=HYPERLINK(\"x\")", Host = "h1", Notes = "two\nlines" }, folder);

        var lines = ConnectionExporter.Export(tree.Root, ExportFormat.Csv, Credentials, Now).Content.Split("\r\n");

        Assert.Equal("Folder,Name,Host,Protocol,Port,Username,Domain,Notes", lines[0]);
        Assert.Equal("\"Site, A\",\"'=HYPERLINK(\"\"x\"\")\",h1,RDP,3389,,,\"two\nlines\"", lines[1]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"format\":\"something-else\",\"schemaVersion\":1,\"root\":{\"type\":\"folder\",\"name\":\"x\"}}")]
    [InlineData("{\"format\":\"orrery-connections\",\"schemaVersion\":99,\"root\":{\"type\":\"folder\",\"name\":\"x\"}}")]
    [InlineData("{\"format\":\"orrery-connections\",\"schemaVersion\":1,\"root\":{\"type\":\"folder\",\"children\":[{\"name\":\"no type\"}]}}")]
    [InlineData("not json")]
    public void Files_that_are_not_an_orrery_export_are_refused(string content)
    {
        Assert.Throws<ImportException>(() => ImportOrrery(content));
    }

    [Fact]
    public void A_file_that_lists_the_same_item_twice_is_refused()
    {
        var id = Guid.NewGuid();
        var content = $"{{\"format\":\"orrery-connections\",\"schemaVersion\":1,\"root\":{{\"type\":\"folder\",\"children\":["
            + $"{{\"type\":\"connection\",\"id\":\"{id}\"}},{{\"type\":\"connection\",\"id\":\"{id}\"}}]}}}}";

        Assert.Throws<ImportException>(() => ImportOrrery(content));
    }

    /// <summary>Lab / hv-01 (clipboard off, user root) with guests vm-a and vm-jump; vm-a goes through vm-jump.</summary>
    static (ConnectionTree Tree, ConnectionFolder Lab, ConnectionEntry Host, ConnectionEntry VmA, ConnectionEntry VmJump) HostSample()
    {
        var tree = ConnectionTree.Empty();
        var lab = new ConnectionFolder { Name = "Lab" };
        var host = new ConnectionEntry { Name = "hv-01", Host = "hv-01.lab", Settings = { ShareClipboard = false, Username = "root" }, GuestsCollapsed = true };
        var vmJump = new ConnectionEntry { Name = "vm-jump", Host = "vm-jump.lab", Settings = { Protocol = Protocol.Ssh } };
        var vmA = new ConnectionEntry { Name = "vm-a", Host = "vm-a.lab", Settings = { Protocol = Protocol.Ssh, JumpHostId = vmJump.Id } };
        tree.Add(lab, tree.Root);
        tree.Add(host, lab);
        tree.AddGuest(vmA, host);
        tree.AddGuest(vmJump, host);
        return (tree, lab, host, vmA, vmJump);
    }

    [Fact]
    public void Hosts_and_their_guests_round_trip_through_an_orrery_export()
    {
        var (tree, lab, host, _, _) = HostSample();

        var export = ConnectionExporter.Export(lab, ExportFormat.Orrery, Credentials, Now);
        Assert.Contains("\"schemaVersion\": 2", export.Content);
        Assert.Equal(3, export.Connections);
        Assert.Contains("\"schemaVersion\": 1", ConnectionExporter.Export(Sample().Tree.Root, ExportFormat.Orrery, Credentials, Now).Content);

        var imported = ImportOrrery(export.Content);
        // Linked as it comes back, before it's added anywhere.
        var newLab = Assert.IsType<ConnectionFolder>(Assert.Single(imported.Root.Children));
        var newHost = Assert.IsType<ConnectionEntry>(Assert.Single(newLab.Children));
        Assert.Same(newLab, newHost.Parent);
        Assert.NotEqual(host.Id, newHost.Id);
        Assert.True(newHost.GuestsCollapsed);
        Assert.Equal(["vm-a", "vm-jump"], newHost.Guests.Select(g => g.Name));
        Assert.All(newHost.Guests, g => Assert.Same(newHost, g.GuestOf));
        Assert.All(newHost.Guests, g => Assert.Same(newLab, g.Parent));
        var newVmA = newHost.Guests[0];
        Assert.Equal(newHost.Guests[1].Id, newVmA.Settings.JumpHostId);
        Assert.False(SettingsResolver.Resolve(newVmA).ShareClipboard.Value);
        Assert.Equal(new Dictionary<string, int> { ["RDP"] = 1, ["SSH"] = 2 }, imported.Report.Protocols);

        Assert.Equal(3, imported.CountAlreadyIn(tree));
    }

    [Fact]
    public void A_guest_exported_on_its_own_is_a_plain_connection_with_what_it_took_from_its_host()
    {
        var (_, _, _, vmA, _) = HostSample();

        var export = ConnectionExporter.Export(vmA, ExportFormat.Orrery, Credentials, Now);
        Assert.Contains("\"schemaVersion\": 1", export.Content);
        Assert.Contains(export.Warnings, w => w.Contains("jump host"));

        var entry = Assert.IsType<ConnectionEntry>(Assert.Single(ImportOrrery(export.Content).Root.Children));
        Assert.Null(entry.GuestOf);
        Assert.False(entry.Settings.ShareClipboard);
        Assert.Null(SettingsResolver.Resolve(entry).Username.Value);
    }

    [Fact]
    public void A_host_exported_on_its_own_gives_its_guests_what_they_took_from_its_folders()
    {
        var (tree, lab, host, _, _) = HostSample();
        tree.Root.Settings.Domain = "CORP";
        lab.Settings.Username = "labuser";

        var orrery = new ConnectionTree(ImportOrrery(ConnectionExporter.Export(host, ExportFormat.Orrery, Credentials, Now).Content).Root);
        var vmA = SettingsResolver.Resolve(orrery.AllConnections().Single(e => e.Name == "vm-a"));
        Assert.Equal(("labuser", "CORP"), (vmA.Username.Value, vmA.Domain.Value));
        Assert.Equal("root", SettingsResolver.Resolve(orrery.AllConnections().Single(e => e.Name == "hv-01")).Username.Value);

        var xml = ConnectionExporter.Export(host, ExportFormat.MRemoteNg, Credentials, Now).Content;
        var mremoteng = new ConnectionTree(MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "confCons.xml").Root);
        Assert.Equal("labuser", SettingsResolver.Resolve(mremoteng.AllConnections().Single(e => e.Name == "vm-a")).Username.Value);

        var lines = ConnectionExporter.Export(host, ExportFormat.Csv, Credentials, Now).Content.Split("\r\n");
        Assert.Contains(lines, l => l.StartsWith("hv-01,vm-a,", StringComparison.Ordinal) && l.Contains(",labuser,CORP,"));
    }

    [Fact]
    public void Skipping_what_is_already_there_keeps_a_hosts_new_guests_with_the_host()
    {
        var (tree, lab, host, vmA, _) = HostSample();
        var vmNew = new ConnectionEntry { Name = "vm-new", Host = "vm-new.lab" };
        tree.AddGuest(vmNew, host);
        var imported = ImportOrrery(ConnectionExporter.Export(lab, ExportFormat.Orrery, Credentials, Now).Content);
        tree.Remove(vmNew);

        // hv-01 comes in again, holding only what's new, so it isn't counted as already there.
        Assert.Equal(2, imported.CountAlreadyIn(tree));
        var filtered = imported.WithoutAlreadyIn(tree);

        var newLab = Assert.IsType<ConnectionFolder>(Assert.Single(filtered.Root.Children));
        var newHost = Assert.IsType<ConnectionEntry>(Assert.Single(newLab.Children));
        Assert.Equal("hv-01", newHost.Name);
        Assert.Equal(["vm-new"], newHost.Guests.Select(g => g.Name));
        Assert.DoesNotContain(filtered.Root.Descendants(), n => n.Name == vmA.Name);
    }

    [Fact]
    public void Skipping_a_jump_host_already_there_points_the_import_roots_default_at_the_one_in_the_tree()
    {
        var (tree, _, bastion, _, _) = Sample();
        tree.Root.Settings.JumpHostId = bastion.Id;
        var imported = ImportOrrery(ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, Credentials, Now).Content);
        Assert.NotEqual(bastion.Id, imported.Root.Settings.JumpHostId);

        var filtered = imported.WithoutAlreadyIn(tree);

        Assert.Equal(bastion.Id, filtered.Root.Settings.JumpHostId);
    }

    [Theory]
    [InlineData("\"protocol\":99")]
    [InlineData("\"displayQuality\":9")]
    [InlineData("\"remoteAudio\":5")]
    [InlineData("\"scaling\":7")]
    [InlineData("\"parity\":42")]
    [InlineData("\"stopBits\":-1")]
    [InlineData("\"flowControl\":3")]
    public void A_setting_number_orrery_does_not_know_is_refused(string setting)
    {
        var content = $$$"""
            {"format":"orrery-connections","schemaVersion":3,"root":{"type":"folder","children":[
              {"type":"connection","name":"hv","host":"hv.lab","guests":[{"type":"connection","name":"vm","settings":{{{{setting}}}}}]}]}}
            """;

        Assert.Throws<ImportException>(() => ImportOrrery(content));
    }

    [Fact]
    public void A_port_forward_kind_orrery_does_not_know_is_refused()
    {
        var content = """
            {"format":"orrery-connections","schemaVersion":1,"root":{"type":"folder","children":[
              {"type":"connection","name":"db","host":"db.lab","portForwards":[{"kind":7,"listenPort":5432}]}]}}
            """;

        Assert.Throws<ImportException>(() => ImportOrrery(content));
    }

    [Fact]
    public void An_mremoteng_export_writes_a_host_as_a_folder_holding_the_host_and_its_guests()
    {
        var (tree, _, _, _, _) = HostSample();

        var first = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, Credentials, Now);
        Assert.Contains(first.Warnings, w => w.Contains("1 host was written as a folder"));
        Assert.Equal(System.Text.RegularExpressions.Regex.Matches(first.Content, "Id=\"[^\"]+\"").Select(m => m.Value),
            System.Text.RegularExpressions.Regex.Matches(ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, Credentials, Now).Content, "Id=\"[^\"]+\"").Select(m => m.Value));

        var imported = new ConnectionTree(MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(first.Content)), "confCons.xml").Root);
        var lab = Assert.IsType<ConnectionFolder>(Assert.Single(imported.Root.Children));
        var hostFolder = Assert.IsType<ConnectionFolder>(Assert.Single(lab.Children));
        Assert.Equal("hv-01", hostFolder.Name);
        Assert.False(hostFolder.Expanded);
        Assert.Equal(["hv-01", "vm-a", "vm-jump"], hostFolder.Children.Select(c => c.Name));
        Assert.Equal("hv-01.lab", ((ConnectionEntry)hostFolder.Children[0]).Host);
        Assert.False(SettingsResolver.Resolve(hostFolder.Children[1]).ShareClipboard.Value);
    }

    [Fact]
    public void A_csv_export_lists_guests_with_their_host_in_the_folder_column()
    {
        var (tree, _, _, _, _) = HostSample();

        var lines = ConnectionExporter.Export(tree.Root, ExportFormat.Csv, Credentials, Now).Content.Split("\r\n");

        Assert.StartsWith("Lab,hv-01,", lines[1]);
        Assert.StartsWith("Lab / hv-01,vm-a,", lines[2]);
    }

    [Fact]
    public void An_orrery_file_with_a_guest_that_has_guests_is_refused()
    {
        var content = """
            {"format":"orrery-connections","schemaVersion":2,"root":{"type":"folder","children":[
              {"type":"connection","name":"hv","guests":[{"name":"vm","guests":[{"name":"nested"}]}]}]}}
            """;

        Assert.Throws<ImportException>(() => ImportOrrery(content));
    }

    [Fact]
    public void Filling_from_a_parent_covers_every_setting()
    {
        var parent = new InheritableSettings();
        foreach (var property in typeof(InheritableSettings).GetProperties().Where(p => p.CanWrite))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            property.SetValue(parent, type == typeof(string) ? "x" : Activator.CreateInstance(type));
        }

        var child = new InheritableSettings();
        child.FillFrom(parent);

        Assert.All(typeof(InheritableSettings).GetProperties().Where(p => p.CanWrite), p => Assert.NotNull(p.GetValue(child)));
    }

    [Fact]
    public void Protocol_settings_carry_over_to_mremoteng_and_back()
    {
        var tree = ConnectionTree.Empty();
        var rdp = new ConnectionEntry
        {
            Name = "rdp", Host = "rdp.example",
            Settings = { Protocol = Protocol.Rdp, RemoteAudio = RemoteAudio.DoNotPlay, ShareAllDrives = true, AdminSession = true },
        };
        var vnc = new ConnectionEntry { Name = "vnc", Host = "vnc.example", Settings = { Protocol = Protocol.Vnc, ViewOnly = true, Scaling = DesktopScaling.ActualSize } };
        var web = new ConnectionEntry { Name = "web", Host = "intranet.example", Settings = { Protocol = Protocol.Https } };
        foreach (var entry in new[] { rdp, vnc, web })
        {
            tree.Add(entry, tree.Root);
        }

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, Credentials, Now);
        var imported = new ConnectionTree(MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "confCons.xml").Root);

        ResolvedSettings Resolved(string name) => SettingsResolver.Resolve(imported.AllConnections().Single(e => e.Name == name));
        Assert.Equal(RemoteAudio.DoNotPlay, Resolved("rdp").RemoteAudio.Value);
        Assert.True(Resolved("rdp").ShareAllDrives.Value);
        Assert.True(Resolved("rdp").AdminSession.Value);
        Assert.True(Resolved("vnc").ViewOnly.Value);
        Assert.Equal(DesktopScaling.ActualSize, Resolved("vnc").Scaling.Value);
        Assert.Equal(Protocol.Https, Resolved("web").Protocol.Value);
        Assert.Equal(443, Resolved("web").Port.Value);
    }

    [Fact]
    public void Mremoteng_is_told_what_it_cannot_hold()
    {
        var tree = ConnectionTree.Empty();
        var lab = new ConnectionFolder { Name = "Lab", Settings = { SocksProxy = "1080", KeepAliveSeconds = 5 } };
        tree.Add(lab, tree.Root);
        tree.Add(new ConnectionEntry { Name = "console", Host = "COM3", Settings = { Protocol = Protocol.Serial } }, lab);
        tree.Add(new ConnectionEntry { Name = "ssh", Host = "ssh.example", Settings = { Protocol = Protocol.Ssh } }, lab);
        tree.Add(new ConnectionEntry { Name = "rdp", Host = "rdp.example", Settings = { SharedFolders = ["C:\\Work"], UseAllMonitors = true } }, lab);

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, Credentials, Now);
        Assert.Contains(export.Warnings, w => w.Contains("no serial connections") && w.Contains("1 was"));
        Assert.Contains(export.Warnings, w => w.Contains("SOCKS proxies"));
        Assert.Contains(export.Warnings, w => w.Contains("SSH keep-alive intervals"));
        Assert.Contains(export.Warnings, w => w.Contains("shared folders"));
        Assert.Contains(export.Warnings, w => w.Contains("full screen over every display"));
        Assert.DoesNotContain("COM3", export.Content);
        Assert.Equal(2, export.Connections);
    }

    [Fact]
    public void An_orrery_export_keeps_serial_and_web_connections_and_says_schema_3()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry
        {
            Name = "console", Host = "/dev/ttyUSB0",
            Settings = { Protocol = Protocol.Serial, BaudRate = 115200, Parity = SerialParity.Odd, FlowControl = SerialFlowControl.XOnXOff },
        }, tree.Root);
        tree.Add(new ConnectionEntry { Name = "web", Host = "router.lan/admin", Settings = { Protocol = Protocol.Http, Port = 8080 } }, tree.Root);

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, Credentials, Now);
        Assert.Contains("\"schemaVersion\": 3", export.Content);
        var imported = new ConnectionTree(ImportOrrery(export.Content).Root);
        var console = SettingsResolver.Resolve(imported.AllConnections().Single(e => e.Name == "console"));
        Assert.Equal(Protocol.Serial, console.Protocol.Value);
        Assert.Equal(115200, console.BaudRate.Value);
        Assert.Equal(SerialParity.Odd, console.Parity.Value);
        Assert.Equal(SerialFlowControl.XOnXOff, console.FlowControl.Value);
        var web = imported.AllConnections().Single(e => e.Name == "web");
        Assert.Equal("router.lan/admin", web.Host);
        Assert.Equal(8080, SettingsResolver.Resolve(web).Port.Value);
    }
}
