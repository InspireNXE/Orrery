using System.Runtime.CompilerServices;
using System.Text.Json;
using Orrery.Core.Connections;
using Orrery.Core.Export;
using Orrery.Core.Network;
using Xunit;

namespace Orrery.Core.Tests.Connections;

/// <summary>
/// What each protocol is and does, pinned, so describing protocols in one place can't change any of it: ports, labels,
/// what they reach, the strings files store them as, the schema a file needs, and what's written for each.
/// </summary>
/// <remarks>Set ORRERY_UPDATE_GOLDEN=1 to rewrite the files in Golden/ after a deliberate change.</remarks>
public class ProtocolGuardTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    public static TheoryData<Protocol, int, string, bool, bool, bool> Facts => new()
    {
        // protocol, default port, label, web, network, desktop
        { Protocol.Rdp, 3389, "RDP", false, true, true },
        { Protocol.Ssh, 22, "SSH", false, true, false },
        { Protocol.Vnc, 5900, "VNC", false, true, true },
        { Protocol.Telnet, 23, "TELNET", false, true, false },
        { Protocol.Serial, 0, "SERIAL", false, false, false },
        { Protocol.Http, 80, "HTTP", true, true, false },
        { Protocol.Https, 443, "HTTPS", true, true, false },
        { Protocol.Local, 0, "LOCAL", false, false, false },
    };

    [Theory]
    [MemberData(nameof(Facts))]
    public void Each_protocols_facts_are_as_they_were(Protocol protocol, int port, string label, bool web, bool network, bool desktop)
    {
        Assert.Equal(port, ProtocolInfo.DefaultPort(protocol));
        Assert.Equal(label, ProtocolInfo.Label(protocol));
        Assert.Equal(web, ProtocolInfo.IsWeb(protocol));
        Assert.Equal(network, ProtocolInfo.UsesNetwork(protocol));
        Assert.Equal(desktop, ProtocolInfo.IsDesktop(protocol));
    }

    [Fact]
    public void Every_protocol_is_pinned()
    {
        var pinned = Facts.Select(row => (Protocol)row[0]).ToHashSet();
        Assert.Equal(Enum.GetValues<Protocol>().ToHashSet(), pinned);
    }

    [Theory]
    [InlineData(Protocol.Rdp, "rdp")]
    [InlineData(Protocol.Ssh, "ssh")]
    [InlineData(Protocol.Vnc, "vnc")]
    [InlineData(Protocol.Telnet, "telnet")]
    [InlineData(Protocol.Serial, "serial")]
    [InlineData(Protocol.Http, "http")]
    [InlineData(Protocol.Https, "https")]
    [InlineData(Protocol.Local, "local")]
    public void Files_store_a_protocol_as_its_camel_case_name(Protocol protocol, string stored)
    {
        var json = JsonSerializer.Serialize(protocol, ConnectionStore.JsonOptions);
        Assert.Equal($"\"{stored}\"", json);
        Assert.Equal(protocol, JsonSerializer.Deserialize<Protocol>(json, ConnectionStore.JsonOptions));
    }

    [Theory]
    [InlineData(Protocol.Rdp, 1)]
    [InlineData(Protocol.Ssh, 1)]
    [InlineData(Protocol.Vnc, 1)]
    [InlineData(Protocol.Telnet, 1)]
    [InlineData(Protocol.Serial, 3)]
    [InlineData(Protocol.Http, 3)]
    [InlineData(Protocol.Https, 3)]
    [InlineData(Protocol.Local, 6)]
    public void A_file_with_one_connection_needs_the_schema_its_protocol_came_in(Protocol protocol, int schema)
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "a", Host = "a", Settings = { Protocol = protocol } }, tree.Root);
        Assert.Equal(schema, ConnectionStore.SchemaFor(tree.Root));

        // A protocol set on a folder counts the same.
        var folderTree = ConnectionTree.Empty();
        folderTree.Add(new ConnectionFolder { Name = "f", Settings = { Protocol = protocol } }, folderTree.Root);
        Assert.Equal(schema, ConnectionStore.SchemaFor(folderTree.Root));
    }

    [Fact]
    public void A_file_with_every_protocol_needs_the_newest_schema()
    {
        Assert.Equal(6, ConnectionStore.SchemaFor(EveryProtocol().Root));
    }

    [Theory]
    [InlineData(Protocol.Rdp, null)]
    [InlineData(Protocol.Ssh, null)]
    [InlineData(Protocol.Vnc, null)]
    [InlineData(Protocol.Telnet, null)]
    [InlineData(Protocol.Http, null)]
    [InlineData(Protocol.Https, null)]
    [InlineData(Protocol.Serial, "a serial port on this computer, not a host on the network")]
    [InlineData(Protocol.Local, "a shell on this computer, not a host on the network")]
    public void Host_status_checks_network_protocols_only(Protocol protocol, string? whyNot)
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "a", Host = "box.example", Settings = { Protocol = protocol } };
        tree.Add(entry, tree.Root);

        var target = ProbeTarget.For(entry, out var reason);
        Assert.Equal(whyNot, reason);
        Assert.Equal(whyNot is null, target is not null);
        if (target is not null)
        {
            Assert.Equal(ProtocolInfo.DefaultPort(protocol), target.Port);
        }
    }

    [Theory]
    [InlineData(Protocol.Rdp, false)]
    [InlineData(Protocol.Ssh, false)]
    [InlineData(Protocol.Vnc, false)]
    [InlineData(Protocol.Telnet, true)]
    [InlineData(Protocol.Serial, true)]
    [InlineData(Protocol.Http, false)]
    [InlineData(Protocol.Https, false)]
    [InlineData(Protocol.Local, false)]
    public void Saved_commands_warn_about_the_unencrypted_protocols(Protocol protocol, bool warns)
    {
        var tree = ConnectionTree.Empty();
        var entry = new ConnectionEntry { Name = "a", Host = "a", Settings = { Protocol = protocol } };
        tree.Add(entry, tree.Root);
        Assert.Equal(warns, SavedCommands.ReachesUnencrypted(entry));
    }

    [Theory]
    [InlineData(Protocol.Rdp, false)]
    [InlineData(Protocol.Ssh, true)]
    [InlineData(Protocol.Vnc, false)]
    [InlineData(Protocol.Telnet, false)]
    [InlineData(Protocol.Serial, false)]
    [InlineData(Protocol.Http, false)]
    [InlineData(Protocol.Https, false)]
    [InlineData(Protocol.Local, false)]
    public void Only_ssh_can_be_a_jump_host(Protocol jumpProtocol, bool allowed)
    {
        var tree = ConnectionTree.Empty();
        var jump = new ConnectionEntry { Name = "jump", Host = "jump.example", Settings = { Protocol = jumpProtocol } };
        var entry = new ConnectionEntry { Name = "a", Host = "a.example", Settings = { JumpHostId = jump.Id } };
        tree.Add(jump, tree.Root);
        tree.Add(entry, tree.Root);

        var chain = SettingsResolver.JumpChain(entry, tree, out var error);
        Assert.Equal(allowed, chain is not null);
        Assert.Equal(allowed, error is null);
    }

    [Fact]
    public void The_connections_file_is_written_as_before()
    {
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"), Path.Combine(_dir, "backups"));
        store.Save(EveryProtocol());
        Golden("connections.json", File.ReadAllText(Path.Combine(_dir, "connections.json")));
    }

    [Theory]
    [InlineData(ExportFormat.Orrery, "export.orrery.json")]
    [InlineData(ExportFormat.MRemoteNg, "export.mremoteng.xml")]
    [InlineData(ExportFormat.Csv, "export.csv")]
    public void Exports_are_written_as_before(ExportFormat format, string golden)
    {
        var tree = EveryProtocol();
        var result = ConnectionExporter.Export(tree.Root, format, credentials: null, new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
        // mRemoteNG's check value is encrypted with a new salt each time.
        var content = System.Text.RegularExpressions.Regex.Replace(result.Content, "Protected=\"[^\"]*\"", "Protected=\"...\"");
        Golden(golden, content + "\n--- warnings ---\n" + string.Join("\n", result.Warnings));
    }

    /// <summary>One connection of each protocol, with fixed ids, and a few protocol settings set so they're written.</summary>
    internal static ConnectionTree EveryProtocol()
    {
        var tree = new ConnectionTree(new ConnectionFolder { Name = "Connections", Id = Id(999) });
        var folder = new ConnectionFolder { Name = "Every protocol", Id = Id(100) };
        tree.Add(folder, tree.Root);
        int n = 0;
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            n++;
            var entry = new ConnectionEntry
            {
                Name = protocol.ToString(),
                Id = Id(n),
                Host = protocol switch
                {
                    Protocol.Serial => "COM3",
                    Protocol.Local => "pwsh",
                    Protocol.Http or Protocol.Https => "router.lan/admin",
                    _ => $"{protocol.ToString().ToLowerInvariant()}.example",
                },
                Settings = { Protocol = protocol, Username = protocol is Protocol.Serial or Protocol.Local ? null : "alice" },
            };
            switch (protocol)
            {
                case Protocol.Rdp:
                    entry.Settings.Domain = "CORP";
                    entry.Settings.DesktopSize = "1920x1080";
                    break;
                case Protocol.Vnc:
                    entry.Settings.ViewOnly = true;
                    break;
                case Protocol.Ssh:
                    entry.Settings.KeepAliveSeconds = 15;
                    entry.Settings.KeyFile = "~/.ssh/id_ed25519";
                    break;
                case Protocol.Serial:
                    entry.Settings.BaudRate = 115200;
                    break;
                case Protocol.Https:
                    entry.Settings.Port = 8443;
                    break;
            }

            tree.Add(entry, folder);
        }

        return tree;
    }

    static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    static void Golden(string name, string actual, [CallerFilePath] string here = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(here)!, "Golden", name);
        actual = actual.ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("ORRERY_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing; run with ORRERY_UPDATE_GOLDEN=1 to write it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }
}
