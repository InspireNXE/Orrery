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
