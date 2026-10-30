using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

public class DataMoverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _source;

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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
