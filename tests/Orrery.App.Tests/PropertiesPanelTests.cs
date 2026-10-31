using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class PropertiesPanelTests
{
    [AvaloniaFact]
    public async Task Selecting_several_nodes_in_turn_does_not_crash()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers" };
        var first = new ConnectionEntry { Name = "one", Host = "one.example" };
        var second = new ConnectionEntry { Name = "two", Host = "two.example" };
        tree.Add(folder, tree.Root);
        tree.Add(first, folder);
        tree.Add(second, folder);

        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 600 };
        window.Show();

        foreach (var node in new ConnectionNode?[] { first, second, folder, null, first })
        {
            await panel.ShowAsync(node);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Same(first, panel.Node);
    }

    [AvaloniaFact]
    public async Task A_slow_earlier_selection_does_not_overwrite_a_newer_one()
    {
        var tree = ConnectionTree.Empty();
        var first = new ConnectionEntry { Name = "one", Host = "one.example", Settings = new InheritableSettings { Username = "alice" } };
        var second = new ConnectionEntry { Name = "two", Host = "two.example", Settings = new InheritableSettings { Username = "bob" } };
        tree.Add(first, tree.Root);
        tree.Add(second, tree.Root);

        var store = new EmptyCredentialStore { Gate = new TaskCompletionSource() };
        var panel = new PropertiesPanel(tree, store);
        var window = new Window { Content = panel, Width = 400, Height = 600 };
        window.Show();

        var slow = panel.ShowAsync(first);
        store.Gate = null;
        await panel.ShowAsync(second);
        var gate = store.FirstGate!;
        gate.SetResult();
        await slow;
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text).ToList();
        Assert.Same(second, panel.Node);
        Assert.Contains("bob", texts);
        Assert.DoesNotContain("alice", texts);
    }

    [AvaloniaFact]
    public async Task Turning_off_the_clipboard_for_a_connection_is_saved()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers", Settings = new InheritableSettings { ShareClipboard = false } };
        var entry = new ConnectionEntry { Name = "box", Host = "box.example" };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 900 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var clipboard = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(c => c.Name == "ShareClipboard");
        Assert.False(clipboard.IsChecked); // Inherited from the folder.
        Assert.False(clipboard.IsEnabled);

        var inherit = InheritBoxFor(clipboard);
        inherit.IsChecked = false;
        clipboard.IsChecked = true;
        Assert.True(panel.TrySave());

        Assert.True(entry.Settings.ShareClipboard);
        Assert.True(SettingsResolver.Resolve(entry).ShareClipboard.Value);
    }

    [AvaloniaFact]
    public async Task Defaults_set_on_the_top_level_reach_every_connection()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = "box.example" };
        tree.Add(entry, tree.Root);

        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 900 };
        window.Show();
        await panel.ShowAsync(tree.Root);
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Defaults for all connections", texts);
        Assert.DoesNotContain("Name", texts);

        // Turn off Inherit on Port (the second row) and set 3390.
        var inherits = window.GetVisualDescendants().OfType<ToggleButton>().Where(c => Equals(c.Content, "Inherit")).ToList();
        inherits[1].IsChecked = false;
        var port = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.IsEffectivelyVisible && t.Text == "3389");
        port.Text = "3390";
        Assert.True(panel.TrySave());

        Assert.Equal(3390, tree.Root.Settings.Port);
        Assert.Equal("Connections", tree.Root.Name);
        Assert.Equal(3390, SettingsResolver.Resolve(entry).Port.Value);

        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "From defaults for all connections");
    }

    /// <summary>The Inherit chip in the same row as <paramref name="editor"/>.</summary>
    static ToggleButton InheritBoxFor(Control editor) =>
        ((Control)editor.Parent!).GetVisualDescendants().OfType<ToggleButton>().Single(c => Equals(c.Content, "Inherit"));

    [AvaloniaFact]
    public async Task The_ssh_agent_setting_is_on_by_default_and_can_be_turned_off()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh } };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 1000 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var agent = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(c => c.Name == "UseSshAgent");
        Assert.True(agent.IsChecked);
        Assert.True(SettingsResolver.Resolve(entry).UseSshAgent.Value);

        InheritBoxFor(agent).IsChecked = false;
        agent.IsChecked = false;
        Assert.True(panel.TrySave());

        Assert.False(entry.Settings.UseSshAgent);
        Assert.False(SettingsResolver.Resolve(entry).UseSshAgent.Value);
    }

    [AvaloniaFact]
    public async Task The_older_algorithms_setting_is_off_by_default_and_can_be_turned_on()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh } };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 1000 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var legacy = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(c => c.Name == "AllowLegacySsh");
        Assert.False(legacy.IsChecked);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Allow older, weaker algorithms");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Only for old devices that can't connect otherwise");

        InheritBoxFor(legacy).IsChecked = false;
        legacy.IsChecked = true;
        Assert.True(panel.TrySave());

        Assert.True(entry.Settings.AllowLegacySsh);
        Assert.True(SettingsResolver.Resolve(entry).AllowLegacySsh.Value);
    }

    [AvaloniaFact]
    public async Task Display_quality_shows_what_is_inherited_and_can_be_set()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Branch office", Settings = { DisplayQuality = DisplayQuality.Fastest } };
        var entry = new ConnectionEntry { Name = "box", Host = "box.example" };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 1000 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var quality = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "DisplayQuality");
        Assert.Equal("Fastest, for slow links", (quality.SelectedItem as ComboBoxItem)?.Content);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "From Branch office");

        InheritBoxFor(quality).IsChecked = false;
        quality.SelectedItem = quality.Items.OfType<ComboBoxItem>().Single(i => (DisplayQuality)i.Tag! == DisplayQuality.Best);
        Assert.True(panel.TrySave());

        Assert.Equal(DisplayQuality.Best, entry.Settings.DisplayQuality);
        Assert.Equal(DisplayQuality.Best, SettingsResolver.Resolve(entry).DisplayQuality.Value);
    }

    [AvaloniaFact]
    public async Task Enlarging_shows_the_app_setting_until_the_connection_sets_its_own()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = "box.example" };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore(), enlargeByDefault: () => false);
        var window = new Window { Content = panel, Width = 400, Height = 1000 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var enlarge = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(c => c.Name == "EnlargeRemoteDesktop");
        Assert.False(enlarge.IsChecked);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "From Settings");

        InheritBoxFor(enlarge).IsChecked = false;
        enlarge.IsChecked = true;
        Assert.True(panel.TrySave());

        Assert.True(entry.Settings.EnlargeRemoteDesktop);
        Assert.True(SettingsResolver.Resolve(entry).Enlarge(appSetting: false));
    }

    [AvaloniaFact]
    public async Task A_guest_says_whose_guest_it_is_and_names_the_host_where_a_setting_comes_from_it()
    {
        var tree = ConnectionTree.Empty();
        var lab = new ConnectionFolder { Name = "Lab" };
        var host = new ConnectionEntry { Name = "hv-01", Host = "hv-01.lab", Settings = { ShareClipboard = false } };
        var guest = new ConnectionEntry { Name = "vm-a", Host = "vm-a.lab" };
        tree.Add(lab, tree.Root);
        tree.Add(host, lab);
        tree.AddGuest(guest, host);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 1000 };
        window.Show();

        await panel.ShowAsync(guest);
        Dispatcher.UIThread.RunJobs();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Guest of hv-01 · Lab", texts);
        Assert.Contains("From hv-01", texts);
        Assert.False(window.GetVisualDescendants().OfType<ToggleSwitch>().Single(c => c.Name == "ShareClipboard").IsChecked);

        await panel.ShowAsync(host);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Lab · 1 guest");
    }

    [AvaloniaFact]
    public async Task A_credential_chosen_while_connecting_shows_and_survives_applying_other_edits()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "one", Host = "one.example" };
        tree.Add(entry, tree.Root);
        var store = new EmptyCredentialStore();
        var panel = new PropertiesPanel(tree, store);
        var window = new Window { Content = panel, Width = 400, Height = 600 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        // An edit not applied yet, then the connect prompt saves a credential and uses it from now on.
        window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == "one").Text = "one renamed";
        var admin = new CredentialSummary(Guid.NewGuid(), "Admin", CredentialKind.Password, "admin", null, null);
        store.Items.Add(admin);
        entry.Settings.CredentialId = admin.Id;
        await panel.CredentialsChangedAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Tag, admin.Id)));
        Assert.Equal(admin.Id, (picker.SelectedItem as ComboBoxItem)?.Tag);

        Assert.True(panel.TrySave());
        Assert.Equal(admin.Id, entry.Settings.CredentialId);
        Assert.Equal("one renamed", entry.Name);
    }

    sealed class EmptyCredentialStore : ICredentialStore
    {
        TaskCompletionSource? _gate;

        /// <summary>When set, the next <see cref="ListAsync"/> waits for it.</summary>
        public TaskCompletionSource? Gate
        {
            get => _gate;
            set { _gate = value; FirstGate ??= value; }
        }

        public TaskCompletionSource? FirstGate { get; private set; }

        public List<CredentialSummary> Items { get; } = [];

        public bool IsLocked => false;
        public bool Exists => true;
        public Task UnlockAsync(string masterPassword) => Task.CompletedTask;
        public void Lock() { }

        public async Task<IReadOnlyList<CredentialSummary>> ListAsync()
        {
            if (_gate is { } gate)
            {
                await gate.Task;
            }

            return Items.ToList();
        }

        public Task<Credential?> GetAsync(Guid id) => Task.FromResult<Credential?>(null);
        public Task SaveAsync(Credential credential) => Task.CompletedTask;
        public Task SaveAllAsync(IReadOnlyList<Credential> credentials) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }
}
