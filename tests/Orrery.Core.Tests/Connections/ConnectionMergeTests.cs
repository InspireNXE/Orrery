using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

/// <summary>
/// Two computers sharing a data folder through a sync app: each has its own store and tree on the same file, as each
/// computer's Orrery would.
/// </summary>
public class ConnectionMergeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("orrery-merge-").FullName;

    string FilePath => Path.Combine(_dir, "connections.json");

    ConnectionStore Store() => new(FilePath, Path.Combine(_dir, "backups"));

    (ConnectionStore Store, ConnectionTree Tree) Open()
    {
        var store = Store();
        return (store, store.Load());
    }

    static ConnectionEntry Entry(string name, string host = "10.0.0.1") => new() { Name = name, Host = host };

    /// <summary>Saves a tree with these connections at the top, and two folders, as both computers start from.</summary>
    void Start(params ConnectionNode[] nodes)
    {
        var tree = ConnectionTree.Empty();
        foreach (var node in nodes)
        {
            tree.Add(node, tree.Root);
        }

        Store().Save(tree);
    }

    static ConnectionNode Named(ConnectionTree tree, string name) => tree.Root.Descendants().Single(n => n.Name == name);

    static List<string> Names(ConnectionFolder folder) => folder.Children.Select(c => c.Name).ToList();

    [Fact]
    public void Connections_added_on_each_computer_both_stay()
    {
        Start(Entry("web"));
        var laptop = Open();
        var desktop = Open();

        laptop.Tree.Add(Entry("db"), laptop.Tree.Root);
        laptop.Store.Save(laptop.Tree);
        desktop.Tree.Add(Entry("mail"), desktop.Tree.Root);
        var report = desktop.Store.Save(desktop.Tree);

        Assert.NotNull(report);
        Assert.Equal(1, report.Added);
        Assert.Equal(["web", "db", "mail"], Names(desktop.Tree.Root));
        Assert.Equal(["web", "db", "mail"], Names(Store().Load().Root));

        Assert.NotNull(laptop.Store.ReloadIfChanged(laptop.Tree));
        Assert.Equal(["web", "db", "mail"], Names(laptop.Tree.Root));
        Assert.False(laptop.Store.NeedsSave);
    }

    [Fact]
    public void Different_fields_of_one_connection_changed_on_each_computer_are_both_kept()
    {
        Start(Entry("web"));
        var laptop = Open();
        var desktop = Open();

        Named(laptop.Tree, "web").Notes = "patched Tuesday";
        laptop.Store.Save(laptop.Tree);
        var web = (ConnectionEntry)Named(desktop.Tree, "web");
        web.Host = "10.0.0.2";
        web.Settings.Port = 2222;
        var report = desktop.Store.Save(desktop.Tree);

        Assert.Empty(report!.Conflicts);
        Assert.Same(web, Named(desktop.Tree, "web")); // Changed in place: sessions keep the object they hold.
        Assert.Equal("patched Tuesday", web.Notes);
        Assert.Equal("10.0.0.2", web.Host);
        Assert.Equal(2222, web.Settings.Port);
    }

    [Fact]
    public void A_field_both_computers_changed_keeps_this_computer_s_and_says_so()
    {
        Start(Entry("web"));
        var laptop = Open();
        var desktop = Open();

        ((ConnectionEntry)Named(laptop.Tree, "web")).Host = "laptop.example";
        laptop.Store.Save(laptop.Tree);
        ((ConnectionEntry)Named(desktop.Tree, "web")).Host = "desktop.example";
        var report = desktop.Store.Save(desktop.Tree);

        var conflict = Assert.Single(report!.Conflicts);
        Assert.Contains("\"web\": host changed on both computers", conflict);
        Assert.Equal("desktop.example", ((ConnectionEntry)Named(Store().Load(), "web")).Host);
    }

    [Fact]
    public void A_connection_deleted_on_one_computer_goes_on_the_other_unless_it_was_changed_there()
    {
        Start(Entry("old"), Entry("kept"));
        var laptop = Open();
        var desktop = Open();

        laptop.Tree.Remove(Named(laptop.Tree, "old"));
        laptop.Tree.Remove(Named(laptop.Tree, "kept"));
        laptop.Store.Save(laptop.Tree);

        Named(desktop.Tree, "kept").Notes = "still needed";
        var removed = new List<Guid>();
        desktop.Tree.Removed += (_, e) => removed.AddRange(e.Ids);
        var report = desktop.Store.Save(desktop.Tree);

        Assert.Equal(["kept"], Names(desktop.Tree.Root));
        Assert.Single(removed);
        Assert.Contains(report!.Conflicts, c => c.Contains("\"kept\" was deleted on the other computer but changed on this one"));
    }

    [Fact]
    public void A_folder_deleted_on_one_computer_comes_back_if_the_other_put_something_in_it()
    {
        var folder = new ConnectionFolder { Name = "Lab" };
        Start(folder);
        var laptop = Open();
        var desktop = Open();

        laptop.Tree.Remove(Named(laptop.Tree, "Lab"));
        laptop.Store.Save(laptop.Tree);
        desktop.Tree.Add(Entry("new box"), (ConnectionFolder)Named(desktop.Tree, "Lab"));
        desktop.Store.Save(desktop.Tree);

        var merged = Store().Load();
        Assert.Equal(["new box"], Names((ConnectionFolder)Named(merged, "Lab")));
    }

    [Fact]
    public void A_move_on_one_computer_and_an_edit_on_the_other_both_happen()
    {
        Start(new ConnectionFolder { Name = "Prod" }, Entry("web"));
        var laptop = Open();
        var desktop = Open();

        laptop.Tree.Move(Named(laptop.Tree, "web"), (ConnectionFolder)Named(laptop.Tree, "Prod"));
        laptop.Store.Save(laptop.Tree);
        Named(desktop.Tree, "web").Name = "web-01";
        desktop.Store.Save(desktop.Tree);

        var web = Named(Store().Load(), "web-01");
        Assert.Equal("Prod", web.Parent!.Name);
    }

    [Fact]
    public void A_reorder_on_one_computer_keeps_what_the_other_added_next_to_its_neighbor()
    {
        Start(Entry("a"), Entry("b"), Entry("c"));
        var laptop = Open();
        var desktop = Open();

        laptop.Tree.Move(Named(laptop.Tree, "c"), laptop.Tree.Root, 0);
        laptop.Store.Save(laptop.Tree);
        desktop.Tree.Add(Entry("b2"), desktop.Tree.Root, 2);
        desktop.Store.Save(desktop.Tree);

        Assert.Equal(["c", "a", "b", "b2"], Names(Store().Load().Root));
    }

    [Fact]
    public void A_guest_added_on_the_other_computer_arrives_under_its_host()
    {
        Start(Entry("hv-01"));
        var laptop = Open();
        var desktop = Open();

        laptop.Tree.AddGuest(Entry("vm-1"), (ConnectionEntry)Named(laptop.Tree, "hv-01"));
        laptop.Store.Save(laptop.Tree);

        desktop.Store.ReloadIfChanged(desktop.Tree);
        var host = (ConnectionEntry)Named(desktop.Tree, "hv-01");
        var guest = Assert.Single(host.Guests);
        Assert.Equal("vm-1", guest.Name);
        Assert.Same(host, guest.GuestOf);
        Assert.Same(desktop.Tree.Root, guest.Parent);
    }

    [Fact]
    public void Two_computers_that_started_with_no_file_keep_both_trees()
    {
        var laptop = Open();
        var desktop = Open();

        laptop.Tree.Add(Entry("one"), laptop.Tree.Root);
        laptop.Store.Save(laptop.Tree);
        desktop.Tree.Add(Entry("two"), desktop.Tree.Root);
        desktop.Store.Save(desktop.Tree);

        Assert.Equal(["one", "two"], Names(Store().Load().Root));
    }

    [Fact]
    public void Saving_with_nothing_new_leaves_the_file_alone()
    {
        Start(Entry("web"));
        var (store, tree) = Open();
        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FilePath, written);

        Assert.Null(store.Save(tree));
        Assert.Equal(written, File.GetLastWriteTimeUtc(FilePath));
    }

    [Fact]
    public void A_file_part_written_by_the_sync_app_is_left_for_the_next_try()
    {
        Start(Entry("web"));
        var (store, tree) = Open();
        File.WriteAllText(FilePath, "{ \"schemaVersion\": 1, \"root\": { \"type\": \"fol");

        Assert.Null(store.ReloadIfChanged(tree));
        Assert.Equal(["web"], Names(tree.Root));
    }

    [Fact]
    public void A_file_a_newer_Orrery_saved_is_never_saved_over()
    {
        Start(Entry("web"));
        var (store, tree) = Open();
        var newer = File.ReadAllText(FilePath).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        File.WriteAllText(FilePath, newer);

        tree.Add(Entry("db"), tree.Root);
        Assert.Throws<InvalidDataException>(() => store.Save(tree));
        Assert.Equal(newer, File.ReadAllText(FilePath));
    }

    // Two computers saving at once leave one file under the real name and the other as a sync app's conflict copy. The
    // file kept was saved from what both had before, not from the other computer's save: measured against that save,
    // what it added looked deleted.
    [Fact]
    public void A_sync_conflict_keeps_what_the_computer_whose_file_lost_added()
    {
        Start(Entry("web"));
        var laptop = Open();
        var desktop = Open();
        var before = File.ReadAllText(FilePath);

        laptop.Tree.Add(Entry("db"), laptop.Tree.Root);
        laptop.Store.Save(laptop.Tree);

        // The desktop saved from the file before the laptop's save, and its file is the one the sync app kept.
        File.WriteAllText(FilePath, before);
        ((ConnectionEntry)Named(desktop.Tree, "web")).Notes = "edited on the desktop";
        desktop.Store.Save(desktop.Tree);

        Assert.NotNull(laptop.Store.ReloadIfChanged(laptop.Tree));
        Assert.Equal(["web", "db"], Names(laptop.Tree.Root));
        Assert.Equal("edited on the desktop", ((ConnectionEntry)Named(laptop.Tree, "web")).Notes);
        Assert.True(laptop.Store.NeedsSave);
    }

    [Fact]
    public void A_computer_that_saved_without_the_file_doesn_t_delete_the_other_computer_s_connections()
    {
        Start(Entry("web"), Entry("db"));
        var laptop = Open();

        // The desktop started while the file wasn't there (a sync app still bringing it, say), and saved what it had.
        var away = FilePath + ".away";
        File.Move(FilePath, away);
        var desktop = Open();
        desktop.Tree.Add(Entry("new"), desktop.Tree.Root);
        desktop.Store.Save(desktop.Tree);
        File.Delete(away);

        Assert.NotNull(laptop.Store.ReloadIfChanged(laptop.Tree));
        Assert.Equal(["db", "new", "web"], Names(laptop.Tree.Root).Order());
    }

    // Each tree that starts empty makes its own root, so after taking in the file its text differed in the root's id,
    // and two computers open on the folder kept saving it at each other.
    [Fact]
    public void A_tree_that_started_empty_takes_the_file_in_without_saving_it_again()
    {
        Start(Entry("web"));
        var away = FilePath + ".away";
        File.Move(FilePath, away);
        var desktop = Open();
        File.Move(away, FilePath);

        Assert.NotNull(desktop.Store.ReloadIfChanged(desktop.Tree));
        Assert.Equal(["web"], Names(desktop.Tree.Root));
        Assert.False(desktop.Store.NeedsSave);

        var written = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(FilePath, written);
        Assert.Null(desktop.Store.Save(desktop.Tree));
        Assert.Equal(written, File.GetLastWriteTimeUtc(FilePath));
    }

    [Fact]
    public void Line_ends_history_and_the_root_s_id_don_t_count_as_changes()
    {
        Start(Entry("web"));
        var text = File.ReadAllText(FilePath);
        var json = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
        json["history"] = new System.Text.Json.Nodes.JsonArray("0123456789abcdef");
        json["root"]!["id"] = Guid.NewGuid().ToString();

        Assert.True(ConnectionStore.SameContent(text.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\r\n")));
        Assert.True(ConnectionStore.SameContent(text, json.ToJsonString()));
        Assert.False(ConnectionStore.SameContent(text, text.Replace("10.0.0.1", "10.0.0.2")));
    }

    [Fact]
    public void Each_save_names_the_versions_it_came_from_up_to_twenty()
    {
        var (store, tree) = Open();
        for (int i = 0; i < 25; i++)
        {
            tree.Add(Entry("c" + i), tree.Root);
            store.Save(tree);
        }

        var history = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(FilePath))!["history"]!.AsArray();
        Assert.Equal(20, history.Count);
        Assert.Equal(20, history.Select(h => h!.GetValue<string>()).Distinct().Count());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
