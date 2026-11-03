using System.Reflection;
using System.Text;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.App.Credentials;
using Orrery.App.Import;
using Orrery.App.Input;
using Orrery.App.Security;
using Orrery.App.Sessions;
using Orrery.App.Settings;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Orrery.Protocols.Ssh;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Every control a screen reader can land on says what it is.</summary>
public class AccessibilityTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    DataLocation Location()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        return location;
    }

    sealed class TextSession : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("box.example", 22, "admin", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived;
        public void Output(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public void The_main_window_with_a_session_and_a_selected_connection_has_no_unnamed_controls()
    {
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(Location()) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var folder = new ConnectionFolder { Name = "Servers" };
            tree.Add(folder, tree.Root);
            var entry = new ConnectionEntry { Name = "web01", Host = "web01.example" };
            tree.Add(entry, folder);
            Dispatcher.UIThread.RunJobs();

            var treeView = VaultAutoLockTests.Private<TreeView>(window, "_treeView");
            treeView.SelectedItem = treeView.GetVisualDescendants().OfType<TreeViewItem>().Select(i => i.DataContext).OfType<NodeViewModel>().First(r => r.Node == entry);
            var addTab = typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var session = new TextSession();
            addTab.Invoke(window, [new TerminalView(session), session, new SessionOrigin("web01", Protocol.Ssh, entry.Id, () => Task.CompletedTask)]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            AccessibilityAudit.AssertAllNamed(window);

            // Tab alone gets from the top of the window to every part of it.
            var order = AccessibilityAudit.TabOrder(window.FindControl<Button>("QuickConnectButton")!);
            Assert.Contains(order, c => c.Name == "FilterBox");
            Assert.Contains(order, c => c is ConnectionTreeItem);
            Assert.Contains(order, c => c is SessionTabHeader);
            Assert.Contains(order, c => c is TerminalView);
            Assert.Contains(order, c => c.Name == "SettingsRailButton");

            // The terminal keeps Tab for the shell; the escape key gets out to its tab.
            var terminal = window.GetVisualDescendants().OfType<TerminalView>().Single();
            terminal.Focus();
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Assert.Same(terminal, window.FocusManager!.GetFocusedElement());
            var escape = SessionEscape.Gesture;
            window.KeyPress(escape.Key, (RawInputModifiers)escape.KeyModifiers, PhysicalKey.Home, null);
            Assert.IsType<SessionTabHeader>(window.FocusManager.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("CredentialsRailButton")]
    [InlineData("RecentRailButton")]
    public void The_other_side_panels_have_no_unnamed_controls(string rail)
    {
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(Location()) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            window.FindControl<Button>(rail)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            AccessibilityAudit.AssertAllNamed(window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tree_rows_give_their_name_what_they_are_and_whether_they_are_open_and_can_be_opened_and_selected()
    {
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(Location()) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var folder = new ConnectionFolder { Name = "Servers" };
            tree.Add(folder, tree.Root);
            var entry = new ConnectionEntry { Name = "web01", Host = "web01.example" };
            tree.Add(entry, folder);
            Dispatcher.UIThread.RunJobs();

            var treeView = VaultAutoLockTests.Private<TreeView>(window, "_treeView");
            var rows = treeView.GetVisualDescendants().OfType<ConnectionTreeItem>().ToList();
            var folderRow = rows.Single(r => r.DataContext is NodeViewModel { Node: ConnectionFolder });
            var entryRow = rows.Single(r => r.DataContext is NodeViewModel { Node: ConnectionEntry });
            var folderPeer = (ConnectionTreeItemPeer)ControlAutomationPeer.CreatePeerForElement(folderRow);
            var entryPeer = (ConnectionTreeItemPeer)ControlAutomationPeer.CreatePeerForElement(entryRow);

            Assert.Equal(AutomationControlType.TreeItem, folderPeer.GetAutomationControlType());
            Assert.Equal("Servers", folderPeer.GetName());
            Assert.Equal("Folder, 1 connection", folderPeer.GetHelpText());
            Assert.Equal(0, folderPeer.Level);
            Assert.Equal("web01", entryPeer.GetName());
            Assert.Contains("web01.example", entryPeer.GetHelpText());
            Assert.Equal(1, entryPeer.Level);
            Assert.Equal(ExpandCollapseState.LeafNode, entryPeer.ExpandCollapseState);

            var states = new List<object?>();
            folderPeer.PropertyChanged += (_, e) =>
            {
                if (e.Property == ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty)
                {
                    states.Add(e.NewValue);
                }
            };
            folderPeer.Collapse();
            Assert.False(folderRow.IsExpanded);
            Assert.Equal(ExpandCollapseState.Collapsed, folderPeer.ExpandCollapseState);
            folderPeer.Expand();
            Assert.True(folderRow.IsExpanded);
            Assert.Equal([ExpandCollapseState.Collapsed, ExpandCollapseState.Expanded], states);

            Dispatcher.UIThread.RunJobs();
            entryPeer.Select();
            Assert.Same(entryRow.DataContext, treeView.SelectedItem);
            Assert.True(entryPeer.IsSelected);
            Assert.False(folderPeer.IsSelected);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_password_box_gives_automation_a_dot_per_character_and_never_the_password()
    {
        var box = new PasswordBox { Text = "hunter22" };
        var window = new Window { Content = box };
        window.Show();
        var peer = ControlAutomationPeer.CreatePeerForElement(box);
        var seen = new List<string>();
        peer.PropertyChanged += (_, e) => seen.AddRange(new[] { e.OldValue, e.NewValue }.OfType<string>());

        Assert.Equal("••••••••", peer.GetProvider<IValueProvider>()!.Value);
        box.Text = "swordfish";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("•••••••••", peer.GetProvider<IValueProvider>()!.Value);
        Assert.Contains("•••••••••", seen);
        Assert.DoesNotContain(seen, s => s.Contains("hunter") || s.Contains("sword"));
        Assert.DoesNotContain("sword", peer.GetName() ?? string.Empty);
        window.Close();
    }

    [AvaloniaFact]
    public void A_session_tab_is_a_tab_with_its_name_its_state_and_whether_it_is_the_current_one()
    {
        var tab = new SessionTab(new TextSession(), new Border(), new SessionOrigin("web01", Protocol.Ssh, null, () => Task.CompletedTask));
        var window = new Window { Content = tab.Header };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var peer = ControlAutomationPeer.CreatePeerForElement(tab.Header);
        var selection = peer.GetProvider<ISelectionItemProvider>()!;

        Assert.Equal(AutomationControlType.TabItem, peer.GetAutomationControlType());
        Assert.Equal("web01, SSH", peer.GetName());
        Assert.Equal("Connected", peer.GetItemStatus());
        Assert.Equal("admin@box.example", peer.GetHelpText());
        Assert.False(selection.IsSelected);

        bool raised = false;
        peer.PropertyChanged += (_, e) => raised |= e.Property == SelectionItemPatternIdentifiers.IsSelectedProperty;
        tab.IsSelected = true;
        Assert.True(selection.IsSelected);
        Assert.True(raised);

        bool chosen = false;
        tab.Selected += (_, _) => chosen = true;
        selection.Select();
        Assert.True(chosen);
        window.Close();
    }

    [AvaloniaFact]
    public void Quick_connect_reads_out_the_result_the_arrow_keys_move_to()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "web-01", Host = "web-01.corp" }, tree.Root);
        var owner = new Window();
        owner.Show();
        var window = new QuickConnectWindow(tree, [new OpenSession("web-01", "admin@web-01.corp", Protocol.Ssh)]);
        _ = window.ShowOver(owner);
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<TextBox>().Single().Text = "web";
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("web-01, RDP, web-01.corp. Connect. 2 of 5", window.Spoken);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        Assert.Equal("web-01, open SSH session, admin@web-01.corp. Switch to. 1 of 5", window.Spoken);
        window.Close();
        owner.Close();
    }

    [Fact]
    public void A_file_row_is_read_as_its_name_kind_size_and_date()
    {
        var modified = new DateTime(2026, 1, 2, 3, 4, 0);
        Assert.Equal("notes.txt, file, 100 B, modified 2026-01-02 03:04",
            FileBrowserView.Spoken(new SftpItem("notes.txt", "/home/me/notes.txt", false, false, 100, modified, "rw-r--r--")));
        Assert.Equal("src, folder, modified 2026-01-02 03:04",
            FileBrowserView.Spoken(new SftpItem("src", "/home/me/src", true, false, 4096, modified, "rwxr-xr-x")));
    }

    [AvaloniaFact]
    public void Settings_have_no_unnamed_controls()
    {
        var location = Location();
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var view = new SettingsView(location, AppSettingsStore.In(location), vault, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AccessibilityAudit.AssertAllNamed(window);
        window.Close();
    }

    public static TheoryData<string> Dialogs() => ["connect", "hostdelete", "export", "master", "unlock", "import", "certificate", "hostkey", "portable", "quick", "password", "text", "message"];

    [AvaloniaTheory]
    [MemberData(nameof(Dialogs))]
    public void Dialogs_have_no_unnamed_controls(string which)
    {
        var location = Location();
        using var vault = new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: CredentialStores.DefaultAutoLock);
        var owner = new Window();
        owner.Show();
        Window window = which switch
        {
            "connect" => new ConnectPromptWindow(vault, "web01", "web01.example", "admin", null, offerRemember: true),
            "export" => new ExportSecretsWindow(ExportFormat.Orrery),
            "master" => new ChangeMasterPasswordWindow(vault),
            "unlock" => new VaultUnlockWindow(vault),
            "import" => new ImportPreviewWindow(new ImportResult(new ConnectionFolder { Name = "Imported" }, [],
                new ImportReport("confCons.xml", 1, 2, 0, 0, new Dictionary<string, int> { ["RDP"] = 2 }, ["raw1: RAW isn't supported"], [], [])), 0, "the vault file"),
            "certificate" => new CertificatePromptWindow(new CertificatePrompt(CertificatePromptKind.Changed, "rdp.example", 3389, "CN=rdp", "CN=ca", DateTime.Now, DateTime.Now.AddYears(1), "AB12", "CD34", [])),
            "hostkey" => new HostKeyPromptWindow(new HostKeyPrompt(CertificatePromptKind.Changed, "ssh.example", 22, "ssh-ed25519", "SHA256:abc", "SHA256:def")),
            "portable" => new Orrery.App.Setup.SetupWizard(new Orrery.App.Setup.PortableSetup(_dir, PlatformFolders.ForCurrentOs()), "Windows Credential Manager", []),
            "quick" => new QuickConnectWindow(ConnectionTree.Empty()),
            "hostdelete" => HostDelete(),
            _ => null!,
        };

        if (which is "password" or "text" or "message")
        {
            _ = which switch
            {
                "password" => Orrery.App.Dialogs.PromptPasswordAsync(owner, "Unlock", "Enter it"),
                "text" => Orrery.App.Dialogs.PromptTextAsync(owner, "Rename", "Name", "web01", "Rename"),
                _ => Orrery.App.Dialogs.ShowMessageAsync(owner, "Done", "It worked"),
            };
            Dispatcher.UIThread.RunJobs();
            window = owner.OwnedWindows.Single();
        }
        else
        {
            window.Show(owner);
        }

        Dispatcher.UIThread.RunJobs();
        AccessibilityAudit.AssertAllNamed(window);
        window.Close();
        owner.Close();
    }

    sealed class Desktop : IFrameBufferSession
    {
        public ConnectionTarget Target { get; } = new("desk.example", 3389, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated { add { } remove { } }
        public int Width => 64;
        public int Height => 48;
        public List<ushort> Keys { get; } = [];
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) { }
        public void SendKey(ushort scancode, bool pressed) => Keys.Add(scancode);
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public void Ctrl_alt_home_leaves_a_full_screen_remote_desktop_for_its_tab_and_enter_goes_back_in()
    {
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(Location()) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            var desktop = new Desktop();
            var view = new RemoteFrameView(desktop);
            var addTab = typeof(MainWindow).GetMethod("AddTab", BindingFlags.Instance | BindingFlags.NonPublic)!;
            addTab.Invoke(window, [view, desktop, new SessionOrigin("desk", Protocol.Rdp, null, () => Task.CompletedTask)]);
            Dispatcher.UIThread.RunJobs();
            var peer = ControlAutomationPeer.CreatePeerForElement(view);
            Assert.Equal("Remote desktop: desk", peer.GetName());
            Assert.Equal($"{SessionEscape.Description} moves to the session's tab.", peer.GetHelpText());

            typeof(MainWindow).GetMethod("EnterFullScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [null, null]);
            view.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(view, window.FocusManager!.GetFocusedElement());

            var escape = SessionEscape.Gesture;
            window.KeyPress(escape.Key, (RawInputModifiers)escape.KeyModifiers, PhysicalKey.Home, null);
            Dispatcher.UIThread.RunJobs();

            var header = window.GetVisualDescendants().OfType<SessionTabHeader>().Single();
            Assert.Same(header, window.FocusManager.GetFocusedElement());
            Assert.False(VaultAutoLockTests.Private<bool>(window, "_fullScreen"));
            Assert.Empty(desktop.Keys);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(view, window.FocusManager.GetFocusedElement());
        }
        finally
        {
            window.Close();
        }
    }

    static HostDeleteWindow HostDelete()
    {
        var tree = ConnectionTree.Empty();
        var host = new ConnectionEntry { Name = "hv-01", Host = "hv-01.lab" };
        tree.Add(host, tree.Root);
        tree.AddGuest(new ConnectionEntry { Name = "vm-a", Host = "vm-a.lab" }, host);
        return new HostDeleteWindow(host, tree);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
