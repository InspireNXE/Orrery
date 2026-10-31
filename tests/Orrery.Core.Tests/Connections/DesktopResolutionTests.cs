using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

public class DesktopResolutionTests
{
    [Theory]
    [InlineData("1920x1080", 1920, 1080)]
    [InlineData(" 1280 X 720 ", 1280, 720)]
    [InlineData("1366×768", 1364, 768)] // Width rounded down to a multiple of 4.
    [InlineData("200*200", 200, 200)]
    public void Fixed_sizes_parse(string text, int width, int height)
    {
        Assert.True(DesktopResolution.TryParse(text, out var size, out var error));
        Assert.Null(error);
        Assert.Equal((width, height), size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_means_fit_the_window(string? text)
    {
        Assert.True(DesktopResolution.TryParse(text, out var size, out _));
        Assert.Null(size);
    }

    [Theory]
    [InlineData("1920")]
    [InlineData("1920x")]
    [InlineData("big")]
    [InlineData("1920x1080x2")]
    [InlineData("-1920x1080")]
    [InlineData("100x100")]
    [InlineData("9000x1080")]
    public void Anything_else_is_refused(string text)
    {
        Assert.False(DesktopResolution.TryParse(text, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void A_desktop_size_set_on_a_folder_is_inherited_and_empty_overrides_it()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Kiosks", Settings = { DesktopSize = "1280x720" } };
        var entry = new ConnectionEntry { Name = "kiosk-1", Host = "kiosk-1" };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        Assert.Equal("1280x720", SettingsResolver.Resolve(entry).DesktopSize.Value);
        entry.Settings.DesktopSize = string.Empty;
        Assert.Null(SettingsResolver.Resolve(entry).DesktopSize.Value);
    }
}
