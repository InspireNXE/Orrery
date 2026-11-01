using Orrery.Core.Connections;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

public class WorkspaceStoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    string WorkspacePath => Path.Combine(_root, WorkspaceStore.FileName);

    [Fact]
    public void Saves_and_loads_the_tabs_in_order_with_the_active_one_and_the_window()
    {
        var saved = Guid.NewGuid();
        var store = new WorkspaceStore(WorkspacePath);
        store.Save(new Workspace
        {
            Window = new WindowPlacement(-1200, 40, 1440.5, 900, Maximized: true),
            Tabs = [new WorkspaceTab(saved, null, 0, Protocol.Ssh, Files: true), new WorkspaceTab(null, "jump.example", 2222, Protocol.Vnc)],
            Active = 1,
        });

        var loaded = store.Load();

        Assert.Equal(new WindowPlacement(-1200, 40, 1440.5, 900, true), loaded.Window);
        Assert.Equal([new WorkspaceTab(saved, null, 0, Protocol.Ssh, true), new WorkspaceTab(null, "jump.example", 2222, Protocol.Vnc)], loaded.Tabs);
        Assert.Equal(1, loaded.Active);
        Assert.Contains("\"ssh\"", File.ReadAllText(WorkspacePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"tabs\": \"nope\"}")]
    [InlineData("{\"tabs\": [{\"host\": \"a\", \"port\": 22, \"protocol\": \"carrierPigeon\"}]}")]
    [InlineData("{\"tabs\": [{\"host\": \"a\", \"port\": \"http\", \"protocol\": \"ssh\"}]}")]
    public void A_missing_or_corrupt_file_is_an_empty_workspace(string? contents)
    {
        if (contents is not null)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(WorkspacePath, contents);
        }

        var loaded = new WorkspaceStore(WorkspacePath).Load();

        Assert.Empty(loaded.Tabs);
        Assert.Null(loaded.Window);
        Assert.Equal(-1, loaded.Active);
    }

    [Fact]
    public void Entries_that_make_no_sense_are_dropped_and_the_rest_kept()
    {
        Directory.CreateDirectory(_root);
        var id = Guid.NewGuid();
        File.WriteAllText(WorkspacePath, $$"""
            {
              "window": { "x": 10, "y": 10, "width": -5, "height": 900, "maximized": false },
              "tabs": [
                null,
                { "protocol": "ssh" },
                { "host": "no-port", "port": 0, "protocol": "rdp" },
                { "host": "ok.example", "port": 22, "protocol": "ssh" },
                { "entryId": "{{id}}", "host": "ignored", "port": 99, "protocol": "rdp" },
                { "host": "fake", "port": 22, "protocol": 17 }
              ],
              "active": 4
            }
            """);

        var loaded = new WorkspaceStore(WorkspacePath).Load();

        Assert.Null(loaded.Window);
        Assert.Equal([new WorkspaceTab(null, "ok.example", 22, Protocol.Ssh), new WorkspaceTab(id, null, 0, Protocol.Rdp)], loaded.Tabs);
        Assert.Equal(1, loaded.Active);
    }

    [Fact]
    public void An_enormous_list_is_cut_off()
    {
        var store = new WorkspaceStore(WorkspacePath);
        store.Save(new Workspace { Tabs = [.. Enumerable.Range(0, 500).Select(i => new WorkspaceTab(null, $"h{i}", 22, Protocol.Ssh))], Active = 499 });

        var loaded = store.Load();

        Assert.Equal(Workspace.MaxTabs, loaded.Tabs.Count);
        Assert.Equal(-1, loaded.Active);
    }

    [Fact]
    public void Surviving_drops_deleted_connections_and_keeps_the_active_tab_on_the_same_one()
    {
        var tree = ConnectionTree.Empty();
        var (a, b, c) = (new ConnectionEntry { Name = "a", Host = "a.example" }, new ConnectionEntry { Name = "b", Host = "b.example" }, new ConnectionEntry { Name = "c", Host = "c.example" });
        foreach (var entry in new[] { a, b, c })
        {
            tree.Add(entry, tree.Root);
        }

        var workspace = new Workspace
        {
            Tabs =
            [
                new WorkspaceTab(a.Id, null, 0, Protocol.Rdp),
                new WorkspaceTab(Guid.NewGuid(), null, 0, Protocol.Rdp),
                new WorkspaceTab(null, "typed.example", 22, Protocol.Ssh),
                new WorkspaceTab(c.Id, null, 0, Protocol.Rdp),
            ],
            Active = 3,
        };

        var surviving = workspace.Surviving(tree);

        Assert.Equal([a.Id, null, c.Id], surviving.Tabs.Select(t => t.EntryId));
        Assert.Equal(2, surviving.Active);
    }

    [Theory]
    [InlineData(1, 0)] // the active one was deleted: the one before it
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void Surviving_moves_the_active_tab_off_a_deleted_one(int active, int expected)
    {
        var tree = ConnectionTree.Empty();
        var kept = new ConnectionEntry { Name = "kept", Host = "kept.example" };
        tree.Add(kept, tree.Root);
        var workspace = new Workspace
        {
            Tabs = [new WorkspaceTab(kept.Id, null, 0, Protocol.Rdp), new WorkspaceTab(Guid.NewGuid(), null, 0, Protocol.Rdp)],
            Active = active,
        };

        Assert.Equal(expected, workspace.Surviving(tree).Active);
    }

    [Fact]
    public void Surviving_skips_a_folder_a_connection_with_no_host_and_a_typed_tab_with_none()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "folder" };
        var blank = new ConnectionEntry { Name = "blank", Host = " " };
        tree.Add(folder, tree.Root);
        tree.Add(blank, tree.Root);

        var workspace = new Workspace
        {
            Tabs = [new WorkspaceTab(folder.Id, null, 0, Protocol.Rdp), new WorkspaceTab(blank.Id, null, 0, Protocol.Rdp), new WorkspaceTab(null, "", 22, Protocol.Ssh)],
            Active = 0,
        };

        var surviving = workspace.Surviving(tree);

        Assert.Empty(surviving.Tabs);
        Assert.Equal(-1, surviving.Active);
    }

    [Fact]
    public void A_remembered_tab_holds_no_login()
    {
        // The shape is the guarantee: an address and a protocol, nothing that could carry a password or key.
        Assert.Equal(["EntryId", "Files", "Host", "Port", "Protocol"], typeof(WorkspaceTab).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));

        new WorkspaceStore(WorkspacePath).Save(new Workspace { Tabs = [new WorkspaceTab(null, "box.example", 22, Protocol.Ssh)] });

        var text = File.ReadAllText(WorkspacePath);
        Assert.DoesNotContain("assword", text);
        Assert.DoesNotContain("ecret", text);
        Assert.DoesNotContain("sername", text);
    }

    [Fact]
    public void The_file_lives_in_the_data_folder_and_moves_with_it()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));

        WorkspaceStore.In(location).Save(new Workspace { Tabs = [new WorkspaceTab(null, "box.example", 22, Protocol.Ssh)] });

        Assert.True(File.Exists(location.FileIn("workspace.json")));
        Assert.True(DataFolderFiles.IsCopied(WorkspaceStore.FileName));
        Assert.True(DataFolderFiles.IsDeleted(WorkspaceStore.FileName));
    }

    [Fact]
    public void Restoring_sessions_asks_unless_the_setting_says_otherwise()
    {
        var store = new AppSettingsStore(Path.Combine(_root, AppSettingsStore.FileName));
        Assert.Equal(RestoreSessionsMode.Ask, store.Load().RestoreMode);

        store.Save(new AppSettings { RestoreSessions = RestoreSessionsMode.Never });
        Assert.Equal(RestoreSessionsMode.Never, store.Load().RestoreMode);
        Assert.Contains("\"never\"", File.ReadAllText(Path.Combine(_root, AppSettingsStore.FileName)));

        store.Save(new AppSettings { RestoreSessions = RestoreSessionsMode.Always });
        Assert.Equal(RestoreSessionsMode.Always, store.Load().RestoreMode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
