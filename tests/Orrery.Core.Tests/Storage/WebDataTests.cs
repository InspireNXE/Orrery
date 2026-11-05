using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

public sealed class WebDataTests : IDisposable
{
    readonly string _dataDir = Path.Combine(Path.GetTempPath(), "orrery-web-" + Guid.NewGuid().ToString("N"));

    string Web => WebData.FolderIn(_dataDir);

    [Fact]
    public void Clearing_removes_each_engines_folders_and_then_the_empty_web_folder()
    {
        Directory.CreateDirectory(Path.Combine(Web, "EBWebView", "Default"));
        File.WriteAllText(Path.Combine(Web, "EBWebView", "Default", "Cookies"), "x");
        Directory.CreateDirectory(Path.Combine(Web, "data"));
        Directory.CreateDirectory(Path.Combine(Web, "cache"));
        Assert.True(WebData.Exists(_dataDir));

        WebData.Clear(_dataDir);

        Assert.False(Directory.Exists(Web));
        Assert.True(Directory.Exists(_dataDir));
    }

    [Fact]
    public void Clearing_at_start_waits_for_the_next_start_then_clears()
    {
        Directory.CreateDirectory(Path.Combine(Web, "EBWebView"));
        Assert.False(WebData.ClearIfAsked(_dataDir));

        WebData.ClearAtStart(_dataDir);
        Assert.True(Directory.Exists(Path.Combine(Web, "EBWebView")));

        Assert.True(WebData.ClearIfAsked(_dataDir));
        Assert.False(Directory.Exists(Web));
    }

    [Fact]
    public void Clearing_a_folder_with_no_web_data_does_nothing()
    {
        Directory.CreateDirectory(_dataDir);

        WebData.Clear(_dataDir);

        Assert.False(WebData.Exists(_dataDir));
        Assert.True(Directory.Exists(_dataDir));
    }

    [Fact]
    public void Each_computer_has_its_own_profile_and_clearing_takes_only_this_one_s()
    {
        Assert.Equal(Path.Combine(_dataDir, "web", ComputerFiles.Tag), Web);
        var other = Path.Combine(_dataDir, "web", "abc123");
        Directory.CreateDirectory(Path.Combine(other, "EBWebView"));
        Directory.CreateDirectory(Path.Combine(Web, "EBWebView"));

        WebData.Clear(_dataDir);

        Assert.False(Directory.Exists(Web));
        Assert.True(Directory.Exists(Path.Combine(other, "EBWebView")));

        WebData.ClearAll(_dataDir); // An old copy deleted takes every computer's.
        Assert.False(Directory.Exists(Path.Combine(_dataDir, "web")));
    }

    [Fact]
    public void A_profile_from_before_becomes_this_computer_s()
    {
        var top = Path.Combine(_dataDir, "web");
        Directory.CreateDirectory(Path.Combine(top, "EBWebView", "Default"));
        File.WriteAllText(Path.Combine(top, "EBWebView", "Default", "Cookies"), "x");

        WebData.MoveOldProfile(_dataDir);

        Assert.True(File.Exists(Path.Combine(Web, "EBWebView", "Default", "Cookies")));
        Assert.False(Directory.Exists(Path.Combine(top, "EBWebView")));
    }

    [Fact]
    public void A_portable_copy_s_cache_is_each_computer_s_and_old_downloads_go()
    {
        var location = DataLocationResolver.ForPortable(_dataDir, PlatformFolders.ForCurrentOs(), PortableDataChoice.WithApp);
        Assert.Equal(Path.Combine(_dataDir, "data", "cache", ComputerFiles.Tag), location.CacheDir);

        var old = Path.Combine(location.DataDir, "cache", "updates", "0.1.1");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "Orrery.zip"), "x");
        ComputerFiles.TidyOldCache(location);
        Assert.False(Directory.Exists(Path.Combine(location.DataDir, "cache", "updates")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }
}
