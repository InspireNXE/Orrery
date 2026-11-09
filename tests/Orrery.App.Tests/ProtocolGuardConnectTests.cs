using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>
/// What connecting opens for each protocol, pinned, so moving how sessions start into the protocols' descriptors
/// can't change it: the tab's session and view, what it's called, how it reconnects, and the login it was given.
/// Every host is a closed port on this computer, so each session fails at once and nothing leaves the machine.
/// </summary>
/// <remarks>Set ORRERY_UPDATE_GOLDEN=1 to rewrite Golden/connect.txt after a deliberate change.</remarks>
public class ProtocolGuardConnectTests : IDisposable
{
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task Each_protocol_opens_what_it_did()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        var tabs = VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs");
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var vault = Assert.IsType<VaultFileStore>(VaultAutoLockTests.Private<ICredentialStore>(window, "_credentials"));
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            await vault.UnlockAsync("correct horse battery staple");
            var login = new Credential(Guid.NewGuid(), "lab-admin", CredentialKind.Password, "admin", "LAB", "not-a-real-password");
            await vault.SaveAsync(login);

            int port = ClosedPort();
            var folder = new ConnectionFolder { Name = "Lab" };
            folder.Settings.CredentialId = login.Id;
            folder.Settings.ShareClipboard = false;
            tree.Add(folder, tree.Root);

            ConnectionEntry Add(string name, Protocol protocol, Action<ConnectionEntry>? set = null)
            {
                var entry = new ConnectionEntry { Name = name, Host = "127.0.0.1" };
                entry.Settings.Protocol = protocol;
                entry.Settings.Port = port;
                set?.Invoke(entry);
                tree.Add(entry, folder);
                return entry;
            }

            var jump = Add("jump", Protocol.Ssh);
            List<(ConnectionEntry Entry, bool Files)> connects =
            [
                (Add("rdp", Protocol.Rdp, e => e.Settings.AutoReconnect = true), false),
                (Add("rdp-fixed", Protocol.Rdp, e =>
                {
                    e.Settings.DesktopSize = "1280x720";
                    e.Settings.EnlargeRemoteDesktop = true;
                    e.Settings.Scaling = DesktopScaling.ActualSize;
                    e.Settings.ColorCorrection = false;
                }), false),
                (Add("rdp-via-jump", Protocol.Rdp, e => e.Settings.JumpHostId = jump.Id), false),
                (Add("vnc", Protocol.Vnc, e =>
                {
                    e.Settings.ViewOnly = true;
                    e.Settings.Scaling = DesktopScaling.ShrinkToFit;
                    e.Settings.AutoReconnect = true;
                }), false),
                (Add("ssh", Protocol.Ssh), false),
                (Add("ssh-files", Protocol.Ssh), true),
                (Add("ssh-via-jump", Protocol.Ssh, e => e.Settings.JumpHostId = jump.Id), false),
                (Add("telnet", Protocol.Telnet, e => e.Settings.Username = "operator"), false),
                (Add("serial", Protocol.Serial, e =>
                {
                    e.Host = "ORRERY-NO-SUCH-PORT";
                    e.Settings.BaudRate = 115200;
                }), false),
            ];
            Dispatcher.UIThread.RunJobs();

            var connect = typeof(MainWindow).GetMethod("ConnectAsync", Hidden)!;
            var lines = new List<string>();
            foreach (var (entry, files) in connects)
            {
                int before = tabs.Count;
                await (Task)connect.Invoke(window, [entry, false, files])!;
                Dispatcher.UIThread.RunJobs();
                var tab = Assert.Single(tabs.Skip(before));
                lines.Add(Describe(entry.Name, tab, entry.Id));
            }

            var direct = typeof(MainWindow).GetMethod("ConnectDirectAsync", Hidden)!;
            foreach (var choice in new[]
            {
                new QuickConnectChoice(null, "127.0.0.1", port, Protocol.Telnet, "operator"),
                new QuickConnectChoice(null, "ORRERY-NO-SUCH-PORT", 0, Protocol.Serial, null),
            })
            {
                int before = tabs.Count;
                await (Task)direct.Invoke(window, [choice, false])!;
                Dispatcher.UIThread.RunJobs();
                var tab = Assert.Single(tabs.Skip(before));
                lines.Add(Describe($"quick {choice.Protocol}", tab, null));
            }

