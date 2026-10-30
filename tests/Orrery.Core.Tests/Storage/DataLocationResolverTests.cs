using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

public class DataLocationResolverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _appDir;
    readonly PlatformFolders _folders;

    public DataLocationResolverTests()
    {
        _appDir = Path.Combine(_root, "stick", "Orrery");
        Directory.CreateDirectory(_appDir);
        _folders = new PlatformFolders(
            Path.Combine(_root, "profile", "config"),
            Path.Combine(_root, "profile", "cache"),
            Path.Combine(_root, "profile", "logs"));
    }

    void WriteMarker(string json) => File.WriteAllText(Path.Combine(_appDir, PortableMarker.FileName), json);

    [Fact]
    public void No_marker_means_installed_mode_in_platform_folders()
    {
        var ready = Assert.IsType<StartupStorage.Ready>(DataLocationResolver.Resolve(_appDir, _folders));

        Assert.Equal(StorageMode.Installed, ready.Location.Mode);
        Assert.Equal(_folders.Config, ready.Location.DataDir);
        Assert.Equal(_folders.Cache, ready.Location.CacheDir);
        Assert.True(ready.Location.UsesOsKeychain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"dataLocation\":\"custom\"}")]
    public void Marker_without_a_usable_choice_asks_first(string json)
    {
        WriteMarker(json);
        Assert.IsType<StartupStorage.NeedsPortableChoice>(DataLocationResolver.Resolve(_appDir, _folders));
    }

    [Fact]
    public void With_app_keeps_everything_beside_the_app()
    {
        WriteMarker("{\"version\":1,\"dataLocation\":\"app\"}");
        var location = Assert.IsType<StartupStorage.Ready>(DataLocationResolver.Resolve(_appDir, _folders)).Location;

        Assert.Equal(StorageMode.Portable, location.Mode);
        Assert.Equal(Path.Combine(_appDir, "data"), location.DataDir);
        Assert.StartsWith(location.DataDir, location.CacheDir);
        Assert.StartsWith(location.DataDir, location.LogDir);
        Assert.False(location.UsesOsKeychain);
    }

    [Fact]
    public void User_profile_choice_does_not_collide_with_installed_copy()
    {
        WriteMarker("{\"version\":1,\"dataLocation\":\"user\"}");
        var location = Assert.IsType<StartupStorage.Ready>(DataLocationResolver.Resolve(_appDir, _folders)).Location;

        Assert.Equal(Path.Combine(_folders.Config, "Portable"), location.DataDir);
    }

    [Fact]
    public void Relative_custom_path_resolves_from_the_app_folder()
    {
        WriteMarker("{\"version\":1,\"dataLocation\":\"custom\",\"customPath\":\"../RemoteData\"}");
        var location = Assert.IsType<StartupStorage.Ready>(DataLocationResolver.Resolve(_appDir, _folders)).Location;

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "stick", "RemoteData")), location.DataDir);
    }

    [Fact]
    public void Custom_folder_on_the_same_drive_is_stored_relative()
    {
        var chosen = Path.Combine(_root, "stick", "RemoteData");
        var stored = DataLocationResolver.ToStoredCustomPath(_appDir, chosen);

        Assert.False(Path.IsPathRooted(stored));
        Assert.Equal(Path.GetFullPath(chosen),
            DataLocationResolver.ForPortable(_appDir, _folders, PortableDataChoice.Custom, stored).DataDir);
    }

    [Fact]
    public void Saved_choice_round_trips_through_the_marker()
    {
        Assert.True(PortableMarker.For(PortableDataChoice.UserProfile).TrySave(_appDir));

        var marker = PortableMarker.TryLoad(_appDir);
        Assert.Equal(PortableDataChoice.UserProfile, marker?.Choice);
    }

    [Fact]
    public void App_directory_on_macOS_is_the_folder_holding_the_bundle()
    {
        var baseDir = Path.Combine(_root, "Volumes", "Stick", "Orrery.app", "Contents", "MacOS") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.Combine(_root, "Volumes", "Stick"), DataLocationResolver.AppDirectory(baseDir));
    }

    [Fact]
    public void App_directory_elsewhere_is_the_base_directory()
    {
        Assert.Equal(_appDir, DataLocationResolver.AppDirectory(_appDir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Second_lock_on_the_same_folder_fails_until_the_first_is_released()
    {
        var dataDir = Path.Combine(_root, "data");
        using (var first = DataFolderLock.TryAcquire(dataDir))
        {
            Assert.NotNull(first);
            Assert.Null(DataFolderLock.TryAcquire(dataDir));
        }

        using var again = DataFolderLock.TryAcquire(dataDir);
        Assert.NotNull(again);
    }

    [Fact]
    public void Writable_probe_leaves_no_files_behind()
    {
        var dir = Path.Combine(_root, "probe");
        Assert.True(DataLocationResolver.IsWritable(dir));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public void Saved_marker_holds_only_the_choice_fields()
    {
        Assert.True(PortableMarker.For(PortableDataChoice.Custom, "../moved").TrySave(_appDir));

        var json = File.ReadAllText(Path.Combine(_appDir, PortableMarker.FileName));
        Assert.DoesNotContain("Choice", json);
        Assert.Equal(PortableDataChoice.Custom, PortableMarker.TryLoad(_appDir)!.Choice);
    }

    [Fact]
    public void Orrery_home_redirects_the_platform_folders()
    {
        var previous = Environment.GetEnvironmentVariable(PlatformFolders.HomeOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(PlatformFolders.HomeOverrideVariable, _root);
            var folders = PlatformFolders.ForCurrentOs();
            Assert.Equal(Path.Combine(_root, "config"), folders.Config);
            Assert.Equal(Path.Combine(_root, "logs"), folders.Logs);

            Environment.SetEnvironmentVariable(PlatformFolders.HomeOverrideVariable, "relative/path");
            Assert.NotEqual(Path.Combine("relative/path", "config"), PlatformFolders.ForCurrentOs().Config);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlatformFolders.HomeOverrideVariable, previous);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
