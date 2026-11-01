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
    public void Colour_settings_are_off_unless_chosen()
    {
        var store = new AppSettingsStore(SettingsPath);
        Assert.False(store.Load().UseColourBlindColours);
        Assert.Null(store.Load().TerminalColours);

        store.Save(new AppSettings { ColourBlindColours = true, TerminalColours = TerminalColourScheme.ColourBlind });

        Assert.True(store.Load().UseColourBlindColours);
        Assert.Equal(TerminalColourScheme.ColourBlind, store.Load().TerminalColours);
        Assert.Contains("\"colourBlind\"", File.ReadAllText(SettingsPath));
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
