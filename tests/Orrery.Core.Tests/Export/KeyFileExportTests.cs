using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Xunit;

namespace Orrery.Core.Tests.Export;

/// <summary>The Key file setting in exports and imports: a path travels with Orrery's own JSON and is reported as lost in mRemoteNG's.</summary>
public class KeyFileExportTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    static readonly Dictionary<Guid, CredentialSummary> NoCredentials = [];

    static (ConnectionTree Tree, ConnectionFolder Prod, ConnectionEntry Web, ConnectionEntry Db, ConnectionEntry Plain) Sample()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Prod", Settings = { KeyFile = "~/.ssh/prod_key" } };
        var web = new ConnectionEntry { Name = "web", Host = "web.example", Settings = { Protocol = Protocol.Ssh } };
        var db = new ConnectionEntry { Name = "db", Host = "db.example", Settings = { Protocol = Protocol.Ssh, KeyFile = "/keys/db_key" } };
        var plain = new ConnectionEntry { Name = "plain", Host = "plain.example", Settings = { Protocol = Protocol.Ssh } };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        tree.Add(db, prod);
        tree.Add(plain, tree.Root);
        return (tree, prod, web, db, plain);
    }

    static ImportResult ImportOrrery(string content) => OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(content)), "export.json");

    [Fact]
    public void The_key_file_path_survives_an_orrery_json_round_trip_own_and_inherited()
    {
        var (tree, _, _, _, _) = Sample();

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, NoCredentials, Now);
        var imported = new ConnectionTree(ImportOrrery(export.Content).Root);

        Assert.Empty(export.Warnings);
        Assert.Equal("~/.ssh/prod_key", SettingsResolver.Resolve(imported.AllConnections().Single(e => e.Name == "web")).KeyFile.Value);
        Assert.Equal("/keys/db_key", SettingsResolver.Resolve(imported.AllConnections().Single(e => e.Name == "db")).KeyFile.Value);
        Assert.Null(SettingsResolver.Resolve(imported.AllConnections().Single(e => e.Name == "plain")).KeyFile.Value);
        Assert.Equal("~/.ssh/prod_key", imported.Root.Descendants().OfType<ConnectionFolder>().Single(f => f.Name == "Prod").Settings.KeyFile);
    }

    [Fact]
    public void A_connection_exported_on_its_own_keeps_the_key_file_it_took_from_its_folder()
    {
        var (_, _, web, _, _) = Sample();

        var export = ConnectionExporter.Export(web, ExportFormat.Orrery, NoCredentials, Now);
        var imported = new ConnectionTree(ImportOrrery(export.Content).Root);

        Assert.Equal("~/.ssh/prod_key", SettingsResolver.Resolve(imported.AllConnections().Single()).KeyFile.Value);
    }

    [Fact]
    public void The_path_is_in_the_file_as_a_plain_setting_and_a_sealed_export_hides_it_with_the_rest()
    {
        var (tree, _, _, _, _) = Sample();
        var credentials = new Dictionary<Guid, Credential>();

        var plain = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, NoCredentials, Now);
        var sealedExport = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, NoCredentials, Now,
            new ExportSecrets("a long export password", credentials) { Kdf = new SealedExport.KdfSettings(MemoryKiB: 8 * 1024, Iterations: 1) });

        Assert.Contains("\"keyFile\": \"/keys/db_key\"", plain.Content);
        Assert.DoesNotContain("db_key", sealedExport.Content);
        var opened = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(sealedExport.Content)), "export.json", "a long export password");
        Assert.Equal("/keys/db_key", SettingsResolver.Resolve(new ConnectionTree(opened.Root).AllConnections().Single(e => e.Name == "db")).KeyFile.Value);
    }

    [Fact]
    public void An_empty_key_file_that_turns_off_a_folders_stays_empty()
    {
        var (tree, _, web, _, _) = Sample();
        web.Settings.KeyFile = string.Empty;

        var imported = new ConnectionTree(ImportOrrery(ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, NoCredentials, Now).Content).Root);

        Assert.Null(SettingsResolver.Resolve(imported.AllConnections().Single(e => e.Name == "web")).KeyFile.Value);
    }

    [Fact]
    public void A_key_file_path_that_cant_be_one_is_left_out_on_import_with_a_warning()
    {
        var (tree, _, web, db, _) = Sample();
        web.Settings.KeyFile = "/keys/ok\nProxyCommand evil";
        db.Settings.KeyFile = new string('a', 2000);

        var imported = ImportOrrery(ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, NoCredentials, Now).Content);
        var importedTree = new ConnectionTree(imported.Root);

        Assert.Equal(2, imported.Report.Warnings.Count(w => w.Contains("key file isn't a usable path")));
        Assert.Contains(imported.Report.Warnings, w => w.StartsWith("web:"));
        Assert.Contains(imported.Report.Warnings, w => w.StartsWith("db:"));
        Assert.Null(importedTree.AllConnections().Single(e => e.Name == "web").Settings.KeyFile);
        Assert.Null(importedTree.AllConnections().Single(e => e.Name == "db").Settings.KeyFile);
        Assert.Equal("~/.ssh/prod_key", SettingsResolver.Resolve(importedTree.AllConnections().Single(e => e.Name == "web")).KeyFile.Value);
    }

    [Fact]
    public void An_mremoteng_export_reports_key_files_as_lost_by_how_many_connections_had_one()
    {
        var (tree, _, _, _, _) = Sample();

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, NoCredentials, Now);

        // web (from Prod) and db have one; plain doesn't.
        Assert.Contains(export.Warnings, w => w == "mRemoteNG has no equivalent for SSH key files: 2 connections.");
        Assert.DoesNotContain("db_key", export.Content);
        Assert.DoesNotContain("prod_key", export.Content);
    }

    [Fact]
    public void An_mremoteng_export_says_nothing_about_key_files_when_there_are_none()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "web", Host = "web.example", Settings = { Protocol = Protocol.Ssh } }, tree.Root);

        Assert.DoesNotContain(ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, NoCredentials, Now).Warnings, w => w.Contains("key file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_key_file_on_a_connection_that_isnt_ssh_isnt_reported_and_one_connection_is_singular()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "rdp", Host = "rdp.example", Settings = { Protocol = Protocol.Rdp, KeyFile = "~/k" } }, tree.Root);
        tree.Add(new ConnectionEntry { Name = "ssh", Host = "ssh.example", Settings = { Protocol = Protocol.Ssh, KeyFile = "~/k" } }, tree.Root);

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, NoCredentials, Now);

        Assert.Contains(export.Warnings, w => w == "mRemoteNG has no equivalent for SSH key files: 1 connection.");
    }

    [Fact]
    public void Acronyms_in_the_other_lost_settings_are_no_longer_lower_cased()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "ssh", Host = "ssh.example", Settings = { Protocol = Protocol.Ssh, UseSshAgent = false } }, tree.Root);

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, NoCredentials, Now);

        Assert.Contains(export.Warnings, w => w == "mRemoteNG has no equivalent for SSH agent turned off: 1 connection.");
    }
}
