using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Export;
using Orrery.Core.Import;
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
