using Orrery.Core.Credentials;
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
    public void Colour_settings_are_standard_unless_chosen()
    {
        var store = new AppSettingsStore(SettingsPath);
        Assert.Equal(InterfaceColourScheme.Standard, store.Load().InterfaceColourScheme);
        Assert.Null(store.Load().TerminalColours);

        store.Save(new AppSettings { InterfaceColours = InterfaceColourScheme.Clearer, TerminalColours = TerminalColourScheme.ColourBlind });

        Assert.Equal(InterfaceColourScheme.Clearer, store.Load().InterfaceColourScheme);
        Assert.Equal(TerminalColourScheme.ColourBlind, store.Load().TerminalColours);
        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"clearer\"", json);
        Assert.Contains("\"colourBlind\"", json);
    }

    [Theory]
    [InlineData("{ \"colourBlindColours\": true }", InterfaceColourScheme.ColourBlind)]
    [InlineData("{ \"colourBlindColours\": false }", InterfaceColourScheme.Standard)]
    [InlineData("{ }", InterfaceColourScheme.Standard)]
    [InlineData("{ \"colourBlindColours\": true, \"interfaceColours\": \"clearer\" }", InterfaceColourScheme.Clearer)]
    public void The_old_colour_blind_setting_becomes_an_interface_colour_choice(string json, InterfaceColourScheme expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, json);
        var store = new AppSettingsStore(SettingsPath);

        var loaded = store.Load();
        Assert.Equal(expected, loaded.InterfaceColourScheme);
        Assert.Null(loaded.ColourBlindColours);

        // Saving it back no longer writes the old key.
        store.Save(loaded);
        Assert.DoesNotContain("colourBlindColours", File.ReadAllText(SettingsPath), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, store.Load().InterfaceColourScheme);
    }

    [Fact]
    public void Saving_the_old_setting_writes_the_new_one()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Save(new AppSettings { ColourBlindColours = true });

        var json = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain("colourBlindColours", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(InterfaceColourScheme.ColourBlind, store.Load().InterfaceColours);
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
