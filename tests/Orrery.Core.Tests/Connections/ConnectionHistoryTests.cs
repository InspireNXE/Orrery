using Orrery.Core.Connections;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Connections;

public class ConnectionHistoryTests : IDisposable
{
    static readonly DateTimeOffset Noon = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    string FilePath => Path.Combine(_dir, ConnectionHistory.FileName);

    [Fact]
    public void A_connection_that_never_connected_has_no_record()
    {
        var use = new ConnectionHistory(FilePath).Get(Guid.NewGuid());

        Assert.Null(use.Connected);
        Assert.Null(use.Failed);
    }

    [Fact]
    public void Times_are_kept_across_restarts()
    {
        var id = Guid.NewGuid();
        var history = new ConnectionHistory(FilePath);
        history.RecordConnected(id, Noon);
        history.RecordFailed(id, Noon.AddHours(1));

        var reloaded = new ConnectionHistory(FilePath).Get(id);

        Assert.Equal(Noon, reloaded.Connected);
        Assert.Equal(Noon.AddHours(1), reloaded.Failed);
    }

    [Fact]
    public void A_failure_is_shown_only_while_it_is_newer_than_the_last_success()
    {
        var history = new ConnectionHistory(FilePath);
        var id = Guid.NewGuid();

        history.RecordFailed(id, Noon);
        Assert.Equal(Noon, history.Get(id).FailedSince);

        history.RecordConnected(id, Noon.AddMinutes(5));
        Assert.Null(history.Get(id).FailedSince);

        history.RecordFailed(id, Noon.AddMinutes(10));
        Assert.Equal(Noon.AddMinutes(10), history.Get(id).FailedSince);
    }

    [Fact]
    public void A_folder_shows_the_latest_of_its_connections_including_nested_ones_and_guests()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers" };
        var nested = new ConnectionFolder { Name = "Old" };
        var a = new ConnectionEntry { Name = "a", Host = "a" };
        var b = new ConnectionEntry { Name = "b", Host = "b" };
        var guest = new ConnectionEntry { Name = "guest", Host = "g" };
        var elsewhere = new ConnectionEntry { Name = "elsewhere", Host = "e" };
        tree.Add(folder, tree.Root);
        tree.Add(nested, folder);
        tree.Add(a, folder);
        tree.Add(b, nested);
        tree.AddGuest(guest, a);
        tree.Add(elsewhere, tree.Root);

        var history = new ConnectionHistory(FilePath);
        history.RecordConnected(a.Id, Noon);
        history.RecordConnected(b.Id, Noon.AddDays(-3));
        history.RecordConnected(guest.Id, Noon.AddHours(2));
        history.RecordFailed(b.Id, Noon.AddHours(1));
        history.RecordConnected(elsewhere.Id, Noon.AddDays(9));

        var summary = history.Summary(folder);

