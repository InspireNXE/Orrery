using System.Text;
using System.Text.Json.Nodes;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Xunit;

namespace Orrery.Core.Tests.Export;

/// <summary>Exports that include saved passwords and keys, encrypted with a password chosen for the export.</summary>
public class SecretExportTests
{
    const string Password = "correct horse battery";
    static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    static readonly Credential Admin = new(Guid.NewGuid(), "Admin", CredentialKind.Password, "admin", "CORP", "s3cret!", Notes: "rotated monthly");
    static readonly Credential Deploy = new(Guid.NewGuid(), "Deploy key", CredentialKind.SshKeyWithPassphrase, "deploy", null,
        "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----", Passphrase: "phrase");

    /// <summary>Prod (Admin) / web, db (Deploy, its own); other at the top level with no credential.</summary>
    static (ConnectionTree Tree, ConnectionFolder Prod, ConnectionEntry Web, ConnectionEntry Db) Sample()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Prod", Settings = { CredentialId = Admin.Id } };
        var web = new ConnectionEntry { Name = "web", Host = "web.example" };
        var db = new ConnectionEntry { Name = "db", Host = "db.internal", Settings = { Protocol = Protocol.Ssh, CredentialId = Deploy.Id } };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        tree.Add(db, prod);
        tree.Add(new ConnectionEntry { Name = "other", Host = "other.example" }, tree.Root);
        return (tree, prod, web, db);
    }

    static Dictionary<Guid, CredentialSummary> Summaries => new() { [Admin.Id] = Admin.Summary, [Deploy.Id] = Deploy.Summary };

    // Cheap Argon2id, so the tests stay fast; real exports use the vault's cost.
    static ExportSecrets Secrets => new(Password, new Dictionary<Guid, Credential> { [Admin.Id] = Admin, [Deploy.Id] = Deploy })
    {
        Kdf = new SealedExport.KdfSettings(MemoryKiB: 8 * 1024, Iterations: 1),
    };

    static ImportResult ImportOrrery(string content, string? password) =>
        OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(content)), "export.json", password);

    [Fact]
    public void An_orrery_export_with_secrets_is_encrypted_as_a_whole()
    {
        var (tree, _, _, _) = Sample();

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, Summaries, Now, Secrets);

        Assert.Equal(2, export.Secrets);
        foreach (var plain in new[] { "web.example", "db.internal", "Prod", "s3cret!", "phrase", "admin" })
        {
            Assert.DoesNotContain(plain, export.Content);
        }

        Assert.Equal(SealedExport.FormatName, JsonNode.Parse(export.Content)!["format"]!.GetValue<string>());
    }

    [Fact]
    public void Importing_it_asks_for_the_password_and_says_when_it_is_wrong()
    {
        var (tree, _, _, _) = Sample();
        var content = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, Summaries, Now, Secrets).Content;

        Assert.False(Assert.Throws<MasterPasswordRequiredException>(() => ImportOrrery(content, null)).WrongPassword);
        Assert.True(Assert.Throws<MasterPasswordRequiredException>(() => ImportOrrery(content, "wrong password")).WrongPassword);
    }

    [Fact]
    public void With_the_password_the_credentials_come_back_with_new_ids_and_connections_use_them()
    {
        var (tree, _, _, _) = Sample();
        var content = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, Summaries, Now, Secrets).Content;

        var imported = ImportOrrery(content, Password);

        Assert.Equal(2, imported.Credentials.Count);
        Assert.Equal(2, imported.Report.CredentialsCreated);
        var admin = Assert.Single(imported.Credentials, c => c.Name == "Admin");
        var deploy = Assert.Single(imported.Credentials, c => c.Name == "Deploy key");
        Assert.NotEqual(Admin.Id, admin.Id);
        Assert.Equal(Admin with { Id = admin.Id }, admin);
        Assert.Equal(Deploy with { Id = deploy.Id }, deploy);

        var importedTree = new ConnectionTree(imported.Root);
        var prod = importedTree.Root.Descendants().OfType<ConnectionFolder>().Single(f => f.Name == "Prod");
        Assert.Equal(admin.Id, prod.Settings.CredentialId);
        var entries = importedTree.AllConnections().ToDictionary(e => e.Name);
        Assert.Equal(admin.Id, SettingsResolver.Resolve(entries["web"]).CredentialId.Value);
        Assert.Equal(deploy.Id, entries["db"].Settings.CredentialId);
        Assert.Null(SettingsResolver.Resolve(entries["other"]).CredentialId.Value);
    }

    [Fact]
    public void Only_the_credentials_used_by_what_is_exported_go_in()
    {
        var (_, _, web, _) = Sample();

        var export = ConnectionExporter.Export(web, ExportFormat.Orrery, Summaries, Now, Secrets);

        Assert.Equal(1, export.Secrets);
        var imported = ImportOrrery(export.Content, Password);
        var admin = Assert.Single(imported.Credentials);
        Assert.Equal("Admin", admin.Name);
        Assert.Equal(admin.Id, imported.Root.Descendants().OfType<ConnectionEntry>().Single().Settings.CredentialId);
    }

    [Fact]
    public void Without_secrets_the_orrery_export_stays_plain_and_carries_none()
    {
        var (tree, _, _, _) = Sample();

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, Summaries, Now);

        Assert.Contains("web.example", export.Content);
        Assert.DoesNotContain("s3cret!", export.Content);
        Assert.DoesNotContain("\"credentials\"", export.Content);
        Assert.Empty(ImportOrrery(export.Content, null).Credentials);
    }

    [Fact]
    public void Editing_the_key_settings_in_the_file_stops_it_opening()
    {
        var (tree, _, _, _) = Sample();
        var file = JsonNode.Parse(ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, Summaries, Now, Secrets).Content)!;

        file["kdf"]!["iterations"] = 2;
        Assert.True(Assert.Throws<MasterPasswordRequiredException>(() => ImportOrrery(file.ToJsonString(), Password)).WrongPassword);

        // Costs far beyond what Orrery writes aren't attempted at all.
        file["kdf"]!["memoryKiB"] = 64 * 1024 * 1024;
        Assert.Throws<ImportException>(() => ImportOrrery(file.ToJsonString(), Password));
    }

    [Fact]
    public void An_mremoteng_export_with_secrets_needs_its_master_password_and_holds_the_passwords()
    {
        var (tree, _, _, _) = Sample();

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, Summaries, Now, Secrets);

        Assert.Equal(1, export.Secrets);
        Assert.Contains(export.Warnings, w => w.Contains("SSH keys"));
        Assert.DoesNotContain("s3cret!", export.Content);
        Assert.Contains("KdfIterations=\"10000\"", export.Content);

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(export.Content)))
        {
            Assert.False(Assert.Throws<MasterPasswordRequiredException>(() => MRemoteNgImporter.Import(stream, "confCons.xml")).WrongPassword);
        }

        using var again = new MemoryStream(Encoding.UTF8.GetBytes(export.Content));
        var imported = MRemoteNgImporter.Import(again, "confCons.xml", Password);
        var credential = Assert.Single(imported.Credentials);
        Assert.Equal(("admin", "CORP", "s3cret!"), (credential.Username, credential.Domain, credential.Secret));
        var web = imported.Root.Descendants().OfType<ConnectionEntry>().Single(e => e.Name == "web");
        Assert.Equal(credential.Id, web.Settings.CredentialId);
    }

    [Fact]
    public void Without_secrets_the_mremoteng_export_opens_without_a_master_password()
    {
        var (tree, _, _, _) = Sample();

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, Summaries, Now);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(export.Content));
        Assert.Empty(MRemoteNgImporter.Import(stream, "confCons.xml").Credentials);
    }

    [Fact]
    public void A_csv_export_never_takes_secrets()
    {
        var (tree, _, _, _) = Sample();

        Assert.Throws<ArgumentException>(() => ConnectionExporter.Export(tree.Root, ExportFormat.Csv, Summaries, Now, Secrets));
    }

    [Fact]
    public void The_export_password_stays_out_of_logs()
    {
        Assert.DoesNotContain(Password, Secrets.ToString());
    }
}
