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

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }
}
