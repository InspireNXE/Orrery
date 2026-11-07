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

    [Fact]
    public void Enlarging_follows_the_app_setting_until_a_folder_or_connection_chooses()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers" };
        var entry = new ConnectionEntry { Name = "web-1", Host = "web-1" };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        Assert.True(SettingsResolver.Resolve(entry).EnlargeRemoteDesktop.IsDefault);
        Assert.True(SettingsResolver.Resolve(entry).Enlarge(appSetting: true));
        Assert.False(SettingsResolver.Resolve(entry).Enlarge(appSetting: false));

        folder.Settings.EnlargeRemoteDesktop = false;
        Assert.False(SettingsResolver.Resolve(entry).Enlarge(appSetting: true));
        Assert.Same(folder, SettingsResolver.Resolve(entry).EnlargeRemoteDesktop.Source);

        entry.Settings.EnlargeRemoteDesktop = true;
        Assert.True(SettingsResolver.Resolve(entry).Enlarge(appSetting: false));
    }

    [Fact]
    public void Where_web_pages_open_follows_the_app_setting_until_a_folder_or_connection_chooses()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Network" };
        var entry = new ConnectionEntry { Name = "router", Host = "router.lan", Settings = { Protocol = Protocol.Https } };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        Assert.True(SettingsResolver.Resolve(entry).WebPagesInOrrery.IsDefault);
        Assert.True(SettingsResolver.Resolve(entry).InOrrery(appSetting: true));
        Assert.False(SettingsResolver.Resolve(entry).InOrrery(appSetting: false));

        folder.Settings.WebPagesInOrrery = false;
        Assert.False(SettingsResolver.Resolve(entry).InOrrery(appSetting: true));
        Assert.Same(folder, SettingsResolver.Resolve(entry).WebPagesInOrrery.Source);

        entry.Settings.WebPagesInOrrery = true;
        Assert.True(SettingsResolver.Resolve(entry).InOrrery(appSetting: false));
    }

    [Fact]
    public void Where_alt_tab_goes_follows_the_app_setting_until_a_folder_or_connection_chooses()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers" };
        var entry = new ConnectionEntry { Name = "desk", Host = "desk.lan", Settings = { Protocol = Protocol.Rdp } };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        Assert.True(SettingsResolver.Resolve(entry).SystemShortcuts.IsDefault);
        Assert.Equal(Orrery.Core.Settings.SystemShortcuts.FullScreen, SettingsResolver.Resolve(entry).ShortcutsGo(Orrery.Core.Settings.SystemShortcuts.FullScreen));
        Assert.Equal(Orrery.Core.Settings.SystemShortcuts.Local, SettingsResolver.Resolve(entry).ShortcutsGo(Orrery.Core.Settings.SystemShortcuts.Local));

        folder.Settings.SystemShortcuts = Orrery.Core.Settings.SystemShortcuts.Remote;
        Assert.Equal(Orrery.Core.Settings.SystemShortcuts.Remote, SettingsResolver.Resolve(entry).ShortcutsGo(Orrery.Core.Settings.SystemShortcuts.FullScreen));
        Assert.Same(folder, SettingsResolver.Resolve(entry).SystemShortcuts.Source);

        entry.Settings.SystemShortcuts = Orrery.Core.Settings.SystemShortcuts.Local;
        Assert.Equal(Orrery.Core.Settings.SystemShortcuts.Local, SettingsResolver.Resolve(entry).ShortcutsGo(Orrery.Core.Settings.SystemShortcuts.Remote));

        // Kept in the saved file, as other settings are.
        var json = System.Text.Json.JsonSerializer.Serialize(entry.Settings);
        Assert.Equal(Orrery.Core.Settings.SystemShortcuts.Local, System.Text.Json.JsonSerializer.Deserialize<InheritableSettings>(json)!.SystemShortcuts);

        // Orrery 0.1.1 doesn't know it, so a file that chooses refuses to load there instead of losing the choice on save.
        Assert.Equal(6, ConnectionStore.SchemaFor(tree.Root));
        folder.Settings.SystemShortcuts = entry.Settings.SystemShortcuts = null;
        Assert.True(ConnectionStore.SchemaFor(tree.Root) < 6);
    }

    [Fact]
    public void Color_correction_follows_the_app_setting_until_a_folder_or_connection_turns_it_off()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Design" };
        var entry = new ConnectionEntry { Name = "mac-1", Host = "mac-1" };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);

        Assert.True(SettingsResolver.Resolve(entry).ColorCorrection.Value);
        Assert.True(SettingsResolver.Resolve(entry).ColorCorrection.IsDefault);

        folder.Settings.ColorCorrection = false;
        Assert.False(SettingsResolver.Resolve(entry).ColorCorrection.Value);
        Assert.Same(folder, SettingsResolver.Resolve(entry).ColorCorrection.Source);

        entry.Settings.ColorCorrection = true;
        Assert.True(SettingsResolver.Resolve(entry).ColorCorrection.Value);
    }
}