        Assert.Equal(Noon.AddHours(2), summary.Connected);
        Assert.Equal(Noon.AddHours(1), summary.Failed);
        Assert.Equal(Noon.AddDays(-3), history.Summary(nested).Connected);
    }

    [Fact]
    public void An_empty_folder_has_no_record()
    {
        var folder = new ConnectionFolder { Name = "Empty" };

        Assert.Equal(ConnectionUse.None, new ConnectionHistory(FilePath).Summary(folder));
    }

    [Fact]
    public void Renaming_and_moving_a_connection_keeps_its_record()
    {
        var tree = ConnectionTree.Empty();
        var one = new ConnectionFolder { Name = "One" };
        var two = new ConnectionFolder { Name = "Two" };
        var entry = new ConnectionEntry { Name = "box", Host = "box" };
        tree.Add(one, tree.Root);
        tree.Add(two, tree.Root);
        tree.Add(entry, one);
        var history = new ConnectionHistory(FilePath);
        history.RecordConnected(entry.Id, Noon);

        entry.Name = "renamed";
        tree.Move(entry, two);

        Assert.Equal(Noon, history.Summary(entry).Connected);
        Assert.Equal(Noon, history.Summary(two).Connected);
        Assert.Null(history.Summary(one).Connected);
    }

    [Fact]
    public void Forgetting_removes_only_the_given_records()
    {
        var kept = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var history = new ConnectionHistory(FilePath);
        history.RecordConnected(kept, Noon);
        history.RecordConnected(gone, Noon);

        history.Forget([gone]);

        var reloaded = new ConnectionHistory(FilePath);
        Assert.Equal(Noon, reloaded.Get(kept).Connected);
        Assert.Equal(ConnectionUse.None, reloaded.Get(gone));
    }

    [Fact]
    public void Deleting_a_connection_or_a_folder_reports_every_id_that_went()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers" };
        var host = new ConnectionEntry { Name = "host", Host = "h" };
        var guest = new ConnectionEntry { Name = "vm", Host = "v" };
        var lone = new ConnectionEntry { Name = "lone", Host = "l" };
        tree.Add(folder, tree.Root);
        tree.Add(host, folder);
        tree.AddGuest(guest, host);
        tree.Add(lone, tree.Root);
        var removed = new List<Guid>();
        tree.Removed += (_, e) => removed.AddRange(e.Ids);

        tree.Remove(lone);
        tree.Remove(folder);

        Assert.Equivalent(new[] { lone.Id, folder.Id, host.Id, guest.Id }, removed);
    }

    [Fact]
    public void Removing_a_host_but_keeping_its_guests_reports_only_the_host()
    {
        var tree = ConnectionTree.Empty();
        var host = new ConnectionEntry { Name = "host", Host = "h" };
        var guest = new ConnectionEntry { Name = "vm", Host = "v" };
        tree.Add(host, tree.Root);
        tree.AddGuest(guest, host);
        var removed = new List<Guid>();
        tree.Removed += (_, e) => removed.AddRange(e.Ids);

        tree.RemoveKeepingGuests(host, tree.Root);

        Assert.Equal([host.Id], removed);
    }

    [Fact]
    public void An_unreadable_file_is_an_empty_history_that_can_be_written_over()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var id = Guid.NewGuid();
        var history = new ConnectionHistory(FilePath);

        Assert.Equal(ConnectionUse.None, history.Get(id));
        history.RecordConnected(id, Noon);

        Assert.Equal(Noon, new ConnectionHistory(FilePath).Get(id).Connected);
    }

    [Fact]
    public void Changed_is_raised_when_a_time_is_recorded()
    {
        var history = new ConnectionHistory(FilePath);
        int raised = 0;
        history.Changed += (_, _) => raised++;

        history.RecordConnected(Guid.NewGuid(), Noon);
        history.RecordFailed(Guid.NewGuid(), Noon);

        Assert.Equal(2, raised);
    }

    [Fact]
    public void The_history_file_is_moved_with_the_rest_of_the_data()
    {
        Assert.True(DataFolderFiles.IsCopied(ConnectionHistory.FileName));
    }

    [Fact]
    public void The_history_is_not_part_of_the_saved_connections()
    {
        var entry = new ConnectionEntry { Name = "box", Host = "box.example" };
        var history = new ConnectionHistory(FilePath);
        history.RecordConnected(entry.Id, Noon);

        var connections = new ConnectionStore(Path.Combine(_dir, "connections.json"), Path.Combine(_dir, "backups"));
        var tree = ConnectionTree.Empty();
        tree.Add(entry, tree.Root);
        connections.Save(tree);

        Assert.DoesNotContain("2026-10-31", File.ReadAllText(Path.Combine(_dir, "connections.json")));
    }

    [Fact]
    public void Turning_history_off_deletes_it_and_records_nothing_until_it_is_on_again()
    {
        var id = Guid.NewGuid();
        var history = new ConnectionHistory(FilePath);
        history.RecordConnected(id, Noon);
        history.RecordFailed(id, Noon.AddHours(1));
        int changes = 0;
        history.Changed += (_, _) => changes++;

        history.Enabled = false;

        Assert.Equal(1, changes);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(ConnectionUse.None, history.Get(id));

        history.RecordConnected(id, Noon.AddHours(2));
        history.RecordFailed(id, Noon.AddHours(3));
        history.Forget([id]);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(ConnectionUse.None, history.Get(id));

        history.Enabled = true;
        Assert.Equal(ConnectionUse.None, history.Get(id));
        history.RecordConnected(id, Noon.AddHours(4));
        Assert.Equal(new ConnectionUse(Noon.AddHours(4), null), new ConnectionHistory(FilePath).Get(id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
