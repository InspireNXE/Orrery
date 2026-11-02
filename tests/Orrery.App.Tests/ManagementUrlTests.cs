using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Storage;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

public class ManagementUrlTests : IDisposable
{
    const string Watermark = "https://10.20.0.1:8443";
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

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

    static async Task<(PropertiesPanel Panel, Window Window)> Show(ConnectionTree tree, ConnectionNode node)
    {
        var panel = new PropertiesPanel(tree, new NoCredentials());
        var window = new Window { Content = panel, Width = 400, Height = 1400 };
        window.Show();
        await panel.ShowAsync(node);
        Dispatcher.UIThread.RunJobs();
        return (panel, window);
    }

    static TextBox Field(Window window) => window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == Watermark);

    [AvaloniaFact]
    public async Task A_connection_saves_its_management_url_and_clearing_it_removes_it()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "fw", Host = "10.20.0.1" };
        tree.Add(entry, tree.Root);
        var (panel, window) = await Show(tree, entry);

        Field(window).Text = "10.20.0.1:8443";
        Assert.True(panel.TrySave());
        Assert.Equal("https://10.20.0.1:8443", entry.ManagementUrl);

        Field(window).Text = "";
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.TrySave());
        Assert.Null(entry.ManagementUrl);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_address_that_isnt_a_web_page_is_refused()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "fw", Host = "10.20.0.1" };
        tree.Add(entry, tree.Root);
        var (panel, window) = await Show(tree, entry);

        Field(window).Text = "file:///C:/Windows/System32/calc.exe";
        Assert.False(panel.TrySave());
        Assert.Null(entry.ManagementUrl);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_folder_has_no_management_url_to_pass_on()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Lab" };
        tree.Add(folder, tree.Root);
        var (_, window) = await Show(tree, folder);

        Assert.False(Field(window).IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void The_right_click_menu_offers_the_management_page_only_for_a_connection_that_has_one()
    {
        VaultAutoLockTests.AddMainWindowResources();
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var folder = new ConnectionFolder { Name = "Lab" };
            tree.Add(folder, tree.Root);
            var firewall = new ConnectionEntry { Name = "fw", Host = "10.20.0.1", ManagementUrl = "https://10.20.0.1:8443" };
            var guest = new ConnectionEntry { Name = "web", Host = "10.20.0.5" };
            tree.Add(firewall, folder);
            tree.Add(guest, folder);
            Dispatcher.UIThread.RunJobs();

            var treeView = VaultAutoLockTests.Private<TreeView>(window, "_treeView");
            var item = window.FindControl<MenuItem>("ManagementMenuItem")!;
            bool OfferedFor(ConnectionNode node)
            {
                treeView.SelectedItems!.Clear();
                treeView.SelectedItem = treeView.GetVisualDescendants().OfType<TreeViewItem>().Select(i => i.DataContext).OfType<NodeViewModel>().First(r => r.Node == node);
                Dispatcher.UIThread.RunJobs();
                treeView.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = treeView });
                Dispatcher.UIThread.RunJobs();
                bool shown = item.IsVisible;
                treeView.ContextMenu!.Close();
                Dispatcher.UIThread.RunJobs();
                return shown;
            }

            Assert.Equal((true, false, false), (OfferedFor(firewall), OfferedFor(guest), OfferedFor(folder)));
        }
        finally
        {
            window.Close();
        }
    }
}
