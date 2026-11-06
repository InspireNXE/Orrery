using Orrery.Core.Connections;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Connections;

/// <summary>Values that belong to one computer, in a data folder two computers share through a sync app.</summary>
public class PerComputerFieldsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("orrery-percomputer-").FullName;

    string Shared => Path.Combine(_dir, "connections.json");

    ConnectionStore Store(string computer) => new(Shared, Path.Combine(_dir, "backups"),
        computerValuesPath: Path.Combine(_dir, ComputerFiles.FolderName, computer + "-connections.json"));

    (ConnectionStore Store, ConnectionTree Tree) Open(string computer)
    {
        var store = Store(computer);
        return (store, store.Load());
    }

    static T Named<T>(ConnectionTree tree, string name) where T : ConnectionNode => (T)tree.Root.Descendants().Single(n => n.Name == name);

    void Start(params ConnectionNode[] nodes)
    {
        var tree = ConnectionTree.Empty();
        foreach (var node in nodes)
        {
            tree.Add(node, tree.Root);
        }

        Store("laptop").Save(tree);
    }

    [Fact]
    public void Each_computer_keeps_its_own_key_file_and_the_rest_is_shared()
    {
        Start(new ConnectionEntry { Name = "web", Host = "web.example", Settings = { KeyFile = @"C:\Users\me\.ssh\id_ed25519" } });
        var laptop = Open("laptop");
        var desktop = Open("desktop");

        Named<ConnectionEntry>(desktop.Tree, "web").Settings.KeyFile = @"D:\keys\web";
        Named<ConnectionEntry>(desktop.Tree, "web").Notes = "from the desktop";
        var report = desktop.Store.Save(desktop.Tree);
        Assert.Null(report);

        // The desktop's tree still has its own key file after the save.
        Assert.Equal(@"D:\keys\web", Named<ConnectionEntry>(desktop.Tree, "web").Settings.KeyFile);

        laptop.Store.ReloadIfChanged(laptop.Tree);
        var onLaptop = Named<ConnectionEntry>(laptop.Tree, "web");
        Assert.Equal(@"C:\Users\me\.ssh\id_ed25519", onLaptop.Settings.KeyFile);
        Assert.Equal("from the desktop", onLaptop.Notes);

        Assert.Equal(@"D:\keys\web", Named<ConnectionEntry>(Open("desktop").Tree, "web").Settings.KeyFile);
        Assert.Equal(@"C:\Users\me\.ssh\id_ed25519", Named<ConnectionEntry>(Open("someone new").Tree, "web").Settings.KeyFile);
    }

    [Fact]
    public void A_computer_that_started_without_the_file_keeps_its_own_values_when_the_file_comes()
    {
        Start(new ConnectionEntry { Name = "web", Host = "web.example", Settings = { KeyFile = @"C:\keys\shared" } });
        var desktop = Open("desktop");
        Named<ConnectionEntry>(desktop.Tree, "web").Settings.KeyFile = @"D:\keys\web";
        desktop.Store.Save(desktop.Tree);

        // Started again while the file wasn't there yet: its tree starts empty, and the file comes in afterwards.
        var away = Shared + ".away";
        File.Move(Shared, away);
        var again = Open("desktop");
        File.Move(away, Shared);
        again.Store.ReloadIfChanged(again.Tree);

        Assert.Equal(@"D:\keys\web", Named<ConnectionEntry>(again.Tree, "web").Settings.KeyFile);
        Assert.Equal(@"D:\keys\web", Named<ConnectionEntry>(Open("desktop").Tree, "web").Settings.KeyFile);
    }

    // Its tree keeps the root it started with, the file the one it was first saved with: values kept under the root's id
    // were gone once the file was read again.
    [Fact]
    public void A_computer_that_started_without_the_file_keeps_its_own_defaults_for_every_connection()
    {
        var desktop = Open("desktop");
        var laptop = ConnectionTree.Empty();
        laptop.Root.Settings.KeyFile = @"C:\keys\default";
        laptop.Add(new ConnectionEntry { Name = "web", Host = "web.example" }, laptop.Root);
        Store("laptop").Save(laptop);

        desktop.Store.ReloadIfChanged(desktop.Tree);
        Assert.Equal(@"C:\keys\default", desktop.Tree.Root.Settings.KeyFile);
        desktop.Tree.Root.Settings.KeyFile = @"D:\keys\default";
        desktop.Store.Save(desktop.Tree);

        Assert.Equal(@"D:\keys\default", Open("desktop").Tree.Root.Settings.KeyFile);
        Assert.Equal(@"C:\keys\default", Open("laptop").Tree.Root.Settings.KeyFile);
    }

    [Fact]
    public void The_root_s_values_saved_under_its_id_are_still_read()
    {
        Start(new ConnectionEntry { Name = "web", Host = "web.example" });
        var root = Open("laptop").Tree.Root.Id.ToString("N");
        var values = Path.Combine(_dir, ComputerFiles.FolderName, "desktop-connections.json");
        Directory.CreateDirectory(Path.GetDirectoryName(values)!);
        File.WriteAllText(values, $$"""{ "{{root}}": { "settings.keyFile": "D:\\keys\\default" } }""");

        var (store, tree) = Open("desktop");
        Assert.Equal(@"D:\keys\default", tree.Root.Settings.KeyFile);

        store.Save(tree);
        Assert.Equal(@"D:\keys\default", Open("desktop").Tree.Root.Settings.KeyFile);
        Assert.DoesNotContain(root, File.ReadAllText(values));
    }

    [Fact]
    public void A_deleted_connection_s_own_values_go_with_it()
    {
        Start(new ConnectionEntry { Name = "web", Host = "web.example", Settings = { KeyFile = @"C:\keys\shared" } });
        var desktop = Open("desktop");
        var web = Named<ConnectionEntry>(desktop.Tree, "web");
        web.Settings.KeyFile = @"D:\keys\web";
        desktop.Store.Save(desktop.Tree);
        var values = Path.Combine(_dir, ComputerFiles.FolderName, "desktop-connections.json");
        Assert.Contains(web.Id.ToString("N"), File.ReadAllText(values));

        desktop.Tree.Remove(web);
        desktop.Store.Save(desktop.Tree);

        Assert.False(File.Exists(values) && File.ReadAllText(values).Contains(web.Id.ToString("N")));
    }

    [Fact]
    public void Both_computers_changing_their_own_values_is_no_conflict()
    {
        Start(new ConnectionEntry { Name = "rdp", Host = "10.0.0.5", Settings = { SharedFolders = [@"C:\Share"] } });
        var laptop = Open("laptop");
        var desktop = Open("desktop");

        Named<ConnectionEntry>(laptop.Tree, "rdp").Settings.SharedFolders = [@"C:\Work"];
        laptop.Store.Save(laptop.Tree);
        Named<ConnectionEntry>(desktop.Tree, "rdp").Settings.SharedFolders = [@"E:\Projects"];
        var report = desktop.Store.Save(desktop.Tree);

        Assert.True(report is null || report.Conflicts.Count == 0);
        Assert.Equal([@"C:\Work"], Named<ConnectionEntry>(Open("laptop").Tree, "rdp").Settings.SharedFolders!);
        Assert.Equal([@"E:\Projects"], Named<ConnectionEntry>(Open("desktop").Tree, "rdp").Settings.SharedFolders!);
    }

    [Fact]
    public void A_connection_new_on_one_computer_brings_its_values_to_the_other()
    {
        Start();
        var laptop = Open("laptop");
        var desktop = Open("desktop");

        laptop.Tree.Add(new ConnectionEntry { Name = "box", Host = "box", Settings = { KeyFile = "~/.ssh/box" } }, laptop.Tree.Root);
        laptop.Store.Save(laptop.Tree);

        desktop.Store.ReloadIfChanged(desktop.Tree);
        Assert.Equal("~/.ssh/box", Named<ConnectionEntry>(desktop.Tree, "box").Settings.KeyFile);
    }

    [Fact]
    public void Opening_a_folder_in_the_tree_doesn_t_touch_the_shared_file()
    {
        Start(new ConnectionFolder { Name = "Lab" });
        var (store, tree) = Open("desktop");
        var before = File.ReadAllText(Shared);

        Named<ConnectionFolder>(tree, "Lab").Expanded = false;
        store.Save(tree);

        Assert.Equal(before, File.ReadAllText(Shared));
        Assert.False(Named<ConnectionFolder>(Open("desktop").Tree, "Lab").Expanded);
        Assert.True(Named<ConnectionFolder>(Open("laptop").Tree, "Lab").Expanded);
    }

    [Fact]
    public void A_local_shell_or_serial_port_is_each_computer_s_and_a_remote_host_is_shared()
    {
        Start(new ConnectionEntry { Name = "shell", Host = "pwsh", Settings = { Protocol = Protocol.Local } },
            new ConnectionEntry { Name = "console", Host = "COM3", Settings = { Protocol = Protocol.Serial } },
            new ConnectionEntry { Name = "web", Host = "web.example", Settings = { Protocol = Protocol.Ssh } });
        var (store, tree) = Open("desktop");

        Named<ConnectionEntry>(tree, "shell").Host = "cmd";
        Named<ConnectionEntry>(tree, "console").Host = "COM7";
        Named<ConnectionEntry>(tree, "web").Host = "web2.example";
        store.Save(tree);

        var laptop = Open("laptop").Tree;
        Assert.Equal("pwsh", Named<ConnectionEntry>(laptop, "shell").Host);
        Assert.Equal("COM3", Named<ConnectionEntry>(laptop, "console").Host);
        Assert.Equal("web2.example", Named<ConnectionEntry>(laptop, "web").Host);
        Assert.Equal("cmd", Named<ConnectionEntry>(Open("desktop").Tree, "shell").Host);
    }

    [Fact]
    public void Settings_for_this_computer_s_screen_and_tools_stay_on_it()
    {
        var shared = Path.Combine(_dir, "settings.json");
        AppSettingsStore Settings(string computer) => new(shared, Path.Combine(_dir, ComputerFiles.FolderName, computer + "-settings.json"));
        Settings("laptop").Save(new AppSettings { SidePanelWidth = 300, FfmpegPath = @"C:\ffmpeg.exe", TerminalFontSize = 12 });

        var desktop = Settings("desktop");
        desktop.Save(desktop.Load() with { SidePanelWidth = 520, TerminalFontSize = 14 });

        var onLaptop = Settings("laptop").Load();
        Assert.Equal(300, onLaptop.SidePanelWidth);
        Assert.Equal(14, onLaptop.TerminalFontSize);
        var onDesktop = Settings("desktop").Load();
        Assert.Equal(520, onDesktop.SidePanelWidth);
        Assert.Equal(@"C:\ffmpeg.exe", onDesktop.FfmpegPath); // Not set here, so the shared one.
    }

    [Fact]
    public void A_workspace_from_before_files_were_per_computer_is_carried_over()
    {
        File.WriteAllText(Path.Combine(_dir, WorkspaceStore.FileName), "{}");

        var own = ComputerFiles.PathFor(_dir, WorkspaceStore.FileName);

        Assert.Equal(Path.Combine(_dir, ComputerFiles.FolderName, $"{ComputerFiles.Tag}-workspace.json"), own);
        Assert.Equal("{}", File.ReadAllText(own));
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
