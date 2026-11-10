using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Edits in the inspector that don't validate are never thrown away by something else happening in the window.</summary>
public class UnsavedEditsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    MainWindow Open()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    static void SelectRow(MainWindow window, ConnectionNode node)
    {
        var treeView = VaultAutoLockTests.Private<TreeView>(window, "_treeView");
        treeView.SelectedItem = ((IEnumerable<NodeViewModel>)treeView.ItemsSource!).Single(r => r.Node == node);
        Dispatcher.UIThread.RunJobs();
    }

    static ConnectionNode? SelectedRow(MainWindow window) =>
        (VaultAutoLockTests.Private<TreeView>(window, "_treeView").SelectedItem as NodeViewModel)?.Node;

    static TextBox PortBox(PropertiesPanel panel) =>
        panel.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Port");

    static object? Call(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic, args.Select(a => a.GetType()).ToArray())!.Invoke(window, args);

    [AvaloniaFact]
    public void Choosing_another_row_with_a_bad_port_stays_on_the_edited_row_and_keeps_the_edit()
    {
        var window = Open();
        try
        {
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var panel = VaultAutoLockTests.Private<PropertiesPanel>(window, "_properties");
            var first = new ConnectionEntry { Name = "one", Host = "one.example" };
            var second = new ConnectionEntry { Name = "two", Host = "two.example" };
            tree.Add(first, tree.Root);
            tree.Add(second, tree.Root);
            SelectRow(window, first);

            PortBox(panel).Text = "abc";
            Dispatcher.UIThread.RunJobs();
            SelectRow(window, second);

            Assert.Same(first, SelectedRow(window));
            Assert.Same(first, panel.Node);
            Assert.Equal("abc", PortBox(panel).Text);

            // Duplicating waits for the edit to be fixed rather than dropping it.
            Call(window, "Duplicate", first);
            Assert.Equal(2, tree.Root.Children.Count);
            Assert.Equal("abc", PortBox(panel).Text);

            PortBox(panel).Text = "2222";
            Dispatcher.UIThread.RunJobs();
            SelectRow(window, second);
            Assert.Same(second, panel.Node);
            Assert.Equal(2222, first.Settings.Port);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_filter_that_hides_a_row_with_bad_edits_is_cleared_so_the_row_can_be_fixed()
    {
        var window = Open();
        try
        {
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var panel = VaultAutoLockTests.Private<PropertiesPanel>(window, "_properties");
            var filter = VaultAutoLockTests.Private<TextBox>(window, "_filter");
            var first = new ConnectionEntry { Name = "one", Host = "one.example" };
            tree.Add(first, tree.Root);
            tree.Add(new ConnectionEntry { Name = "two", Host = "two.example" }, tree.Root);
            SelectRow(window, first);

            PortBox(panel).Text = "abc";
            Dispatcher.UIThread.RunJobs();
            filter.Text = "two";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(string.Empty, filter.Text);
            Assert.Same(first, SelectedRow(window));
            Assert.Same(first, panel.Node);
            Assert.Equal("abc", PortBox(panel).Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task A_vault_lock_saves_valid_edits_and_leaves_ones_that_dont_validate_in_the_pane()
    {
        var window = Open();
        try
        {
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var panel = VaultAutoLockTests.Private<PropertiesPanel>(window, "_properties");
            var entry = new ConnectionEntry { Name = "box", Host = "box.example" };
            tree.Add(entry, tree.Root);
            SelectRow(window, entry);

            PortBox(panel).Text = "2222";
            Dispatcher.UIThread.RunJobs();
            await (Task)Call(window, "ShowVaultLockedAsync")!;
            Assert.Equal(2222, entry.Settings.Port);

            PortBox(panel).Text = "abc";
            Dispatcher.UIThread.RunJobs();
            await (Task)Call(window, "ShowVaultLockedAsync")!;
            Assert.Same(entry, panel.Node);
            Assert.Equal("abc", PortBox(panel).Text);
            Assert.Equal(2222, entry.Settings.Port);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_deleted_connection_keeps_what_its_folders_and_host_gave_it()
    {
        var tree = ConnectionTree.Empty();
        var jump = new ConnectionEntry { Name = "bastion", Host = "bastion.example", Settings = { Protocol = Protocol.Ssh } };
        tree.Root.Settings.KeepAliveSeconds = 17;
        var folder = new ConnectionFolder { Name = "Internal", Settings = { JumpHostId = jump.Id, Username = "ops", Protocol = Protocol.Ssh } };
        var inner = new ConnectionFolder { Name = "Web", Settings = { ChosenDisplays = [new ChosenDisplay(1920, 0, 1920, 1080)] } };
        var entry = new ConnectionEntry { Name = "web-01", Host = "10.0.0.5", Settings = { UseAllMonitors = true } };
        var host = new ConnectionEntry { Name = "hv-01", Host = "10.0.0.9", Settings = { AutoReconnect = false } };
        var guest = new ConnectionEntry { Name = "vm-01", Host = "10.0.0.10" };
        tree.Add(jump, tree.Root);
        tree.Add(folder, tree.Root);
        tree.Add(inner, folder);
        tree.Add(entry, inner);
        tree.Add(host, folder);
        tree.AddGuest(guest, host);

        var nodes = new ConnectionNode[] { entry, host, guest };
        var before = nodes.Select(SettingsResolver.Resolve).ToList();

        MainWindow.KeepInheritedSettings(folder, withGuests: true);
        tree.Remove(folder);

        for (int i = 0; i < nodes.Length; i++)
        {
            var after = SettingsResolver.Resolve(nodes[i]);
            Assert.Equal(before[i].JumpHostId.Value, after.JumpHostId.Value);
            Assert.Equal(jump.Id, after.JumpHostId.Value);
            Assert.Equal("ops", after.Username.Value);
            Assert.Equal(before[i].Protocol.Value, after.Protocol.Value);
            Assert.Equal(before[i].KeepAliveSeconds.Value, after.KeepAliveSeconds.Value);
            Assert.Equal(before[i].AutoReconnect.Value, after.AutoReconnect.Value);
            Assert.Equal(before[i].FullScreen.Value.On, after.FullScreen.Value.On);
        }

        // Its own "every display" still wins over the folder's chosen ones.
        Assert.Equal(FullScreenOn.EveryDisplay, SettingsResolver.Resolve(entry).FullScreen.Value.On);
        Assert.False(SettingsResolver.Resolve(guest).AutoReconnect.Value);
        Assert.Equal(17, SettingsResolver.Resolve(guest).KeepAliveSeconds.Value);
        Assert.Equal([jump], SettingsResolver.JumpChain(guest, tree, out _));
    }

    // A user's right-click menu "stopped working": the pane with a bad edit had been hidden, so the menu stayed shut
    // (and other rows wouldn't select) with the reason out of sight.
    [AvaloniaFact]
    public void With_the_pane_hidden_a_bad_edit_brings_it_back_when_it_stops_the_menu_or_a_row_change()
    {
        var window = Open();
        try
        {
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var panel = VaultAutoLockTests.Private<PropertiesPanel>(window, "_properties");
            var inspector = VaultAutoLockTests.Private<Border>(window, "_propertiesHost");
            var treeView = VaultAutoLockTests.Private<TreeView>(window, "_treeView");
            var first = new ConnectionEntry { Name = "one", Host = "one.example" };
            var second = new ConnectionEntry { Name = "two", Host = "two.example" };
            tree.Add(first, tree.Root);
            tree.Add(second, tree.Root);
            SelectRow(window, first);

            PortBox(panel).Text = "abc";
            Dispatcher.UIThread.RunJobs();
            Call(window, "ToggleInspector");
            Dispatcher.UIThread.RunJobs();
            Assert.False(inspector.IsVisible);

            bool opened = false;
            treeView.ContextMenu!.Opened += (_, _) => opened = true;
            treeView.RaiseEvent(new ContextRequestedEventArgs());
            Dispatcher.UIThread.RunJobs();
            Assert.False(opened);
            Assert.True(inspector.IsVisible);

            Call(window, "ToggleInspector");
            Dispatcher.UIThread.RunJobs();
            Assert.False(inspector.IsVisible);
            SelectRow(window, second);
            Assert.Same(first, SelectedRow(window));
            Assert.True(inspector.IsVisible);
            Assert.Equal("abc", PortBox(panel).Text);
        }
        finally
        {
            window.Close();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
