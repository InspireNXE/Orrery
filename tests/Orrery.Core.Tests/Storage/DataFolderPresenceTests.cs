using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

public class DataFolderPresenceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("orrery-presence-").FullName;
    DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    DataFolderPresence On(string computer) => new(_dir, computer, () => _now);

    [Fact]
    public void A_computer_s_tag_is_steady_short_and_doesn_t_hold_its_name()
    {
        var tag = DataFolderPresence.ComputerTag("LAPTOP-7");
        Assert.Matches("^[0-9a-f]{6}$", tag);
        Assert.Equal(tag, DataFolderPresence.ComputerTag("laptop-7"));
        Assert.NotEqual(tag, DataFolderPresence.ComputerTag("DESKTOP-2"));
    }

    [Fact]
    public void Each_computer_sees_the_other_but_not_itself()
    {
        using var laptop = On("LAPTOP-7");
        using var desktop = On("DESKTOP-2");

        Assert.Empty(laptop.Start());
        var seen = Assert.Single(desktop.Start());
        Assert.Equal("LAPTOP-7", seen.Name);
        Assert.Equal(laptop.Tag, seen.Tag);
        Assert.Equal("DESKTOP-2", Assert.Single(laptop.Others()).Name);
    }

    [Fact]
    public void Closing_removes_the_computer_s_file()
    {
        var laptop = On("LAPTOP-7");
        laptop.Start();
        Assert.Single(Directory.GetFiles(_dir, DataFolderPresence.FilePrefix + "*"));

        laptop.Dispose();
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_file_not_rewritten_for_a_while_is_from_an_Orrery_that_stopped()
    {
        using var laptop = On("LAPTOP-7");
        laptop.Mark();
        using var desktop = On("DESKTOP-2");

        _now += DataFolderPresence.StaleAfter + TimeSpan.FromSeconds(1);
        Assert.Empty(desktop.Others());

        laptop.Mark();
        Assert.Single(desktop.Others());
    }

    [Fact]
    public void Another_computer_opening_later_is_told_once()
    {
        using var desktop = On("DESKTOP-2");
        var told = new List<string>();
        desktop.OtherOpened += o => told.Add(o.Name);
        desktop.Start();

        desktop.TickNow();
        Assert.Empty(told);

        using var laptop = On("LAPTOP-7");
        laptop.Mark();
        desktop.TickNow();
        desktop.TickNow();
        Assert.Equal(["LAPTOP-7"], told);
    }

    [Fact]
    public void A_damaged_or_odd_file_is_passed_over()
    {
        File.WriteAllText(Path.Combine(_dir, DataFolderPresence.FilePrefix + "abcdef.json"), "{ not json");
        File.WriteAllText(Path.Combine(_dir, DataFolderPresence.FilePrefix + "toolongtag.json"), "{}");
        using var desktop = On("DESKTOP-2");
        Assert.Empty(desktop.Start());
    }

    [Fact]
    public void Sync_apps_conflict_copies_of_Orrery_s_files_are_found()
    {
        foreach (var name in new[]
        {
            "connections.json",
            "vault.frv",
            "connections (LAPTOP-7's conflicted copy 2026-11-05).json", // Dropbox
            "vault_LAPTOP-7_Conflict_20261105.frv", // Synology Drive
            "settings.sync-conflict-20261105-120000-ABCDEFG.json", // Syncthing
            "known-hosts-LAPTOP-7.json", // OneDrive
            "connections.json.unreadable-20261105", // Orrery's own
            "notes (conflict).txt", // Not Orrery's
            DataFolderPresence.FilePrefix + "abcdef.json",
        })
        {
            File.WriteAllText(Path.Combine(_dir, name), "x");
        }

        var found = SyncConflicts.Find(_dir, ["LAPTOP-7"]);

        Assert.Contains("connections (LAPTOP-7's conflicted copy 2026-11-05).json", found);
        Assert.Contains("vault_LAPTOP-7_Conflict_20261105.frv", found);
        Assert.Contains("settings.sync-conflict-20261105-120000-ABCDEFG.json", found);
        Assert.Contains("known-hosts-LAPTOP-7.json", found);
        Assert.DoesNotContain("connections.json", found);
        Assert.DoesNotContain("vault.frv", found);
        Assert.DoesNotContain("connections.json.unreadable-20261105", found);
        Assert.DoesNotContain("notes (conflict).txt", found);
        Assert.DoesNotContain(DataFolderPresence.FilePrefix + "abcdef.json", found);
        Assert.Empty(SyncConflicts.Find(Path.Combine(_dir, "missing")));
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
