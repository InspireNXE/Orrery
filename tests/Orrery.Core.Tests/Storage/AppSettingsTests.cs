using Orrery.Core.Credentials;
using Orrery.Core.Network;
using Orrery.Core.Settings;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Storage;

public class AppSettingsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    string SettingsPath => Path.Combine(_root, AppSettingsStore.FileName);

    [Fact]
    public void Saves_and_loads()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Save(new AppSettings { CredentialStore = CredentialStoreKind.VaultFile, AutoLockMinutes = 5 });

        var loaded = store.Load();

        Assert.Equal(CredentialStoreKind.VaultFile, loaded.CredentialStore);
        Assert.Equal(TimeSpan.FromMinutes(5), loaded.AutoLockAfter);
        Assert.Contains("\"vaultFile\"", File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{\"autoLockMinutes\":\"soon\"}")]
    public void Missing_or_damaged_settings_give_the_defaults(string? contents)
    {
        if (contents is not null)
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(SettingsPath, contents);
        }

        var settings = new AppSettingsStore(SettingsPath).Load();

        Assert.Null(settings.CredentialStore);
        Assert.Equal(CredentialStores.DefaultAutoLock, settings.AutoLockAfter);
    }

    [Fact]
    public void Remote_desktop_zoom_is_on_unless_turned_off()
    {
        var store = new AppSettingsStore(SettingsPath);
        Assert.True(store.Load().ZoomRemoteDesktops);

        store.Save(new AppSettings { RemoteDesktopZoom = false });
        Assert.False(store.Load().ZoomRemoteDesktops);
    }

    [Fact]
    public void Web_pages_open_in_orrery_unless_the_browser_is_chosen()
    {
        var store = new AppSettingsStore(SettingsPath);
        Assert.True(store.Load().OpensWebPagesInOrrery);

        store.Save(new AppSettings { WebPagesInOrrery = false });
        Assert.False(store.Load().OpensWebPagesInOrrery);
    }

    [Fact]
    public void Hosts_are_checked_by_their_port_unless_ping_is_chosen()
    {
        var store = new AppSettingsStore(SettingsPath);
        Assert.Equal(HostCheckMethod.Tcp, store.Load().HostCheckMethod);

        store.Save(new AppSettings { HostStatusMethod = HostCheckMethod.Ping });
        Assert.Equal(HostCheckMethod.Ping, store.Load().HostCheckMethod);
        Assert.Contains("\"hostStatusMethod\": \"ping\"", File.ReadAllText(SettingsPath));

        store.Save(store.Load() with { HostStatusMethod = HostCheckMethod.Tcp });
        Assert.Equal(HostCheckMethod.Tcp, store.Load().HostCheckMethod);
        Assert.Contains("\"tcp\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Recent_connections_are_remembered_unless_turned_off()
    {
        var store = new AppSettingsStore(SettingsPath);
        Assert.True(store.Load().RemembersRecentConnections);

        store.Save(new AppSettings { RememberRecentConnections = false });
        Assert.False(store.Load().RemembersRecentConnections);
        Assert.Contains("\"rememberRecentConnections\": false", File.ReadAllText(SettingsPath));

        store.Save(new AppSettings { RememberRecentConnections = true });
        Assert.True(store.Load().RemembersRecentConnections);
    }

    [Fact]
    public void Color_settings_are_standard_unless_chosen()
    {
        var store = new AppSettingsStore(SettingsPath);
        Assert.Equal(InterfaceColorScheme.Standard, store.Load().InterfaceColorScheme);
        Assert.Null(store.Load().TerminalColors);

        store.Save(new AppSettings { InterfaceColors = InterfaceColorScheme.Clearer, TerminalColors = TerminalColorScheme.ColorBlind });

        Assert.Equal(InterfaceColorScheme.Clearer, store.Load().InterfaceColorScheme);
        Assert.Equal(TerminalColorScheme.ColorBlind, store.Load().TerminalColors);
        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"clearer\"", json);
        Assert.Contains("\"colorBlind\"", json);
    }

    [Theory]
    [InlineData("{ \"colourBlindColours\": true }", InterfaceColorScheme.ColorBlind)]
    [InlineData("{ \"colourBlindColours\": false }", InterfaceColorScheme.Standard)]
    [InlineData("{ }", InterfaceColorScheme.Standard)]
    [InlineData("{ \"colourBlindColours\": true, \"interfaceColours\": \"clearer\" }", InterfaceColorScheme.Clearer)]
    public void The_old_color_blind_setting_becomes_an_interface_color_choice(string json, InterfaceColorScheme expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, json);
        var store = new AppSettingsStore(SettingsPath);

        var loaded = store.Load();
        Assert.Equal(expected, loaded.InterfaceColorScheme);
        Assert.Null(loaded.ColorBlindColors);

        // Saving it back no longer writes the old key.
        store.Save(loaded);
        Assert.DoesNotContain("colourBlindColours", File.ReadAllText(SettingsPath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, store.Load().InterfaceColorScheme);
    }

    [Fact]
    public void Saving_the_old_setting_writes_the_new_one()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Save(new AppSettings { ColorBlindColors = true });

        var json = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("colourBlindColours", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(InterfaceColorScheme.ColorBlind, store.Load().InterfaceColors);
    }

    [Fact]
    public void Color_vision_and_correction_strength_round_trip()
    {
        var store = new AppSettingsStore(SettingsPath);
        Assert.Equal(ColorVision.None, store.Load().Vision);
        Assert.Equal(AppSettings.DefaultColorCorrectionStrength, store.Load().CorrectionStrength);

        store.Save(new AppSettings { ColorVision = ColorVision.Deuteranopia, ColorCorrectionStrength = 60, InterfaceColors = InterfaceColorScheme.Clearer });

        var loaded = store.Load();
        Assert.Equal(ColorVision.Deuteranopia, loaded.Vision);
        Assert.Equal(60, loaded.CorrectionStrength);
        Assert.Equal(InterfaceColorScheme.Clearer, loaded.InterfaceColorScheme); // The palette choice stays the user's.
        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"colorVision\": \"deuteranopia\"", json);
        Assert.Contains("\"colorCorrectionStrength\": 60", json);
    }

    [Theory]
    [InlineData("{ \"colorCorrectionStrength\": 250 }", 100)]
    [InlineData("{ \"colorCorrectionStrength\": -5 }", 0)]
    [InlineData("{ }", 100)]
    public void A_correction_strength_out_of_range_is_brought_into_it(string json, int expected)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, json);

        Assert.Equal(expected, new AppSettingsStore(SettingsPath).Load().CorrectionStrength);
    }

    [Fact]
    public void Settings_from_before_color_vision_still_load()
    {
        // As the previous version wrote them, with the older on/off key too.
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, "{ \"schemaVersion\": 1, \"colourBlindColours\": true, \"terminalColours\": \"colourBlind\" }");

        var loaded = new AppSettingsStore(SettingsPath).Load();

        Assert.Equal(ColorVision.None, loaded.Vision);
        Assert.Null(loaded.ColorVision);
        Assert.Equal(InterfaceColorScheme.ColorBlind, loaded.InterfaceColorScheme);
        Assert.Equal(TerminalColorScheme.ColorBlind, loaded.TerminalColors);
    }

    // Builds before the switch to American spelling wrote British keys and values.
    const string BritishSettings = """
        {
          "schemaVersion": 1,
          "terminalColours": "colourBlind",
          "interfaceColours": "colourBlind",
          "colourVision": "tritanopia",
          "colourCorrectionStrength": 40
        }
        """;

    [Fact]
    public void Settings_with_british_keys_load_with_the_same_meaning()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, BritishSettings);

        var loaded = new AppSettingsStore(SettingsPath).Load();

        Assert.Equal(TerminalColorScheme.ColorBlind, loaded.TerminalColors);
        Assert.Equal(InterfaceColorScheme.ColorBlind, loaded.InterfaceColors);
        Assert.Equal(ColorVision.Tritanopia, loaded.ColorVision);
        Assert.Equal(40, loaded.CorrectionStrength);
    }

    [Fact]
    public void Settings_with_british_keys_are_saved_back_with_american_ones()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, BritishSettings);
        var store = new AppSettingsStore(SettingsPath);

        var before = store.Load();
        store.Save(before);

        Assert.Equal(before, store.Load());
        var json = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("colour", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"terminalColors\": \"colorBlind\"", json);
        Assert.Contains("\"interfaceColors\": \"colorBlind\"", json);
        Assert.Contains("\"colorVision\": \"tritanopia\"", json);
        Assert.Contains("\"colorCorrectionStrength\": 40", json);
    }

    [Fact]
    public void New_settings_are_written_with_american_keys_and_values()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Save(new AppSettings
        {
            TerminalColors = TerminalColorScheme.ColorBlind,
            InterfaceColors = InterfaceColorScheme.ColorBlind,
            ColorVision = ColorVision.Protanopia,
            ColorCorrectionStrength = 75,
        });

        var json = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("colour", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"terminalColors\": \"colorBlind\"", json);
        Assert.Contains("\"interfaceColors\": \"colorBlind\"", json);
        Assert.Contains("\"colorVision\": \"protanopia\"", json);
        Assert.Contains("\"colorCorrectionStrength\": 75", json);
    }

    [Theory]
    [InlineData("""{ "interfaceColours": "colourBlind", "interfaceColors": "clearer" }""")]
    [InlineData("""{ "interfaceColors": "clearer", "interfaceColours": "colourBlind" }""")]
    public void An_american_key_wins_over_a_british_one(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, json);

        Assert.Equal(InterfaceColorScheme.Clearer, new AppSettingsStore(SettingsPath).Load().InterfaceColors);
    }

    [Fact]
    public void A_value_misspelled_some_other_way_still_gives_the_defaults()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(SettingsPath, """{ "autoLockMinutes": 5, "terminalColors": "colorBlindd" }""");

        Assert.Equal(CredentialStores.DefaultAutoLock, new AppSettingsStore(SettingsPath).Load().AutoLockAfter);
    }

    [Fact]
    public void Zero_minutes_means_never_lock()
    {
        Assert.Null(new AppSettings { AutoLockMinutes = 0 }.AutoLockAfter);
    }

    [Fact]
    public void Portable_mode_always_uses_the_vault()
    {
        var portable = new DataLocation(StorageMode.Portable, _root, _root, _root);

        Assert.Equal(CredentialStoreKind.VaultFile, CredentialStores.KindFor(portable, new AppSettings { CredentialStore = CredentialStoreKind.OsKeychain }));
    }

    [Fact]
    public void Installed_mode_on_windows_defaults_to_the_keychain_unless_the_vault_is_chosen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installed = new DataLocation(StorageMode.Installed, _root, _root, _root);

        Assert.Equal(CredentialStoreKind.OsKeychain, CredentialStores.KindFor(installed, new AppSettings()));
        Assert.Equal(CredentialStoreKind.VaultFile, CredentialStores.KindFor(installed, new AppSettings { CredentialStore = CredentialStoreKind.VaultFile }));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(0, 1)]
    [InlineData(3, 2)]
    [InlineData(10, 10)]
    [InlineData(12, 10)]
    [InlineData(13, 15)]
    [InlineData(22, 15)]
    [InlineData(23, 30)]
    [InlineData(44, 30)]
    [InlineData(45, 60)]
    [InlineData(240, 60)]
    public void A_hand_edited_frame_rate_is_brought_to_the_nearest_one_on_offer(int? stored, int used)
    {
        Assert.Equal(used, new AppSettings { RecordingFrameRate = stored }.RecordingFramesPerSecond);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
