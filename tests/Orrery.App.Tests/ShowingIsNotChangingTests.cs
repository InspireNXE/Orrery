using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.App.Tests;

public class ShowingIsNotChangingTests
{
    sealed class NoCredentials : ICredentialStore
    {
        public bool IsLocked => false;
        public bool Exists => true;
        public Task UnlockAsync(string masterPassword) => Task.CompletedTask;
        public void Lock() { }
        public Task<IReadOnlyList<CredentialSummary>> ListAsync() => Task.FromResult<IReadOnlyList<CredentialSummary>>([]);
        public Task<Credential?> GetAsync(Guid id) => Task.FromResult<Credential?>(null);
        public Task SaveAsync(Credential credential) => Task.CompletedTask;
        public Task SaveAllAsync(IReadOnlyList<Credential> credentials) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    // Saving changes the tree, which rebuilds its rows; a save on just looking at a folder lost the double-click that
    // selected the next row.
    [AvaloniaFact]
    public async Task Looking_at_a_folder_then_a_connection_saves_nothing()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Lab" };
        tree.Add(folder, tree.Root);
        var entry = new ConnectionEntry { Name = "east-gpu-01", Host = "10.20.16.3" };
        tree.Add(entry, folder);
        int changes = 0;
        tree.Changed += (_, _) => changes++;

        var panel = new PropertiesPanel(tree, new NoCredentials());
        var window = new Window { Content = panel, Width = 420, Height = 1400 };
        window.Show();
        await panel.ShowAsync(folder);
        Dispatcher.UIThread.RunJobs();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        await panel.ShowAsync(folder);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, changes);
        window.Close();
    }
}
