using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

public class DataMoverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _source;
    readonly List<string> _links = [];

    public DataMoverTests()
    {
        _source = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(_source, "backups"));
        File.WriteAllText(Path.Combine(_source, "connections.json"), "{\"tree\":1}");
        File.WriteAllText(Path.Combine(_source, "vault.frv"), "secret-bytes");
        File.WriteAllText(Path.Combine(_source, "backups", "connections-1.json"), "{\"tree\":0}");
        File.WriteAllText(Path.Combine(_source, ".lock"), string.Empty);
    }

    [Fact]
    public void Copies_every_data_file_but_not_the_lock()
    {
        var target = Path.Combine(_root, "moved");

        int count = DataMover.Copy(_source, target);

        Assert.Equal(3, count);
        Assert.Equal("{\"tree\":1}", File.ReadAllText(Path.Combine(target, "connections.json")));
        Assert.Equal("secret-bytes", File.ReadAllText(Path.Combine(target, "vault.frv")));
        Assert.True(File.Exists(Path.Combine(target, "backups", "connections-1.json")));
        Assert.False(File.Exists(Path.Combine(target, ".lock")));
        Assert.True(File.Exists(Path.Combine(_source, "connections.json")), "the original stays until deleted on request");
    }

    [Fact]
    public void Refuses_a_folder_that_already_has_files()
    {
        var target = Path.Combine(_root, "busy");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "mine");

        Assert.NotNull(DataMover.CheckTarget(_source, target));
        Assert.Throws<IOException>(() => DataMover.Copy(_source, target));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("data/inner")]
    [InlineData("")]
    public void Refuses_the_same_folder_or_one_nested_with_it(string relative)
    {
        Assert.NotNull(DataMover.CheckTarget(_source, Path.Combine(_root, relative)));
    }

    [Fact]
    public void An_empty_existing_folder_is_fine()
    {
        var target = Path.Combine(_root, "empty");
        Directory.CreateDirectory(target);

        Assert.Null(DataMover.CheckTarget(_source, target));
    }

    [Fact]
    public void Deleting_the_old_copy_keeps_what_isnt_orrerys_data()
    {
        File.WriteAllText(Path.Combine(_source, InstalledLocationPointer.FileName), "{}");

        DataMover.DeleteOldCopy(_source);

        Assert.False(File.Exists(Path.Combine(_source, "connections.json")));
        Assert.False(Directory.Exists(Path.Combine(_source, "backups")));
        Assert.True(File.Exists(Path.Combine(_source, InstalledLocationPointer.FileName)));
    }

    [Fact]
    public void Deleting_a_folder_with_only_data_removes_the_folder()
    {
        File.Delete(Path.Combine(_source, ".lock"));

        DataMover.DeleteOldCopy(_source);

        Assert.False(Directory.Exists(_source));
    }

    [Fact]
    public void Installed_mode_follows_the_pointer_after_a_move()
    {
        var folders = new PlatformFolders(Path.Combine(_root, "config"), Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        var moved = Path.Combine(_root, "elsewhere");
        var appDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(appDir);

        InstalledLocationPointer.Set(folders.Config, moved);
        var location = Assert.IsType<StartupStorage.Ready>(DataLocationResolver.Resolve(appDir, folders)).Location;
        Assert.Equal(moved, location.DataDir);
        Assert.Equal(folders.Cache, location.CacheDir);

        // Moving back to the default folder removes the pointer.
        InstalledLocationPointer.Set(folders.Config, folders.Config);
        Assert.False(File.Exists(Path.Combine(folders.Config, InstalledLocationPointer.FileName)));
        location = Assert.IsType<StartupStorage.Ready>(DataLocationResolver.Resolve(appDir, folders)).Location;
        Assert.Equal(folders.Config, location.DataDir);
    }

    [Fact]
    public void User_files_are_neither_copied_nor_deleted()
    {
        File.WriteAllText(Path.Combine(_source, "Thesis.docx"), "chapter 1");
        Directory.CreateDirectory(Path.Combine(_source, "Photos"));
        File.WriteAllText(Path.Combine(_source, "Photos", "a.jpg"), "jpeg");
        var target = Path.Combine(_root, "moved");

        Assert.Equal(3, DataMover.Copy(_source, target));
        Assert.False(File.Exists(Path.Combine(target, "Thesis.docx")));
        Assert.False(Directory.Exists(Path.Combine(target, "Photos")));

        var left = DataMover.DeleteOldCopy(_source);

        Assert.False(File.Exists(Path.Combine(_source, "connections.json")));
        Assert.Equal("chapter 1", File.ReadAllText(Path.Combine(_source, "Thesis.docx")));
        Assert.Equal("jpeg", File.ReadAllText(Path.Combine(_source, "Photos", "a.jpg")));
        Assert.Equal(["Photos", "Thesis.docx"], left.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_nested_portable_folder_is_neither_copied_nor_deleted()
    {
        var portable = Path.Combine(_source, "Portable");
        Directory.CreateDirectory(Path.Combine(portable, "backups"));
        File.WriteAllText(Path.Combine(portable, "connections.json"), "{\"tree\":\"portable\"}");
        File.WriteAllText(Path.Combine(portable, "backups", "connections-1.json"), "{}");
        var target = Path.Combine(_root, "moved");

        DataMover.Copy(_source, target);
        Assert.False(Directory.Exists(Path.Combine(target, "Portable")));

        var left = DataMover.DeleteOldCopy(_source);

        Assert.Equal("{\"tree\":\"portable\"}", File.ReadAllText(Path.Combine(portable, "connections.json")));
        Assert.True(File.Exists(Path.Combine(portable, "backups", "connections-1.json")));
        Assert.Contains("Portable", left);
    }

    [Fact]
    public void A_linked_folder_is_not_followed()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "mine");
        File.WriteAllText(Path.Combine(outside, "connections.json"), "{\"tree\":\"other\"}");
        var link = Path.Combine(_source, "Linked");
        Link(link, outside);
        var target = Path.Combine(_root, "moved");

        DataMover.Copy(_source, target);
        Assert.False(Directory.Exists(Path.Combine(target, "Linked")));

        var left = DataMover.DeleteOldCopy(_source);

        Assert.Equal("mine", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        Assert.Equal("{\"tree\":\"other\"}", File.ReadAllText(Path.Combine(outside, "connections.json")));
        Assert.True(Directory.Exists(link));
        Assert.Contains("Linked", left);
    }

    [Fact]
    public void A_linked_backups_folder_stops_the_copy_and_survives_delete()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "connections-9.json"), "{\"tree\":9}");
        Directory.Delete(Path.Combine(_source, "backups"), recursive: true);
        Link(Path.Combine(_source, "backups"), outside);
        var target = Path.Combine(_root, "moved");

        Assert.Throws<IOException>(() => DataMover.Copy(_source, target));
        Assert.False(Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any(), "a failed copy leaves nothing behind");

        DataMover.DeleteOldCopy(_source);

        Assert.Equal("{\"tree\":9}", File.ReadAllText(Path.Combine(outside, "connections-9.json")));
    }

    [Fact]
    public void Refuses_a_root_source()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_root))!;

        Assert.NotNull(DataMover.CheckTarget(root, Path.Combine(_root, "moved")));
        Assert.NotNull(DataMover.CheckTarget(_source, root));
    }

    [Fact]
    public void Refuses_a_target_inside_the_source_through_a_link()
    {
        var alias = Path.Combine(_root, "alias");
        Link(alias, _source);
        var target = Path.Combine(alias, "new");

        // The nesting itself, caught before the write probe would create a folder inside the source.
        Assert.Equal(DataMover.NestedMessage, DataMover.CheckTarget(_source, target));
        Assert.Throws<IOException>(() => DataMover.Copy(_source, target));
        Assert.False(Directory.Exists(Path.Combine(_source, "new")));
        Assert.Equal("{\"tree\":1}", File.ReadAllText(Path.Combine(_source, "connections.json")));
    }

    [Fact]
    public void Refuses_a_source_inside_the_target_through_a_link()
    {
        var alias = Path.Combine(_root, "alias");
        Link(alias, _root);

        // Not the "choose an empty folder" message: the nesting itself has to be caught.
        Assert.Equal(DataMover.NestedMessage, DataMover.CheckTarget(Path.Combine(alias, "data"), _root));
        Assert.Equal(DataMover.NestedMessage, DataMover.CheckTarget(_source, Path.Combine(alias, "data", "inner")));
    }

    [Fact]
    public void Copy_records_the_old_folder_for_cleanup()
    {
        var target = Path.Combine(_root, "moved");

        DataMover.Copy(_source, target);

        Assert.True(DataMover.SamePath(_source, PendingCleanup.TryLoad(target)!.OldDataDir));
        Assert.Null(PendingCleanup.TryLoad(_source));
    }

    [Fact]
    public void A_pending_cleanup_record_is_deleted_with_the_old_copy_and_never_copied()
    {
        PendingCleanup.Record(_source, Path.Combine(_root, "older"));
        var target = Path.Combine(_root, "moved");

        DataMover.Copy(_source, target);
        Assert.True(DataMover.SamePath(_source, PendingCleanup.TryLoad(target)!.OldDataDir));

        File.Delete(Path.Combine(_source, ".lock"));
        Assert.Empty(DataMover.DeleteOldCopy(_source));
        Assert.False(Directory.Exists(_source));
    }

    [Fact]
    public void Checking_the_old_copy_refuses_the_current_folder_however_its_spelled()
    {
        var current = Path.Combine(_root, "current");
        Directory.CreateDirectory(Path.Combine(current, "inner"));
        var alias = Path.Combine(_root, "alias");
        Link(alias, current);

        Assert.NotNull(DataMover.CheckOldCopy(alias, current));
        Assert.NotNull(DataMover.CheckOldCopy(Path.Combine(alias, "inner"), current));
        Assert.NotNull(DataMover.CheckOldCopy(_root, current));
        Assert.NotNull(DataMover.CheckOldCopy(Path.GetPathRoot(Path.GetFullPath(_root))!, current));
        Assert.NotNull(DataMover.CheckOldCopy(Path.Combine(_root, "missing"), current));
        Assert.Null(DataMover.CheckOldCopy(_source, current));
    }

    [Fact]
    public void Leftover_files_inside_owned_folders_are_kept_and_reported()
    {
        File.WriteAllText(Path.Combine(_source, "backups", "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(_source, "connections.json.unreadable-20261030-101500"), "broken");
        var target = Path.Combine(_root, "moved");

        Assert.Equal(4, DataMover.Copy(_source, target));
        Assert.True(File.Exists(Path.Combine(target, "connections.json.unreadable-20261030-101500")));
        Assert.False(File.Exists(Path.Combine(target, "backups", "notes.txt")));

        var left = DataMover.DeleteOldCopy(_source);

        Assert.False(File.Exists(Path.Combine(_source, "connections.json.unreadable-20261030-101500")));
        Assert.False(File.Exists(Path.Combine(_source, "backups", "connections-1.json")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_source, "backups", "notes.txt")));
        Assert.Equal([Path.Combine("backups", "notes.txt")], left);
    }

    [Fact]
    public void An_interrupted_atomic_write_is_deleted_with_the_old_copy_and_never_copied()
    {
        // The temp names AtomicFile uses (name + random + ".tmp"), and the plain name.tmp older versions used.
        string[] temps = ["vault.frv.0123456789ABCDEF.tmp", "connections.json.FEDCBA9876543210.tmp", "vault.frv.tmp"];
        foreach (var temp in temps)
        {
            File.WriteAllText(Path.Combine(_source, temp), "half");
        }

        File.WriteAllText(Path.Combine(_source, "backups", "connections-2.json.0123456789ABCDEF.tmp"), "half");
        File.WriteAllText(Path.Combine(_source, "notes.txt.0123456789ABCDEF.tmp"), "mine");
        File.WriteAllText(Path.Combine(_source, "vault.frvX.0123456789ABCDEF.tmp"), "mine");
        var target = Path.Combine(_root, "moved");

        Assert.Equal(3, DataMover.Copy(_source, target));
        Assert.Equal(["backups", "connections.json", "pending-cleanup.json", "vault.frv"], Directory.GetFileSystemEntries(target).Select(Path.GetFileName).Order());

        var left = DataMover.DeleteOldCopy(_source);

        foreach (var temp in temps)
        {
            Assert.False(File.Exists(Path.Combine(_source, temp)), temp);
        }

        Assert.False(File.Exists(Path.Combine(_source, "backups", "connections-2.json.0123456789ABCDEF.tmp")));
        Assert.True(File.Exists(Path.Combine(_source, "notes.txt.0123456789ABCDEF.tmp")));
        Assert.True(File.Exists(Path.Combine(_source, "vault.frvX.0123456789ABCDEF.tmp")));
        Assert.Equal(2, left.Count);
    }

    /// <summary>A junction on Windows (no admin rights needed), a symlink elsewhere.</summary>
    void Link(string link, string target)
    {
        _links.Add(link);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(link), $"couldn't create a junction at {link}");
    }

    public void Dispose()
    {
        // Remove the links on their own first: a recursive delete reports a junction as access denied.
        foreach (var link in Enumerable.Reverse(_links))
        {
            try
            {
                Directory.Delete(link);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
