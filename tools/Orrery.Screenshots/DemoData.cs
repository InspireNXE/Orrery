using Orrery.Core.Connections;
using Orrery.Core.Settings;
using Orrery.Core.Storage;

namespace Orrery.Screenshots;

/// <summary>Made-up connections under the reserved .example domain, written to a fresh data folder before the window opens.</summary>
sealed class DemoData
{
    public required ConnectionTree Tree { get; init; }
    public required ConnectionEntry Web01 { get; init; }
    public required ConnectionEntry BuildAgent { get; init; }
    public required ConnectionEntry Db02 { get; init; }
    public required ConnectionEntry Web02 { get; init; }
    public required ConnectionEntry App03 { get; init; }

    /// <summary>Hosts the fake host check reports as down.</summary>
    public static readonly IReadOnlySet<string> DownHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "win-test.lab.example", "kiosk.lab.example",
    };

    public static DemoData Write(DataLocation location)
    {
        var tree = ConnectionTree.Empty();

        // Production's connections take SSH and the username from their folder.
        var production = new ConnectionFolder { Name = "Production", Settings = { Protocol = Protocol.Ssh, Username = "demo" } };
        tree.Add(production, tree.Root);
        var bastion = new ConnectionEntry { Name = "bastion", Host = "bastion.prod.example" };
        var web01 = new ConnectionEntry { Name = "web-01", Host = "web-01.prod.example" };
        var db02 = new ConnectionEntry { Name = "db-02", Host = "db-02.prod.example" };
        tree.Add(bastion, production);
        tree.Add(web01, production);
        var web02 = new ConnectionEntry { Name = "web-02", Host = "web-02.prod.example" };
        tree.Add(web02, production);
        tree.Add(db02, production);
        var app03 = Rdp("app-03", "app-03.prod.example");
        app03.Settings.JumpHostId = bastion.Id; // Can't be checked from here: the dashed ring.
        tree.Add(app03, production);

        var lab = new ConnectionFolder { Name = "Lab", Settings = { Username = "demo" } };
        tree.Add(lab, tree.Root);
        var hypervisor = Rdp("hv-01", "hv-01.lab.example");
        tree.Add(hypervisor, lab);
        tree.AddGuest(Rdp("win-test", "win-test.lab.example"), hypervisor);
        tree.AddGuest(Ssh("ubuntu-vm", "ubuntu-vm.lab.example"), hypervisor);
        var buildAgent = Ssh("build-agent", "build-agent.lab.example");
        tree.Add(buildAgent, lab);
        tree.Add(new ConnectionEntry { Name = "kiosk", Host = "kiosk.lab.example", Settings = { Protocol = Protocol.Vnc } }, lab);
        tree.Add(new ConnectionEntry { Name = "core-switch", Host = "core-switch.lab.example", Settings = { Protocol = Protocol.Telnet } }, lab);

        web01.Notes = "Front-end web server. Deploys from build-agent.";
        web01.MacAddress = "02:00:5e:10:00:11";

        // Production's commands reach every server in it; restarting asks first.
        production.Commands =
        [
            new SavedCommand { Name = "Restart nginx", Text = "sudo systemctl restart nginx", Confirm = true },
            new SavedCommand { Name = "Follow the access log", Text = "tail -f /var/log/nginx/access.log" },
            new SavedCommand { Name = "Disk space", Text = "df -h /srv" },
        ];

        // One command for every terminal, and one saved on two connections at once (linked copies).
        tree.Root.Commands = [new SavedCommand { Name = "Uptime", Text = "uptime" }];
        SavedCommands.Save([], new SavedCommand { Name = "Pending updates", Text = "apt list --upgradable" }, [web01, buildAgent]);

        ConnectionStore.In(location).Save(tree);
        AppSettingsStore.In(location).Save(new AppSettings
        {
            // The real host check stays off; the screenshots swap in a fake one that never touches the network.
            HostStatusChecks = false,
            RestoreSessions = RestoreSessionsMode.Never,
            SettingsPage = "Appearance",
            RecordingNoteShown = true,
        });

        var history = ConnectionHistory.In(location);
        history.RecordConnected(web01.Id, DateTimeOffset.Now.AddDays(-3));
        history.RecordConnected(db02.Id, DateTimeOffset.Now.AddDays(-12));

        return new DemoData { Tree = tree, Web01 = web01, BuildAgent = buildAgent, Db02 = db02, Web02 = web02, App03 = app03 };
    }

    static ConnectionEntry Ssh(string name, string host) => new() { Name = name, Host = host, Settings = { Protocol = Protocol.Ssh } };

    static ConnectionEntry Rdp(string name, string host) => new() { Name = name, Host = host, Settings = { Protocol = Protocol.Rdp } };
}
