using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.App.Tests;

public class WakePropertiesTests
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

    static async Task<(PropertiesPanel Panel, Window Window, ConnectionEntry Entry, TextBox Mac, TextBox Broadcast, TextBlock Error)> Open()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "nas", Host = "nas.local" };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new NoCredentials());
        var window = new Window { Content = panel, Width = 400, Height = 1400 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        var boxes = window.GetVisualDescendants().OfType<TextBox>().ToList();
        return (panel, window, entry, boxes.Single(t => t.Watermark == "00:1A:2B:3C:4D:5E"), boxes.Single(t => t.Watermark == "255.255.255.255"),
            window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Foreground == Dialogs.DangerBrush));
    }

    [AvaloniaFact]
    public async Task A_mac_address_in_any_common_format_is_saved_in_one_form_with_the_broadcast_address()
    {
        var (panel, window, entry, mac, broadcast, _) = await Open();

        mac.Text = "00-1a-2b-3c-4d-5e";
        broadcast.Text = "192.168.1.255";
        Assert.True(panel.TrySave());

        Assert.Equal("00:1A:2B:3C:4D:5E", entry.MacAddress);
        Assert.Equal("192.168.1.255", entry.WakeBroadcast);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_default_broadcast_address_is_not_stored_and_clearing_the_fields_removes_both()
    {
        var (panel, window, entry, mac, broadcast, _) = await Open();
        mac.Text = "001a2b3c4d5e";
        broadcast.Text = "255.255.255.255";
        Assert.True(panel.TrySave());
        Assert.Null(entry.WakeBroadcast);
        Assert.NotNull(entry.MacAddress);

        mac.Text = "";
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.TrySave());
        Assert.Null(entry.MacAddress);
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_invalid_mac_or_broadcast_address_is_refused_with_a_message_and_nothing_is_saved()
    {
        var (panel, window, entry, mac, broadcast, error) = await Open();

        mac.Text = "12:34";
        Assert.False(panel.TrySave());
        Assert.True(error.IsVisible);
        Assert.Contains("MAC address", error.Text);
        Assert.Null(entry.MacAddress);

        mac.Text = "00:1A:2B:3C:4D:5E";
        broadcast.Text = "nope";
        Assert.False(panel.TrySave());
        Assert.Contains("broadcast", error.Text);
        Assert.Null(entry.MacAddress);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Folders_have_no_wake_fields()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "f" };
        tree.Add(folder, tree.Root);
        var panel = new PropertiesPanel(tree, new NoCredentials());
        var window = new Window { Content = panel, Width = 400, Height = 1400 };
        window.Show();
        await panel.ShowAsync(folder);
        Dispatcher.UIThread.RunJobs();

        var mac = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Watermark == "00:1A:2B:3C:4D:5E");
        Assert.False(mac.IsEffectivelyVisible);
        window.Close();
    }
}
