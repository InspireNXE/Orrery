using Orrery.Core.Settings;
using Orrery.Core.Storage;

namespace Orrery.Core.Credentials;

public static class CredentialStores
{
    public static readonly TimeSpan DefaultAutoLock = TimeSpan.FromMinutes(AppSettings.DefaultAutoLockMinutes);

    /// <summary>
    /// True where Orrery can use the OS keychain: installed mode on Windows. Portable mode always uses the
    /// vault file, since the keychain doesn't travel; so do macOS and Linux until their keychains are supported.
    /// </summary>
    public static bool CanUseOsKeychain(DataLocation location) => location.UsesOsKeychain && OperatingSystem.IsWindows();

    /// <summary>The store <paramref name="settings"/> asks for, or the vault file where that isn't available.</summary>
    public static CredentialStoreKind KindFor(DataLocation location, AppSettings settings) =>
        CanUseOsKeychain(location) && settings.CredentialStore != CredentialStoreKind.VaultFile
            ? CredentialStoreKind.OsKeychain
            : CredentialStoreKind.VaultFile;

    public static ICredentialStore For(DataLocation location, AppSettings settings) =>
        Create(location, KindFor(location, settings), settings.AutoLockAfter);

    public static ICredentialStore Create(DataLocation location, CredentialStoreKind kind, TimeSpan? autoLockAfter)
    {
        if (kind == CredentialStoreKind.OsKeychain && CanUseOsKeychain(location) && OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore(location.FileIn("credentials.json"));
        }

        return new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: autoLockAfter);
    }
}
