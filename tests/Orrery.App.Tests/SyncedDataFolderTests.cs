using Avalonia.Headless.XUnit;
using Orrery.Core.Connections;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>The window when another computer saves the data folder's files through a sync app.</summary>
public class SyncedDataFolderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-synced-" + Guid.NewGuid().ToString("N"));

    DataLocation Location()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        return location;
    }

    [AvaloniaFact]
    public async Task A_connection_another_computer_saved_shows_in_the_tree_and_this_computer_s_stays()
    {
        var location = Location();
        var window = new MainWindow(location) { Width = 1200, Height = 800 };
        try
        {
            window.Show();
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            tree.Add(new ConnectionEntry { Name = "here", Host = "10.0.0.1" }, tree.Root);

            // The other computer saves its own tree to the file.
            var other = ConnectionStore.In(location);
            var theirs = other.Load();
            theirs.Add(new ConnectionEntry { Name = "there", Host = "10.0.0.2" }, theirs.Root);
            other.Save(theirs);

            await window.OnDataFilesChangedAsync(["connections.json"]);

            Assert.Equal(["here", "there"], tree.Root.Children.Select(c => c.Name).Order());
            Assert.Equal(["here", "there"], ConnectionStore.In(location).Load().Root.Children.Select(c => c.Name).Order());
        }
        finally
        {
            window.Close();
        }
    }

    // Moved aside, it went from every computer sharing the folder, and the first to save had its few connections
    // merged into the others' as if the rest had been deleted.
    [Fact]
    public void A_file_a_newer_Orrery_saved_stays_where_it_is_and_isn_t_saved_over()
    {
        var location = Location();
        var path = location.FileIn("connections.json");
        var newer = """{ "schemaVersion": 99, "root": { "type": "folder", "name": "Connections", "children": [] } }""";
        File.WriteAllText(path, newer);
        var store = ConnectionStore.In(location);

        var (tree, warning) = MainWindow.LoadTree(store, location);

        Assert.Empty(tree.Root.Children);
        Assert.Contains("newer version of Orrery", warning);
        Assert.Equal(newer, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(location.DataDir, "connections.json*"));

        tree.Add(new ConnectionEntry { Name = "here", Host = "10.0.0.1" }, tree.Root);
        Assert.Throws<InvalidDataException>(() => store.Save(tree));
        Assert.Equal(newer, File.ReadAllText(path));
    }

    [Fact]
    public void A_damaged_file_is_moved_aside_and_the_message_says_where()
    {
        var location = Location();
        var path = location.FileIn("connections.json");
        File.WriteAllText(path, "{ \"schemaVersion\": 6, \"root\": { \"type\": \"fol");

        var (tree, warning) = MainWindow.LoadTree(ConnectionStore.In(location), location);

        Assert.Empty(tree.Root.Children);
        Assert.False(File.Exists(path));
        var aside = Assert.Single(Directory.GetFiles(location.DataDir, "connections.json.unreadable-*"));
        Assert.Contains(aside, warning);
        Assert.StartsWith("Orrery couldn't read connections.json", warning);
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
