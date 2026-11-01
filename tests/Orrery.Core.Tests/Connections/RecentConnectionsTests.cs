using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

public class RecentConnectionsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    string FilePath => Path.Combine(_dir, RecentConnections.FileName);

    static RecentConnection Direct(string host, Protocol protocol = Protocol.Ssh, string? user = "admin") =>
        new(null, host, protocol, host, ProtocolInfo.DefaultPort(protocol), user, DateTimeOffset.UtcNow);

    [Fact]
    public void Opening_a_connection_again_moves_it_to_the_top()
    {
        var recent = new RecentConnections(FilePath);
        recent.Add(Direct("a"));
        recent.Add(Direct("b"));
        recent.Add(Direct("A"));

        Assert.Equal(["A", "b"], recent.Items.Select(r => r.Host));
    }

    [Fact]
    public void A_saved_connection_is_matched_by_id_even_after_a_rename()
    {
        var id = Guid.NewGuid();
        var recent = new RecentConnections(FilePath);
        recent.Add(new RecentConnection(id, "old name", Protocol.Rdp, "box", 3389, null, DateTimeOffset.UtcNow));
        recent.Add(new RecentConnection(id, "new name", Protocol.Rdp, "box", 3389, null, DateTimeOffset.UtcNow));

        Assert.Equal("new name", Assert.Single(recent.Items).Title);
    }

    [Fact]
    public void The_same_host_over_another_protocol_is_a_separate_entry()
    {
        var recent = new RecentConnections(FilePath);
        recent.Add(Direct("box", Protocol.Ssh));
        recent.Add(Direct("box", Protocol.Telnet));

        Assert.Equal(2, recent.Items.Count);
    }

    [Fact]
    public void Only_the_newest_are_kept_and_they_survive_a_restart()
    {
        var recent = new RecentConnections(FilePath, limit: 3);
        foreach (var host in new[] { "a", "b", "c", "d" })
        {
            recent.Add(Direct(host));
        }

        Assert.Equal(["d", "c", "b"], new RecentConnections(FilePath, limit: 3).Items.Select(r => r.Host));
    }

    [Fact]
    public void An_unreadable_file_is_an_empty_history()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "not json");

        Assert.Empty(new RecentConnections(FilePath).Items);
    }

    [Fact]
    public void Turning_remembering_off_deletes_the_file_and_leftovers_and_records_nothing_until_it_is_on_again()
    {
        var recent = new RecentConnections(FilePath);
        recent.Add(Direct("before"));
        var leftover = FilePath + ".0123456789ABCDEF.tmp";
        File.WriteAllText(leftover, "half a write");
        File.WriteAllText(Path.Combine(_dir, "unrelated.json"), "{}");
        int changes = 0;
        recent.Changed += (_, _) => changes++;

        recent.Enabled = false;

        Assert.Equal(1, changes);
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(Path.Combine(_dir, "unrelated.json")));
        Assert.Empty(recent.Items);

        recent.Add(Direct("while off"));
        recent.Remove(Direct("while off"));
        recent.Clear();
        Assert.False(File.Exists(FilePath));
        Assert.Empty(recent.Items);

        recent.Enabled = true;
        Assert.Empty(recent.Items);
        recent.Add(Direct("after"));
        Assert.Equal(["after"], new RecentConnections(FilePath).Items.Select(r => r.Host));
    }

    [Fact]
    public void Turning_remembering_off_with_no_file_is_fine()
    {
        var recent = new RecentConnections(FilePath) { Enabled = false };

        Assert.False(File.Exists(FilePath));
        Assert.Empty(recent.Items);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
