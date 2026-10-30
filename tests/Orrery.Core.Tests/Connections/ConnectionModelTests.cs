using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

public class ConnectionModelTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    // Production (credential, domain) / Web (port 3390) / web-01
    static (ConnectionTree Tree, ConnectionFolder Production, ConnectionFolder Web, ConnectionEntry Web01) Sample()
    {
        var tree = ConnectionTree.Empty();
        var production = new ConnectionFolder { Name = "Production" };
        production.Settings.CredentialId = Guid.NewGuid();
        production.Settings.Domain = "corp";
        var web = new ConnectionFolder { Name = "Web" };
        web.Settings.Port = 3390;
        var web01 = new ConnectionEntry { Name = "web-01", Host = "web-01.corp.local" };

        tree.Add(production, tree.Root);
        tree.Add(web, production);
        tree.Add(web01, web);
        return (tree, production, web, web01);
    }

    [Fact]
    public void Settings_inherit_from_the_nearest_folder_that_sets_them()
    {
        var (_, production, web, web01) = Sample();
        var resolved = SettingsResolver.Resolve(web01);

        Assert.Equal(3390, resolved.Port.Value);
        Assert.Same(web, resolved.Port.Source);
        Assert.Equal(production.Settings.CredentialId, resolved.CredentialId.Value);
        Assert.Same(production, resolved.CredentialId.Source);
        Assert.Equal("corp", resolved.Domain.Value);
    }

    [Fact]
    public void A_connection_overrides_what_it_would_inherit()
    {
        var (_, _, _, web01) = Sample();
        web01.Settings.Port = 4000;

        var resolved = SettingsResolver.Resolve(web01);
        Assert.Equal(4000, resolved.Port.Value);
        Assert.Same(web01, resolved.Port.Source);
        Assert.Equal(3390, SettingsResolver.ResolveInherited(web01).Port.Value);
    }

    [Fact]
    public void Defaults_apply_when_nothing_sets_a_value_and_port_follows_protocol()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = "box" };
        tree.Add(entry, tree.Root);

        Assert.Equal(Protocol.Rdp, SettingsResolver.Resolve(entry).Protocol.Value);
        Assert.Equal(3389, SettingsResolver.Resolve(entry).Port.Value);
        Assert.True(SettingsResolver.Resolve(entry).Port.IsDefault);

        entry.Settings.Protocol = Protocol.Ssh;
        Assert.Equal(22, SettingsResolver.Resolve(entry).Port.Value);
    }

    [Fact]
    public void Explicit_none_overrides_an_inherited_credential_and_domain()
    {
        var (_, _, _, web01) = Sample();
        web01.Settings.CredentialId = InheritableSettings.NoCredential;
        web01.Settings.Domain = string.Empty;

        var resolved = SettingsResolver.Resolve(web01);
        Assert.Null(resolved.CredentialId.Value);
        Assert.Same(web01, resolved.CredentialId.Source);
        Assert.Null(resolved.Domain.Value);
    }

    [Fact]
    public void Moving_changes_parent_and_what_is_inherited()
    {
        var (tree, production, _, web01) = Sample();
        tree.Move(web01, production, 0);

        Assert.Same(production, web01.Parent);
        Assert.Same(web01, production.Children[0]);
        Assert.Equal(3389, SettingsResolver.Resolve(web01).Port.Value);
        Assert.Equal("Production", web01.Path);
    }

    [Fact]
    public void Moving_down_within_the_same_folder_lands_where_expected()
    {
        var tree = ConnectionTree.Empty();
        var a = new ConnectionEntry { Name = "a" };
        var b = new ConnectionEntry { Name = "b" };
        var c = new ConnectionEntry { Name = "c" };
        tree.Add(a, tree.Root);
        tree.Add(b, tree.Root);
        tree.Add(c, tree.Root);

        // "Drop before c" is index 2 before removal.
        tree.Move(a, tree.Root, 2);
        Assert.Equal(["b", "a", "c"], tree.Root.Children.Select(n => n.Name));
    }

    [Fact]
    public void A_folder_cannot_move_into_its_own_subfolder()
    {
        var (tree, production, web, _) = Sample();
        Assert.Throws<InvalidOperationException>(() => tree.Move(production, web));
        Assert.Throws<InvalidOperationException>(() => tree.Move(production, production));
    }

    [Fact]
    public void Changes_raise_the_changed_event()
    {
        var (tree, _, _, web01) = Sample();
        int changes = 0;
        tree.Changed += (_, _) => changes++;

        tree.Move(web01, tree.Root);
        tree.Remove(web01);
        tree.NotifyChanged();
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Store_round_trips_the_tree_and_relinks_parents()
    {
        var (tree, production, _, web01) = Sample();
        web01.Notes = "Front end";
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"), Path.Combine(_dir, "backups"));
        store.Save(tree);

        var loaded = store.Load();
        var loadedEntry = Assert.IsType<ConnectionEntry>(loaded.Find(web01.Id));
        Assert.Equal("web-01.corp.local", loadedEntry.Host);
        Assert.Equal("Front end", loadedEntry.Notes);
        Assert.Equal("Production / Web", loadedEntry.Path);
        Assert.Equal(production.Settings.CredentialId, SettingsResolver.Resolve(loadedEntry).CredentialId.Value);
    }

    [Fact]
    public void Older_algorithms_are_off_by_default_and_follow_the_nearest_folder_that_sets_them()
    {
        var (_, production, _, web01) = Sample();
        var resolved = SettingsResolver.Resolve(web01);
        Assert.False(resolved.AllowLegacySsh.Value);
        Assert.True(resolved.AllowLegacySsh.IsDefault);

        production.Settings.AllowLegacySsh = true;
        resolved = SettingsResolver.Resolve(web01);
        Assert.True(resolved.AllowLegacySsh.Value);
        Assert.Same(production, resolved.AllowLegacySsh.Source);

        web01.Settings.AllowLegacySsh = false;
        Assert.False(SettingsResolver.Resolve(web01).AllowLegacySsh.Value);
        Assert.True(SettingsResolver.ResolveInherited(web01).AllowLegacySsh.Value);
    }

    [Fact]
    public void A_connections_file_saved_before_the_older_algorithms_setting_loads_with_it_off_and_keeps_it_once_set()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "connections.json");
        File.WriteAllText(path, """
            {"schemaVersion":1,"root":{"type":"folder","name":"Connections","id":"00000000-0000-0000-0000-000000000001","children":[
              {"type":"connection","name":"old","id":"00000000-0000-0000-0000-000000000002","host":"old.example","settings":{"protocol":"ssh","useSshAgent":false}}]}}
            """);
        var store = new ConnectionStore(path, Path.Combine(_dir, "backups"));

        var tree = store.Load();
        var entry = Assert.IsType<ConnectionEntry>(tree.Root.Children.Single());
        Assert.Null(entry.Settings.AllowLegacySsh);
        Assert.False(SettingsResolver.Resolve(entry).AllowLegacySsh.Value);
        Assert.False(entry.Settings.UseSshAgent);

        entry.Settings.AllowLegacySsh = true;
        store.Save(tree);
        Assert.Contains("\"allowLegacySsh\": true", File.ReadAllText(path));
        Assert.True(SettingsResolver.Resolve(store.Load().Find(entry.Id)!).AllowLegacySsh.Value);
    }

    [Fact]
    public void Store_round_trips_folders_nested_60_deep()
    {
        var tree = ConnectionTree.Empty();
        var parent = tree.Root;
        for (int i = 1; i <= 60; i++)
        {
            var folder = new ConnectionFolder { Name = $"level {i}" };
            tree.Add(folder, parent);
            parent = folder;
        }

        var leaf = new ConnectionEntry { Name = "leaf", Host = "deep.example" };
        tree.Add(leaf, parent);
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"), Path.Combine(_dir, "backups"));
        store.Save(tree);

        var loaded = Assert.IsType<ConnectionEntry>(store.Load().Find(leaf.Id));
        Assert.Equal("deep.example", loaded.Host);
        Assert.Equal(60, loaded.Ancestors().Count(a => a.Parent is not null));
    }

    [Fact]
    public void Folders_can_be_placed_down_to_the_depth_limit_and_no_further()
    {
        var tree = ConnectionTree.Empty();
        var parent = tree.Root;
        for (int i = 1; i < ConnectionTree.MaxFolderDepth; i++)
        {
            var folder = new ConnectionFolder { Name = $"level {i}" };
            tree.Add(folder, parent);
            parent = folder;
        }

        Assert.Equal(0, ConnectionTree.DepthOf(tree.Root));
        Assert.Equal(ConnectionTree.MaxFolderDepth - 1, ConnectionTree.DepthOf(parent));
        Assert.True(ConnectionTree.CanPlace(new ConnectionFolder(), parent));
        Assert.True(ConnectionTree.CanPlace(new ConnectionEntry(), parent));

        // A folder with a subfolder needs two levels, and there's only one left.
        var pair = new ConnectionFolder { Name = "outer" };
        tree.Add(pair, tree.Root);
        tree.Add(new ConnectionFolder { Name = "inner" }, pair);
        Assert.False(ConnectionTree.CanPlace(pair, parent));

        var last = new ConnectionFolder { Name = "last" };
        tree.Add(last, parent);
        Assert.False(ConnectionTree.CanPlace(new ConnectionFolder(), last));
        Assert.True(ConnectionTree.CanPlace(new ConnectionEntry(), last));
    }

    [Fact]
    public void Missing_file_loads_an_empty_tree()
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"), Path.Combine(_dir, "backups"));
        Assert.Empty(store.Load().Root.Children);
    }

    [Fact]
    public void Saves_keep_a_bounded_number_of_backups()
    {
        var (tree, _, _, _) = Sample();
        var backups = Path.Combine(_dir, "backups");
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"), backups, backupsToKeep: 3, backupInterval: TimeSpan.Zero);

        for (int i = 0; i < 6; i++)
        {
            store.Save(tree);
            Thread.Sleep(5); // Backup names carry milliseconds.
        }

        Assert.Equal(3, Directory.GetFiles(backups).Length);
    }

    [Fact]
    public void Frequent_saves_make_one_backup_per_interval()
    {
        var (tree, _, _, _) = Sample();
        var backups = Path.Combine(_dir, "backups");
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"), backups);

        for (int i = 0; i < 5; i++)
        {
            store.Save(tree);
        }

        Assert.Single(Directory.GetFiles(backups));
    }

    [Theory]
    [InlineData("web-01", "web-01", 3389)]
    [InlineData("web-01:3390", "web-01", 3390)]
    [InlineData("10.0.0.5", "10.0.0.5", 3389)]
    [InlineData("[fe80::1]:4000", "fe80::1", 4000)]
    [InlineData("fe80::1", "fe80::1", 3389)]
    public void Host_input_parses_host_and_port(string input, string host, int port)
    {
        Assert.True(HostAddress.TryParse(input, 3389, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("web-01:0")]
    [InlineData("web-01:99999")]
    [InlineData("[fe80::1")]
    public void Bad_host_input_is_rejected(string input)
    {
        Assert.False(HostAddress.TryParse(input, 3389, out _, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Duplicate_copies_a_folder_deeply_right_after_the_original()
    {
        var tree = ConnectionTree.Empty();
        var credential = Guid.NewGuid();
        var folder = new ConnectionFolder { Name = "Prod", Settings = new InheritableSettings { Port = 3390, CredentialId = credential } };
        var web = new ConnectionEntry { Name = "web", Host = "web.example", Notes = "front", ImportSourceId = "mremoteng:1" };
        var after = new ConnectionEntry { Name = "after" };
        tree.Add(folder, tree.Root);
        tree.Add(after, tree.Root);
        tree.Add(web, folder);

        var copy = Assert.IsType<ConnectionFolder>(tree.Duplicate(folder));

        Assert.Equal(["Prod", "Prod (copy)", "after"], tree.Root.Children.Select(c => c.Name));
        Assert.NotEqual(folder.Id, copy.Id);
        Assert.Equal(3390, copy.Settings.Port);
        Assert.Equal(credential, copy.Settings.CredentialId);

        var webCopy = Assert.IsType<ConnectionEntry>(Assert.Single(copy.Children));
        Assert.Equal(("web", "web.example", "front"), (webCopy.Name, webCopy.Host, webCopy.Notes));
        Assert.NotEqual(web.Id, webCopy.Id);
        Assert.Null(webCopy.ImportSourceId);
        Assert.Same(copy, webCopy.Parent);
        Assert.Equal(3390, SettingsResolver.Resolve(webCopy).Port.Value);

        // The copy's settings are its own.
        copy.Settings.Port = 4000;
        Assert.Equal(3390, folder.Settings.Port);
    }
}
