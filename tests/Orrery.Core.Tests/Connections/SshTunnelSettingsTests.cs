using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

public class SshTunnelSettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("L 8080:db:5432", ForwardKind.Local, null, 8080, "db", 5432)]
    [InlineData("l 0.0.0.0:8080:db:5432", ForwardKind.Local, "0.0.0.0", 8080, "db", 5432)]
    [InlineData("R 9000:localhost:3000", ForwardKind.Remote, null, 9000, "localhost", 3000)]
    [InlineData("  D 1080  ", ForwardKind.Dynamic, null, 1080, null, 0)]
    [InlineData("D 127.0.0.2:1080", ForwardKind.Dynamic, "127.0.0.2", 1080, null, 0)]
    public void Forwards_parse_in_openssh_form_and_write_back_the_same(string line, ForwardKind kind, string? listen, int listenPort, string? host, int port)
    {
        var forward = PortForward.TryParse(line, out var error);

        Assert.Null(error);
        Assert.Equal(new PortForward(kind, listenPort, host, port, listen), forward);
        Assert.Equal(forward, PortForward.TryParse(forward!.ToString(), out _));
    }

    [Theory]
    [InlineData("8080:db:5432")]
    [InlineData("X 8080:db:5432")]
    [InlineData("L 8080:db")]
    [InlineData("L 0:db:5432")]
    [InlineData("L 8080:db:70000")]
    [InlineData("L 8080::5432")]
    [InlineData("D 1080:extra:bits")]
    [InlineData("D -1")]
    public void Bad_forwards_are_rejected_with_a_message(string line)
    {
        Assert.Null(PortForward.TryParse(line, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_list_of_forwards_skips_blank_lines_and_fails_on_the_first_bad_one()
    {
        Assert.Equal(2, PortForward.TryParseAll("L 8080:db:5432\r\n\n  \nD 1080\n", out _)!.Count);
        Assert.Null(PortForward.TryParseAll("L 8080:db:5432\nnonsense", out var error));
        Assert.Contains("nonsense", error);
    }

    [Fact]
    public void Forwards_listen_on_loopback_unless_told_otherwise()
    {
        Assert.Equal("127.0.0.1", new PortForward(ForwardKind.Local, 8080, "db", 5432).EffectiveListenAddress);
        Assert.Equal("0.0.0.0", new PortForward(ForwardKind.Local, 8080, "db", 5432, "0.0.0.0").EffectiveListenAddress);
    }

    static (ConnectionTree Tree, ConnectionEntry Bastion, ConnectionFolder Behind, ConnectionEntry Db) Sample()
    {
        var tree = ConnectionTree.Empty();
        var bastion = new ConnectionEntry { Name = "bastion", Host = "bastion.example.com", Settings = { Protocol = Protocol.Ssh } };
        var behind = new ConnectionFolder { Name = "Behind the bastion" };
        var db = new ConnectionEntry { Name = "db", Host = "db.internal", Settings = { Protocol = Protocol.Ssh } };
        tree.Add(bastion, tree.Root);
        tree.Add(behind, tree.Root);
        tree.Add(db, behind);
        behind.Settings.JumpHostId = bastion.Id;
        return (tree, bastion, behind, db);
    }

    [Fact]
    public void A_jump_host_set_on_a_folder_is_inherited_and_can_be_turned_off()
    {
        var (tree, bastion, behind, db) = Sample();

        Assert.Equal(bastion.Id, SettingsResolver.Resolve(db).JumpHostId.Value);
        Assert.Same(behind, SettingsResolver.Resolve(db).JumpHostId.Source);
        Assert.Equal([bastion], SettingsResolver.JumpChain(db, tree, out _));

        db.Settings.JumpHostId = InheritableSettings.NoJumpHost;
        Assert.Null(SettingsResolver.Resolve(db).JumpHostId.Value);
        Assert.Empty(SettingsResolver.JumpChain(db, tree, out _)!);
    }

    [Fact]
    public void A_jump_hosts_own_jump_host_comes_first()
    {
        var (tree, bastion, _, db) = Sample();
        var edge = new ConnectionEntry { Name = "edge", Host = "edge.example.com", Settings = { Protocol = Protocol.Ssh } };
        tree.Add(edge, tree.Root);
        bastion.Settings.JumpHostId = edge.Id;

        Assert.Equal([edge, bastion], SettingsResolver.JumpChain(db, tree, out _));
    }

    [Fact]
    public void A_jump_chain_that_loops_is_refused()
    {
        var (tree, bastion, _, db) = Sample();
        bastion.Settings.JumpHostId = db.Id;

        Assert.Null(SettingsResolver.JumpChain(db, tree, out var error));
        Assert.Contains("through itself", error);
    }

    [Fact]
    public void A_missing_or_non_ssh_jump_host_is_refused()
    {
        var (tree, bastion, behind, db) = Sample();
        bastion.Settings.Protocol = Protocol.Rdp;
        Assert.Null(SettingsResolver.JumpChain(db, tree, out var error));
        Assert.Contains("must be an SSH connection", error);

        behind.Settings.JumpHostId = Guid.NewGuid();
        Assert.Null(SettingsResolver.JumpChain(db, tree, out error));
        Assert.Contains("no longer exists", error);
    }

    [Fact]
    public void Jump_chains_stop_at_the_limit()
    {
        var tree = ConnectionTree.Empty();
        ConnectionEntry? previous = null;
        var hops = new List<ConnectionEntry>();
        for (int i = 0; i <= SettingsResolver.MaxJumpHosts + 1; i++)
        {
            var hop = new ConnectionEntry { Name = $"hop-{i}", Host = $"hop-{i}", Settings = { Protocol = Protocol.Ssh, JumpHostId = previous?.Id } };
            tree.Add(hop, tree.Root);
            hops.Add(hop);
            previous = hop;
        }

        Assert.Equal(SettingsResolver.MaxJumpHosts, SettingsResolver.JumpChain(hops[SettingsResolver.MaxJumpHosts], tree, out _)!.Count);
        Assert.Null(SettingsResolver.JumpChain(hops[^1], tree, out var error));
        Assert.Contains("more than", error);
    }

    [Fact]
    public void Jump_hosts_and_forwards_are_saved_and_duplicated()
    {
        var (tree, bastion, _, db) = Sample();
        db.PortForwards = [new PortForward(ForwardKind.Local, 5432, "localhost", 5432), new PortForward(ForwardKind.Dynamic, 1080)];
        var store = new ConnectionStore(Path.Combine(_dir, "connections.json"), Path.Combine(_dir, "backups"));
        store.Save(tree);

        var loaded = store.Load();
        var loadedDb = (ConnectionEntry)loaded.Find(db.Id)!;
        Assert.Equal(db.PortForwards, loadedDb.PortForwards);
        Assert.Equal(bastion.Id, SettingsResolver.Resolve(loadedDb).JumpHostId.Value);
        Assert.Null(((ConnectionEntry)loaded.Find(bastion.Id)!).PortForwards);

        var copy = (ConnectionEntry)tree.Duplicate(db);
        Assert.Equal(db.PortForwards, copy.PortForwards);
        Assert.NotSame(db.PortForwards, copy.PortForwards);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
