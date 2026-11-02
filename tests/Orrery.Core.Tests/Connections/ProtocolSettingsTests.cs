using Orrery.Core.Connections;
using Orrery.Core.Network;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Connections;

/// <summary>The per-protocol settings: RDP audio, drives, monitors and admin session; VNC view only and scaling; SSH keep-alive and SOCKS; serial line.</summary>
public class ProtocolSettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    ConnectionStore Store() => new(Path.Combine(_dir, "connections.json"), Path.Combine(_dir, "backups"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Nothing_set_gives_the_defaults()
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "a", Host = "a.example" };
        tree.Add(entry, tree.Root);

        var resolved = SettingsResolver.Resolve(entry);
        Assert.Equal(RemoteAudio.PlayHere, resolved.RemoteAudio.Value);
        Assert.False(resolved.ShareAllDrives.Value);
        Assert.Empty(resolved.SharedFolders.Value);
        Assert.Equal(FullScreenOn.ThisDisplay, resolved.FullScreen.Value.On);
        Assert.False(resolved.AdminSession.Value);
        Assert.False(resolved.ViewOnly.Value);
        Assert.Equal(DesktopScaling.Fit, resolved.Scaling.Value);
        Assert.Equal(30, resolved.KeepAliveSeconds.Value);
        Assert.Null(resolved.SocksProxy.Value);
        Assert.Equal(9600, resolved.BaudRate.Value);
        Assert.Equal(8, resolved.DataBits.Value);
        Assert.Equal(SerialParity.None, resolved.Parity.Value);
        Assert.Equal(SerialStopBits.One, resolved.StopBits.Value);
        Assert.Equal(SerialFlowControl.None, resolved.FlowControl.Value);
        Assert.True(resolved.KeepAliveSeconds.IsDefault);
    }

    [Fact]
    public void A_folders_settings_reach_every_connection_under_it()
    {
        var tree = ConnectionTree.Empty();
        var lab = new ConnectionFolder
        {
            Name = "Lab",
            Settings = { SocksProxy = "1080", KeepAliveSeconds = 10, BaudRate = 115200, SharedFolders = ["~/Shared"], Scaling = DesktopScaling.Fit },
        };
        var ssh = new ConnectionEntry { Name = "ssh", Host = "ssh.lab", Settings = { Protocol = Protocol.Ssh } };
        var own = new ConnectionEntry { Name = "own", Host = "own.lab", Settings = { SocksProxy = "", SharedFolders = [] } };
        tree.Add(lab, tree.Root);
        tree.Add(ssh, lab);
        tree.Add(own, lab);

        var resolved = SettingsResolver.Resolve(ssh);
        Assert.Equal("1080", resolved.SocksProxy.Value);
        Assert.Same(lab, resolved.SocksProxy.Source);
        Assert.Equal(10, resolved.KeepAliveSeconds.Value);
        Assert.Equal(115200, resolved.BaudRate.Value);
        Assert.Equal(["~/Shared"], resolved.SharedFolders.Value);
        Assert.Equal(DesktopScaling.Fit, resolved.Scaling.Value);

        // Empty set on the connection itself means none, overriding the folder's.
        var overridden = SettingsResolver.Resolve(own);
        Assert.Empty(overridden.SharedFolders.Value);
        Assert.Same(own, overridden.SharedFolders.Source);
    }

    [Fact]
    public void A_copy_of_the_settings_never_shares_the_folder_list()
    {
        var settings = new InheritableSettings { SharedFolders = ["C:\\"] };

        var clone = settings.Clone();
        clone.SharedFolders!.Add("D:\\");
        Assert.Equal(["C:\\"], settings.SharedFolders);

        var filled = new InheritableSettings();
        filled.FillFrom(settings);
        filled.SharedFolders!.Add("E:\\");
        Assert.Equal(["C:\\"], settings.SharedFolders);
    }

    [Theory]
    [InlineData(Protocol.Serial, 0)]
    [InlineData(Protocol.Http, 80)]
    [InlineData(Protocol.Https, 443)]
    public void New_protocols_have_their_own_ports(Protocol protocol, int port)
    {
        Assert.Equal(port, ProtocolInfo.DefaultPort(protocol));
    }

    [Fact]
    public void Only_web_opens_in_the_browser_and_only_serial_stays_off_the_network()
    {
        Assert.True(ProtocolInfo.IsWeb(Protocol.Http));
        Assert.True(ProtocolInfo.IsWeb(Protocol.Https));
        Assert.False(ProtocolInfo.IsWeb(Protocol.Rdp));
        Assert.False(ProtocolInfo.UsesNetwork(Protocol.Serial));
        Assert.True(ProtocolInfo.UsesNetwork(Protocol.Https));
    }

    // ---- The file ----

    [Fact]
    public void A_tree_using_nothing_new_stays_on_the_old_schema()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "a", Host = "a.example", Settings = { Protocol = Protocol.Ssh } }, tree.Root);

        Store().Save(tree);
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(Path.Combine(_dir, "connections.json")));
    }

    [Fact]
    public void A_new_setting_anywhere_saves_schema_3()
    {
        var tree = ConnectionTree.Empty();
        var lab = new ConnectionFolder { Name = "Lab", Settings = { KeepAliveSeconds = 15 } };
        tree.Add(lab, tree.Root);
        tree.Add(new ConnectionEntry { Name = "a", Host = "a.example" }, lab);

        Store().Save(tree);
        Assert.Contains("\"schemaVersion\": 3", File.ReadAllText(Path.Combine(_dir, "connections.json")));
    }

    [Theory]
    [InlineData(Protocol.Serial)]
    [InlineData(Protocol.Http)]
    [InlineData(Protocol.Https)]
    public void A_new_protocol_saves_schema_3(Protocol protocol)
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "a", Host = "a", Settings = { Protocol = protocol } }, tree.Root);

        Store().Save(tree);
        Assert.Contains("\"schemaVersion\": 3", File.ReadAllText(Path.Combine(_dir, "connections.json")));
    }

    [Fact]
    public void The_nearest_full_screen_choice_wins_whether_its_every_display_or_chosen_ones()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Lab", Settings = { ChosenDisplays = [new(0, 0, 1920, 1080)] } };
        var entry = new ConnectionEntry { Name = "box", Host = "box.example" };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        var chosen = SettingsResolver.Resolve(entry).FullScreen;
        Assert.Equal(FullScreenOn.ChosenDisplays, chosen.Value.On);
        Assert.Same(folder, chosen.Source);

        entry.Settings.UseAllMonitors = true;
        Assert.Equal(FullScreenChoice.EveryDisplay, SettingsResolver.Resolve(entry).FullScreen.Value);
        entry.Settings.UseAllMonitors = false;
        Assert.Equal(FullScreenChoice.ThisDisplay, SettingsResolver.Resolve(entry).FullScreen.Value);
    }

    [Fact]
    public void Chosen_displays_are_found_by_position_or_else_by_name()
    {
        var choice = new FullScreenChoice(FullScreenOn.ChosenDisplays, [new(0, 0, 1920, 1080, "DELL"), new(4480, 0, 1080, 1920, "PORTRAIT"), new(9000, 0, 800, 600)]);
        ScreenArea[] screens = [new(0, 0, 1920, 1080, true), new(1920, 0, 2560, 1440, false), new(4480, 0, 1200, 1920, false)];

        // The portrait one changed resolution, so it's found by name; the third isn't connected.
        Assert.Equal([0, 2], choice.Pick(screens, ["DELL", "LG", "PORTRAIT"], out int missing));
        Assert.Equal(1, missing);
    }

    [Fact]
    public void Every_new_setting_survives_saving_and_loading()
    {
        var tree = ConnectionTree.Empty();
        var rdp = new ConnectionEntry
        {
            Name = "rdp",
            Host = "rdp.example",
            Settings =
            {
                Protocol = Protocol.Rdp, RemoteAudio = RemoteAudio.DoNotPlay, ShareAllDrives = false, SharedFolders = ["C:\\Work", "~/Shared"],
                UseAllMonitors = true, AdminSession = true,
            },
        };
        var vnc = new ConnectionEntry
        {
            Name = "vnc",
            Host = "vnc.example",
            Settings = { Protocol = Protocol.Vnc, ViewOnly = true, Scaling = DesktopScaling.ActualSize, ChosenDisplays = [new(1920, 0, 2560, 1440, "LG 27GP850")] },
        };
        var ssh = new ConnectionEntry { Name = "ssh", Host = "ssh.example", Settings = { Protocol = Protocol.Ssh, KeepAliveSeconds = 0, SocksProxy = "127.0.0.1:1080" } };
        var serial = new ConnectionEntry
        {
            Name = "console",
            Host = "COM3",
            Settings =
            {
                Protocol = Protocol.Serial, BaudRate = 115200, DataBits = 7, Parity = SerialParity.Even, StopBits = SerialStopBits.Two,
                FlowControl = SerialFlowControl.RtsCts,
            },
        };
        var web = new ConnectionEntry { Name = "router", Host = "router.lan/admin", Settings = { Protocol = Protocol.Https } };
        foreach (var entry in new[] { rdp, vnc, ssh, serial, web })
        {
            tree.Add(entry, tree.Root);
        }

        Store().Save(tree);
        var loaded = Store().Load();

        InheritableSettings Loaded(ConnectionEntry entry) => Assert.IsType<ConnectionEntry>(loaded.Find(entry.Id)).Settings;
        Assert.Equal(RemoteAudio.DoNotPlay, Loaded(rdp).RemoteAudio);
        Assert.False(Loaded(rdp).ShareAllDrives);
        Assert.Equal(["C:\\Work", "~/Shared"], Loaded(rdp).SharedFolders);
        Assert.True(Loaded(rdp).UseAllMonitors);
        Assert.True(Loaded(rdp).AdminSession);
        Assert.True(Loaded(vnc).ViewOnly);
        Assert.Equal(DesktopScaling.ActualSize, Loaded(vnc).Scaling);
        Assert.Equal([new ChosenDisplay(1920, 0, 2560, 1440, "LG 27GP850")], Loaded(vnc).ChosenDisplays);
        Assert.Equal(0, Loaded(ssh).KeepAliveSeconds);
        Assert.Equal("127.0.0.1:1080", Loaded(ssh).SocksProxy);
        Assert.Equal(Protocol.Serial, Loaded(serial).Protocol);
        Assert.Equal(115200, Loaded(serial).BaudRate);
        Assert.Equal(7, Loaded(serial).DataBits);
        Assert.Equal(SerialParity.Even, Loaded(serial).Parity);
        Assert.Equal(SerialStopBits.Two, Loaded(serial).StopBits);
        Assert.Equal(SerialFlowControl.RtsCts, Loaded(serial).FlowControl);
        Assert.Equal(Protocol.Https, Loaded(web).Protocol);
        Assert.Equal("router.lan/admin", Assert.IsType<ConnectionEntry>(loaded.Find(web.Id)).Host);
    }

    // ---- SOCKS ----

    [Theory]
    [InlineData("1080", null, 1080)]
    [InlineData("D 1080", null, 1080)]
    [InlineData(" d 9050 ", null, 9050)]
    [InlineData("127.0.0.1:1080", "127.0.0.1", 1080)]
    public void Reads_a_socks_proxy_as_a_dynamic_forward(string text, string? address, int port)
    {
        var forward = PortForward.TryParseSocks(text, out var error);
        Assert.Null(error);
        Assert.Equal(new PortForward(ForwardKind.Dynamic, port, ListenAddress: address), forward);
    }

    [Theory]
    [InlineData("socks")]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("L 1080:db:5432")]
    public void Refuses_a_socks_proxy_that_is_not_a_port(string text)
    {
        Assert.Null(PortForward.TryParseSocks(text, out var error));
        Assert.Contains("should be a port", error);
    }

    [Fact]
    public void A_folders_socks_proxy_is_added_to_a_connections_own_forwards()
    {
        var own = new[] { new PortForward(ForwardKind.Local, 5432, "localhost", 5432) };

        var forwards = PortForward.WithSocks(own, "1080");
        Assert.Equal([own[0], new PortForward(ForwardKind.Dynamic, 1080)], forwards);
        Assert.Equal(own, PortForward.WithSocks(own, null));
        Assert.Equal(own, PortForward.WithSocks(own, ""));
        Assert.Equal(own, PortForward.WithSocks(own, "not a port"));
        Assert.Equal([new PortForward(ForwardKind.Dynamic, 1080)], PortForward.WithSocks(null, "1080"));
    }

    [Fact]
    public void A_connection_already_listening_on_the_proxys_port_keeps_its_own()
    {
        var local = new[] { new PortForward(ForwardKind.Local, 1080, "localhost", 80) };
        Assert.Equal(local, PortForward.WithSocks(local, "1080"));

        // A remote forward listens on the server, so the port here is still free.
        var remote = new[] { new PortForward(ForwardKind.Remote, 1080, "localhost", 80) };
        Assert.Equal(2, PortForward.WithSocks(remote, "1080").Count);
    }

    // ---- Host check ----

    [Fact]
    public void A_serial_port_is_not_checked_on_the_network()
    {
        var tree = ConnectionTree.Empty();
        var serial = new ConnectionEntry { Name = "console", Host = "COM3", Settings = { Protocol = Protocol.Serial } };
        tree.Add(serial, tree.Root);

        Assert.Null(ProbeTarget.For(serial, out var why));
        Assert.Contains("serial port", why);
    }

    [Fact]
    public void A_web_page_is_checked_at_its_host_without_the_path()
    {
        var tree = ConnectionTree.Empty();
        var web = new ConnectionEntry { Name = "router", Host = "router.lan:8443/admin?tab=1", Settings = { Protocol = Protocol.Https } };
        var plain = new ConnectionEntry { Name = "wiki", Host = "wiki.lan/start", Settings = { Protocol = Protocol.Http } };
        tree.Add(web, tree.Root);
        tree.Add(plain, tree.Root);

        var target = Assert.IsType<ProbeTarget>(ProbeTarget.For(web, out _));
        Assert.Equal("router.lan", target.Host);
        Assert.Equal(8443, target.Port);
        target = Assert.IsType<ProbeTarget>(ProbeTarget.For(plain, out _));
        Assert.Equal("wiki.lan", target.Host);
        Assert.Equal(80, target.Port);
    }
}