            ProtocolGuardUiTests.Golden("connect.txt", string.Join("\n", lines) + "\n");
        }
        finally
        {
            var close = typeof(MainWindow).GetMethod("CloseTabAsync", Hidden, [typeof(IConnectionSession)])!;
            foreach (var session in tabs.Select(t => t.Session).ToList())
            {
                await (Task)close.Invoke(window, [session])!;
            }

            window.Close();
        }
    }

    [AvaloniaFact]
    public void Only_an_ssh_tab_offers_its_files_not_a_saved_local_shell_whose_tab_says_ssh()
    {
        var location = new DataLocation(StorageMode.Portable, Path.Combine(_dir, "data"), Path.Combine(_dir, "cache"), Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(location.DataDir);
        VaultAutoLockTests.AddMainWindowResources();
        var window = new MainWindow(location) { Width = 1400, Height = 900 };
        try
        {
            window.Show();
            var tree = VaultAutoLockTests.Private<ConnectionTree>(window, "_tree");
            var ssh = new ConnectionEntry { Name = "box", Host = "box.example", Settings = { Protocol = Protocol.Ssh } };
            var shell = new ConnectionEntry { Name = "shell", Settings = { Protocol = Protocol.Local } };
            tree.Add(ssh, tree.Root);
            tree.Add(shell, tree.Root);
            var addTab = typeof(MainWindow).GetMethod("AddTab", Hidden)!;
            addTab.Invoke(window, [new Avalonia.Controls.Border(), new IdleSession("box.example"), new SessionOrigin("box", Protocol.Ssh, ssh.Id, () => Task.CompletedTask)]);
            addTab.Invoke(window, [new Avalonia.Controls.Border(), new IdleSession("this computer"),
                new SessionOrigin("shell", Protocol.Ssh, shell.Id, () => Task.CompletedTask) { Local = true }]);
            Dispatcher.UIThread.RunJobs();

            var tabs = VaultAutoLockTests.Private<List<SessionTab>>(window, "_tabs");
            Assert.Contains("Browse files (SFTP)", MenuTitles(window, tabs[0]));
            Assert.DoesNotContain("Browse files (SFTP)", MenuTitles(window, tabs[1]));
        }
        finally
        {
            window.Close();
        }
    }

    static string[] MenuTitles(MainWindow window, SessionTab tab) =>
        ((IEnumerable<Avalonia.Controls.Control>)typeof(MainWindow).GetMethod("TabActions", Hidden)!.Invoke(window, [tab])!)
            .OfType<Avalonia.Controls.MenuItem>().Select(m => m.Header?.ToString() ?? string.Empty).ToArray();

    sealed class IdleSession(string host) : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new(host, 22, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static string Describe(string name, SessionTab tab, Guid? entryId)
    {
        var origin = tab.Origin;
        var target = tab.Session.Target;
        var parts = new List<string>
        {
            name,
            $"title={origin.Title}",
            $"label={origin.Label}",
            $"protocol={origin.Protocol}",
            $"entry={(origin.EntryId == entryId ? "same" : "other")}",
            $"files={origin.Files}",
            $"local={origin.Local}",
            $"direct={origin.ReachableDirectly}",
            $"autoReconnect={origin.AutoReconnect}",
            $"restart={origin.Restart is not null}",
            $"session={tab.Session.GetType().Name}",
            $"view={tab.View.GetType().Name}",
            $"target={target.Host}:{(target.Port == 0 ? "0" : "port")} user={target.Username ?? "-"} domain={target.Domain ?? "-"}",
        };
        // What the session was started with and keeps to itself: its tunnel, clipboard, quality, SSH's route, a serial line.
        foreach (var field in SessionFields)
        {
            if (tab.Session.GetType().GetField(field, Hidden) is { } info)
            {
                parts.Add($"{field.TrimStart('_')}={Shown(info.GetValue(tab.Session))}");
            }
        }

        if (tab.View is RemoteFrameView desktop)
        {
            parts.Add($"follows={desktop.FollowsViewSize}");
            parts.Add($"zoom={typeof(RemoteFrameView).GetField("_zoom", Hidden)!.GetValue(desktop)}");
            parts.Add($"viewOnly={desktop.ViewOnly}");
            parts.Add($"scaling={desktop.Scaling}");
            parts.Add($"colorCorrection={desktop.AllowsColorCorrection}");
        }

        return string.Join(" | ", parts);
    }

    static readonly string[] SessionFields = ["_route", "_options", "_followsDisplayScale", "_shareClipboard", "_quality", "_allowLegacy", "_jumps", "_forwards", "_keepAlive", "_line"];

    static string Shown(object? value) => value switch
    {
        null => "-",
        ISessionRoute route => route.GetType().Name,
        System.Collections.ICollection list => $"{list.Count} item(s)",
        // A record such as RdpOptions lists its members.
        _ => value.ToString() ?? "-",
    };

    /// <summary>A port on this computer nothing listens on.</summary>
    static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
