using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Controls;
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
    public async Task An_inherited_default_port_follows_the_connections_own_protocol()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers" };
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh } };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 900 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text).ToList();
        Assert.Contains("22", texts);
        Assert.DoesNotContain("3389", texts);

        var protocol = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(c => c.Items.OfType<ComboBoxItem>().Any(i => i.Tag is Protocol));
        protocol.SelectedItem = protocol.Items.OfType<ComboBoxItem>().Single(i => (Protocol)i.Tag! == Protocol.Vnc);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("5900", window.GetVisualDescendants().OfType<TextBox>().Select(t => t.Text));
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
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "From Servers");

        clipboard.IsChecked = true;
        Assert.True(panel.TrySave());

        Assert.True(entry.Settings.ShareClipboard);
        Assert.True(SettingsResolver.Resolve(entry).ShareClipboard.Value);
    }

    [AvaloniaFact]
    public async Task Changing_an_inherited_value_sets_it_here_and_the_footer_counts_it()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Lab servers", Settings = new InheritableSettings { Scaling = DesktopScaling.Fit } };
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { RemoteAudio = RemoteAudio.PlayHere } };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);
        var (panel, window) = await Show(tree, entry);
        bool Shown(string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

        // Only the rows' names and values show until one is opened.
        Assert.True(Shown("1 set on this connection"));
        Assert.True(Shown("Fit the tab"));
        var scaling = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "Scaling");
        Assert.False(scaling.IsEffectivelyVisible);

        scaling.SelectedItem = scaling.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, DesktopScaling.ShrinkToFit));
        Assert.True(Shown("2 set on this connection"));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "From Lab servers: Fit the tab");
        Assert.True(panel.TrySave());
        Assert.Equal(DesktopScaling.ShrinkToFit, entry.Settings.Scaling);

        UseInherited(scaling);
        Assert.Equal(DesktopScaling.Fit, (scaling.SelectedItem as ComboBoxItem)?.Tag);
        Assert.True(Shown("1 set on this connection"));
        Assert.True(panel.TrySave());
        Assert.Null(entry.Settings.Scaling);
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

        // Setting the port sets it here.
        var port = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Port");
        Assert.Equal("3389", port.Text);
        port.Text = "3390";
        Assert.True(panel.TrySave());

        Assert.Equal(3390, tree.Root.Settings.Port);
        Assert.Equal("Connections", tree.Root.Name);
        Assert.Equal(3390, SettingsResolver.Resolve(entry).Port.Value);

        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "From defaults for all connections");
    }

    /// <summary>The headings of the sections shown, as written: "RDP", "SSH TUNNELS".</summary>
    static List<string?> SectionsShown(Window window) => window.GetVisualDescendants().OfType<TextBlock>()
        .Where(t => t.Classes.Contains("section") && t.IsEffectivelyVisible).Select(t => t.Text).ToList();

    static async Task<(PropertiesPanel Panel, Window Window)> Show(ConnectionTree tree, ConnectionNode node)
    {
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 900 };
        window.Show();
        await panel.ShowAsync(node);
        Dispatcher.UIThread.RunJobs();
        return (panel, window);
    }

    [AvaloniaTheory]
    [InlineData(Protocol.Rdp, new[] { "DISPLAY (RDP AND VNC)", "RDP" }, new[] { "VNC", "SSH", "SERIAL LINE" })]
    [InlineData(Protocol.Vnc, new[] { "DISPLAY (RDP AND VNC)", "VNC" }, new[] { "RDP", "SSH", "SERIAL LINE" })]
    [InlineData(Protocol.Ssh, new[] { "SSH", "SSH TUNNELS", "CREDENTIALS" }, new[] { "RDP", "VNC", "SERIAL LINE", "DISPLAY (RDP AND VNC)" })]
    [InlineData(Protocol.Serial, new[] { "SERIAL LINE" }, new[] { "RDP", "VNC", "SSH", "CREDENTIALS", "SSH TUNNELS", "WAKE ON LAN" })]
    [InlineData(Protocol.Https, new[] { "WAKE ON LAN" }, new[] { "RDP", "VNC", "SSH", "SERIAL LINE", "CREDENTIALS", "SESSION", "SSH TUNNELS" })]
    public async Task A_connection_shows_only_its_own_protocols_sections(Protocol protocol, string[] shown, string[] hidden)
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = { Protocol = protocol } };
        tree.Add(entry, tree.Root);

        var (_, window) = await Show(tree, entry);
        var sections = SectionsShown(window);
        Assert.All(shown, s => Assert.Contains(s, sections));
        Assert.All(hidden, s => Assert.DoesNotContain(s, sections));
    }

    [AvaloniaFact]
    public async Task A_folder_and_the_defaults_show_every_protocols_sections()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Lab" };
        tree.Add(folder, tree.Root);

        foreach (var node in new ConnectionNode[] { folder, tree.Root })
        {
            var (_, window) = await Show(tree, node);
            var sections = SectionsShown(window);
            Assert.All(new[] { "CREDENTIALS", "SESSION", "DISPLAY (RDP AND VNC)", "RDP", "VNC", "SSH", "SERIAL LINE", "SSH TUNNELS" },
                s => Assert.Contains(s, sections));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Picking_another_protocol_shows_its_sections_before_saving()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = { Protocol = Protocol.Rdp } };
        tree.Add(entry, tree.Root);

        var (_, window) = await Show(tree, entry);
        var protocol = window.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Protocol");
        protocol.SelectedItem = protocol.Items.Single(i => (i as ComboBoxItem)?.Tag is Protocol.Ssh);
        Dispatcher.UIThread.RunJobs();

        var sections = SectionsShown(window);
        Assert.Contains("SSH", sections);
        Assert.DoesNotContain("RDP", sections);
    }

    [AvaloniaFact]
    public async Task A_folders_keep_alive_and_socks_proxy_are_saved_and_a_bad_proxy_refused()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Lab" };
        tree.Add(folder, tree.Root);

        var (panel, window) = await Show(tree, folder);
        TextBox Box(string name) => window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == name);
        var keepAlive = Box("Keep-alive (seconds)");
        var socks = Box("SOCKS proxy");

        keepAlive.Text = "15";
        socks.Text = "not a port";
        Assert.False(panel.TrySave());
        Assert.Null(folder.Settings.SocksProxy);

        socks.Text = "127.0.0.1:1080";
        Assert.True(panel.TrySave());
        Assert.Equal(15, folder.Settings.KeepAliveSeconds);
        Assert.Equal("127.0.0.1:1080", folder.Settings.SocksProxy);

        keepAlive.Text = "86400";
        Dispatcher.UIThread.RunJobs(); // The edit marks the pane changed once its text-changed event runs.
        Assert.False(panel.TrySave());
        Assert.Equal(15, folder.Settings.KeepAliveSeconds);
    }

    /// <summary>Clicks "Use that instead" in the row of <paramref name="editor"/>, going back to the inherited value.</summary>
    static void UseInherited(Control editor)
    {
        static Button? Use(Control c) => c.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Equals(b.Content, "Use that instead"));
        var around = editor.GetVisualAncestors().OfType<Control>().First(c => Use(c) is not null);
        Use(around)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

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
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "From Settings");

        enlarge.IsChecked = true;
        Assert.True(panel.TrySave());

        Assert.True(entry.Settings.EnlargeRemoteDesktop);
        Assert.True(SettingsResolver.Resolve(entry).Enlarge(appSetting: false));
    }

    [AvaloniaFact]
    public async Task Where_web_pages_open_shows_the_app_setting_until_the_connection_sets_its_own()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "router", Host = "router.lan", Settings = { Protocol = Protocol.Https } };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore(), webPagesInOrreryByDefault: () => false);
        var window = new Window { Content = panel, Width = 400, Height = 1000 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var webPages = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "WebPages");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Open web pages in");
        Assert.Equal("The default browser", (webPages.SelectedItem as ComboBoxItem)?.Content);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "From Settings");

        webPages.SelectedItem = webPages.Items.OfType<ComboBoxItem>().Single(i => (bool)i.Tag! == true);
        Assert.True(panel.TrySave());

        Assert.True(entry.Settings.WebPagesInOrrery);
        Assert.True(SettingsResolver.Resolve(entry).InOrrery(appSetting: false));
    }

    [AvaloniaFact]
    public async Task Color_correction_can_be_turned_off_for_one_connection()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "design", Host = "design.example" };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 1000 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var correction = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ColorCorrection");
        Assert.Equal("Use app setting", (correction.SelectedItem as ComboBoxItem)?.Content);
        Assert.Equal("Color correction", Avalonia.Automation.AutomationProperties.GetName(correction));

        correction.SelectedItem = correction.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Tag, false));
        Assert.True(panel.TrySave());

        Assert.False(entry.Settings.ColorCorrection);
        Assert.False(SettingsResolver.Resolve(entry).ColorCorrection.Value);
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
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "From hv-01");
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

    static TextBox KeyFileBox(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "~/.ssh/id_ed25519");

    [AvaloniaFact]
    public async Task The_key_file_shows_what_a_folder_sets_and_a_connections_own_value_is_saved()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers", Settings = new InheritableSettings { KeyFile = "~/.ssh/team_key" } };
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh } };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 1100 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        var box = KeyFileBox(window);
        Assert.Equal("~/.ssh/team_key", box.Text);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "From Servers");
        box.Text = "  /keys/box_key  ";
        Assert.True(panel.TrySave());

        Assert.Equal("/keys/box_key", entry.Settings.KeyFile);
        Assert.Equal("/keys/box_key", SettingsResolver.Resolve(entry).KeyFile.Value);
        Assert.Equal("~/.ssh/team_key", folder.Settings.KeyFile);
    }

    [AvaloniaFact]
    public async Task Inheriting_the_key_file_stores_nothing_and_clearing_it_overrides_the_folder()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers", Settings = new InheritableSettings { KeyFile = "~/.ssh/team_key" } };
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh, KeyFile = "/keys/own" } };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 1100 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();
        var box = KeyFileBox(window);
        Assert.Equal("/keys/own", box.Text);
        Assert.True(box.IsEffectivelyEnabled);

        box.Text = string.Empty;
        Assert.True(panel.TrySave());
        Assert.Equal(string.Empty, entry.Settings.KeyFile);
        Assert.Null(SettingsResolver.Resolve(entry).KeyFile.Value);

        UseInherited(box);
        Assert.Equal("~/.ssh/team_key", box.Text);
        Assert.True(panel.TrySave());
        Assert.Null(entry.Settings.KeyFile);
        Assert.Equal("~/.ssh/team_key", SettingsResolver.Resolve(entry).KeyFile.Value);
    }

    [AvaloniaFact]
    public async Task A_key_file_path_with_a_line_break_is_refused_with_a_message()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh } };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 1100 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        KeyFileBox(window).Text = "/keys/a\nb";

        Assert.False(panel.TrySave());
        Assert.Null(entry.Settings.KeyFile);
        Assert.Contains(window.GetVisualDescendants().OfType<Notice>(), n => n.IsEffectivelyVisible && n.Text!.Contains("key file path"));
    }

    [AvaloniaFact]
    public async Task The_key_file_box_and_its_browse_button_are_named_for_a_screen_reader()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "box", Host = "box.example", Settings = new InheritableSettings { Protocol = Protocol.Ssh } };
        tree.Add(entry, tree.Root);
        var panel = new PropertiesPanel(tree, new EmptyCredentialStore());
        var window = new Window { Content = panel, Width = 400, Height = 1100 };
        window.Show();
        await panel.ShowAsync(entry);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Key file", AccessibilityAudit.Peer(KeyFileBox(window)).GetName());
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => AccessibilityAudit.Peer(b).GetName() == "Browse for a key file");
        AccessibilityAudit.AssertAllNamed(window);
    }

    [Fact]
    public void A_file_under_the_home_folder_is_written_with_a_tilde_and_forward_slashes()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal("~/.ssh/id_ed25519", PropertiesPanel.WithHome(Path.Combine(home, ".ssh", "id_ed25519")));
        Assert.Equal("~/keys/work/id", PropertiesPanel.WithHome(Path.Combine(home, "keys", "work", "id")));

        // Beside the home folder, not under it.
        var outside = Path.Combine(Path.GetDirectoryName(home.TrimEnd(Path.DirectorySeparatorChar))!, Path.GetFileName(home.TrimEnd(Path.DirectorySeparatorChar)) + "-other", "id");
        Assert.Equal(outside, PropertiesPanel.WithHome(outside));
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
