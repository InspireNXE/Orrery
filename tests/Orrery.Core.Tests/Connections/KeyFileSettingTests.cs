using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

/// <summary>The SSH Key file setting: inherited like the others, kept as a path, written to connections.json.</summary>
public class KeyFileSettingTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    static (ConnectionTree Tree, ConnectionFolder Prod, ConnectionFolder Lab, ConnectionEntry Web, ConnectionEntry Db) Sample()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Prod" };
        var lab = new ConnectionFolder { Name = "Lab" };
        var web = new ConnectionEntry { Name = "web", Host = "web.example", Settings = { Protocol = Protocol.Ssh } };
        var db = new ConnectionEntry { Name = "db", Host = "db.example", Settings = { Protocol = Protocol.Ssh } };
        tree.Add(prod, tree.Root);
        tree.Add(lab, prod);
        tree.Add(web, prod);
        tree.Add(db, lab);
        return (tree, prod, lab, web, db);
    }

    [Fact]
    public void Nothing_set_means_no_key_file()
    {
        var (_, _, _, web, _) = Sample();

        var resolved = SettingsResolver.Resolve(web).KeyFile;

        Assert.Null(resolved.Value);
        Assert.True(resolved.IsDefault);
    }

    [Fact]
    public void A_connections_own_key_file_is_used()
    {
        var (_, _, _, web, _) = Sample();
        web.Settings.KeyFile = "~/.ssh/web_key";

        var resolved = SettingsResolver.Resolve(web).KeyFile;

        Assert.Equal("~/.ssh/web_key", resolved.Value);
        Assert.Same(web, resolved.Source);
    }

    [Fact]
    public void A_folders_key_file_is_inherited_down_through_nested_folders()
    {
        var (tree, prod, _, web, db) = Sample();
        prod.Settings.KeyFile = "~/.ssh/prod_key";

        Assert.Equal("~/.ssh/prod_key", SettingsResolver.Resolve(web).KeyFile.Value);
        Assert.Equal("~/.ssh/prod_key", SettingsResolver.Resolve(db).KeyFile.Value);
        Assert.Same(prod, SettingsResolver.Resolve(db).KeyFile.Source);

        tree.Root.Settings.KeyFile = "~/.ssh/default_key";
        Assert.Equal("~/.ssh/prod_key", SettingsResolver.Resolve(db).KeyFile.Value);
    }

    [Fact]
    public void The_nearest_setting_wins()
    {
        var (_, prod, lab, _, db) = Sample();
        prod.Settings.KeyFile = "~/.ssh/prod_key";
        lab.Settings.KeyFile = "~/.ssh/lab_key";

        Assert.Equal("~/.ssh/lab_key", SettingsResolver.Resolve(db).KeyFile.Value);

        db.Settings.KeyFile = "~/.ssh/db_key";
        Assert.Equal("~/.ssh/db_key", SettingsResolver.Resolve(db).KeyFile.Value);
    }

    [Fact]
    public void An_empty_key_file_means_none_and_overrides_the_folders()
    {
        var (_, prod, _, web, _) = Sample();
        prod.Settings.KeyFile = "~/.ssh/prod_key";
        web.Settings.KeyFile = string.Empty;

        var resolved = SettingsResolver.Resolve(web).KeyFile;

        Assert.Null(resolved.Value);
        Assert.Same(web, resolved.Source);
    }

    [Fact]
    public void What_a_node_would_inherit_leaves_out_its_own_value()
    {
        var (_, prod, _, web, _) = Sample();
        prod.Settings.KeyFile = "~/.ssh/prod_key";
        web.Settings.KeyFile = "~/.ssh/web_key";

        var inherited = SettingsResolver.ResolveInherited(web).KeyFile;

        Assert.Equal("~/.ssh/prod_key", inherited.Value);
        Assert.Same(prod, inherited.Source);
    }

    [Fact]
    public void A_guest_takes_its_key_file_from_its_folders_not_its_host()
    {
        var (tree, prod, _, web, _) = Sample();
        var guest = new ConnectionEntry { Name = "vm", Host = "vm.example", Settings = { Protocol = Protocol.Ssh } };
        tree.AddGuest(guest, web);
        web.Settings.KeyFile = "~/.ssh/host_key";
        prod.Settings.KeyFile = "~/.ssh/prod_key";

        Assert.Equal("~/.ssh/prod_key", SettingsResolver.Resolve(guest).KeyFile.Value);
        Assert.Contains(nameof(InheritableSettings.KeyFile), InheritableSettings.KeptByGuests);
    }

    [Fact]
    public void Settings_taken_from_a_folder_when_a_node_is_lifted_out_include_the_key_file()
    {
        var own = new InheritableSettings();
        own.FillFrom(new InheritableSettings { KeyFile = "~/.ssh/prod_key" });

        Assert.Equal("~/.ssh/prod_key", own.KeyFile);
        Assert.Equal("~/.ssh/prod_key", own.Clone().KeyFile);
    }

    [Fact]
    public void The_key_file_path_is_saved_and_loaded_with_the_connections_and_is_left_out_when_unset()
    {
        var (tree, prod, _, web, db) = Sample();
        prod.Settings.KeyFile = "~/.ssh/prod_key";
        web.Settings.KeyFile = string.Empty;
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "connections.json");
        var store = new ConnectionStore(path, Path.Combine(_dir, "backups"));

        store.Save(tree);
        var text = File.ReadAllText(path);
        var loaded = store.Load();

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "\"keyFile\"").Count);
        Assert.Equal("~/.ssh/prod_key", SettingsResolver.Resolve(loaded.Find(db.Id)!).KeyFile.Value);
        Assert.Null(SettingsResolver.Resolve(loaded.Find(web.Id)!).KeyFile.Value);
        Assert.Equal(string.Empty, loaded.Find(web.Id)!.Settings.KeyFile);
    }

    [Fact]
    public void A_connections_file_without_the_setting_still_loads_with_none()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "connections.json");
        File.WriteAllText(path, """{"schemaVersion":1,"root":{"type":"folder","id":"11111111-1111-1111-1111-111111111111","name":"","children":[{"type":"connection","id":"22222222-2222-2222-2222-222222222222","name":"old","host":"old.example","settings":{"protocol":"ssh"}}]}}""");

        var tree = new ConnectionStore(path, Path.Combine(_dir, "backups")).Load();

        Assert.Null(SettingsResolver.Resolve(tree.AllConnections().Single()).KeyFile.Value);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
