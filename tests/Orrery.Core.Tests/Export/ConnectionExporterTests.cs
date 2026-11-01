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
}
