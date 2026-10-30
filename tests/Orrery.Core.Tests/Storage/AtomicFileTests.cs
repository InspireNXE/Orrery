using System.Runtime.Versioning;
using Orrery.Core.Connections;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

/// <summary>Runs on Linux and macOS only: shows as skipped on Windows, where there are no Unix modes to check.</summary>
sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix file modes: Linux and macOS only";
        }
    }
}

public class AtomicFileTests : IDisposable
{
    const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    string P(params string[] parts) => Path.Combine([_root, .. parts]);

    [Fact]
    public void Saves_the_text_and_leaves_no_temp_file()
    {
        var path = P("data", "vault.frv");

        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(P("data")));
    }

    [Fact]
    public void A_leftover_temp_file_does_not_stop_a_save_and_is_not_overwritten()
    {
        var path = P("vault.frv");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path + ".tmp", "somebody else's");

        AtomicFile.WriteAllText(path, "mine");

        Assert.Equal("mine", File.ReadAllText(path));
        Assert.Equal("somebody else's", File.ReadAllText(path + ".tmp"));
    }

    [Fact]
    public void A_temp_file_held_open_does_not_stop_a_save()
    {
        var path = P("vault.frv");
        Directory.CreateDirectory(_root);
        using var held = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None);

        AtomicFile.WriteAllText(path, "mine");

        Assert.Equal("mine", File.ReadAllText(path));
    }

    [Fact]
    public void A_failed_save_leaves_no_temp_file()
    {
        var path = P("vault.frv");
        Directory.CreateDirectory(path); // the final move can't replace a folder

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(path, "mine"));

        Assert.Empty(Directory.GetFiles(_root));
        Assert.Empty(Directory.GetFileSystemEntries(path));
    }

    [UnixFact, UnsupportedOSPlatform("windows")]
    public void A_link_where_the_old_temp_name_was_is_not_followed()
    {
        var path = P("vault.frv");
        var victim = P("victim.txt");
        Directory.CreateDirectory(_root);
        File.WriteAllText(victim, "keep");
        File.CreateSymbolicLink(path + ".tmp", victim);

        AtomicFile.WriteAllText(path, "mine");

        Assert.Equal("keep", File.ReadAllText(victim));
        Assert.Equal("mine", File.ReadAllText(path));
    }

    [UnixFact, UnsupportedOSPlatform("windows")]
    public void A_new_file_is_readable_only_by_its_owner_and_a_new_folder_too()
    {
        var path = P("data", "inner", "vault.frv");

        AtomicFile.WriteAllText(path, "secret");

        Assert.Equal(PrivateFile, File.GetUnixFileMode(path));
        Assert.Equal(PrivateFolder, File.GetUnixFileMode(P("data", "inner")));
    }

    [UnixFact, UnsupportedOSPlatform("windows")]
    public void A_rewrite_keeps_the_file_private_whatever_it_was_before()
    {
        var path = P("vault.frv");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        AtomicFile.WriteAllText(path, "one");
        Assert.Equal(PrivateFile, File.GetUnixFileMode(path));

        File.SetUnixFileMode(path, PrivateFile);
        AtomicFile.WriteAllText(path, "two");
        Assert.Equal(PrivateFile, File.GetUnixFileMode(path));
    }

    [UnixFact, UnsupportedOSPlatform("windows")]
    public void The_lock_file_and_a_new_data_folder_are_private()
    {
        var dataDir = P("fresh", "data");

        using (DataFolderLock.TryAcquire(dataDir))
        {
            Assert.Equal(PrivateFolder, File.GetUnixFileMode(dataDir));
            Assert.Equal(PrivateFile, File.GetUnixFileMode(Path.Combine(dataDir, ".lock")));
        }
    }

    [UnixFact, UnsupportedOSPlatform("windows")]
    public void Opening_an_existing_data_folder_closes_it_to_group_and_others_once()
    {
        var dataDir = P("data");
        Directory.CreateDirectory(dataDir);
        File.SetUnixFileMode(dataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var existing = Path.Combine(dataDir, "settings.json");
        File.WriteAllText(existing, "{}");
        var existingMode = File.GetUnixFileMode(existing);

        using (DataFolderLock.TryAcquire(dataDir))
        {
            Assert.Equal(PrivateFolder, File.GetUnixFileMode(dataDir));
        }

        Assert.Equal(existingMode, File.GetUnixFileMode(existing));
    }

    [UnixFact, UnsupportedOSPlatform("windows")]
    public void Backups_are_private()
    {
        var backups = P("backups");
        var store = new ConnectionStore(P("connections.json"), backups, backupInterval: TimeSpan.Zero);
        var tree = store.Load();
        store.Save(tree);
        store.Save(tree);

        var backup = Assert.Single(Directory.GetFiles(backups));
        Assert.Equal(PrivateFolder, File.GetUnixFileMode(backups));
        Assert.Equal(PrivateFile, File.GetUnixFileMode(backup));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
