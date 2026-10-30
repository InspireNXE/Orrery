using Orrery.Core.Settings;
using Orrery.Core.Storage;

namespace Orrery.Core.Credentials;

public static class CredentialStores
{
    public static readonly TimeSpan DefaultAutoLock = TimeSpan.FromMinutes(AppSettings.DefaultAutoLockMinutes);

    // Found once per run: on Linux, finding out means asking the Secret Service over D-Bus.
    static readonly Lazy<ISecretBackend?> OsBackend = new(FindOsKeychain);

    /// <summary>
    /// This system's keychain: Windows Credential Manager, the macOS Keychain, or the Secret Service on Linux
    /// when one is running. Null when there's none, e.g. a headless Linux server.
    /// </summary>
    public static ISecretBackend? OsKeychain => OsBackend.Value;

    /// <summary>
    /// True where Orrery can use the OS keychain: installed mode with a keychain present. Portable mode always
    /// uses the vault file, since the keychain doesn't travel with the app.
    /// </summary>
    public static bool CanUseOsKeychain(DataLocation location) => location.UsesOsKeychain && OsKeychain is not null;

    /// <summary>The store <paramref name="settings"/> asks for, or the vault file where that isn't available.</summary>
    public static CredentialStoreKind KindFor(DataLocation location, AppSettings settings) =>
        CanUseOsKeychain(location) && settings.CredentialStore != CredentialStoreKind.VaultFile
            ? CredentialStoreKind.OsKeychain
            : CredentialStoreKind.VaultFile;

    public static ICredentialStore For(DataLocation location, AppSettings settings) =>
        Create(location, KindFor(location, settings), settings.AutoLockAfter);

    public static ICredentialStore Create(DataLocation location, CredentialStoreKind kind, TimeSpan? autoLockAfter)
    {
        if (kind == CredentialStoreKind.OsKeychain && CanUseOsKeychain(location) && OsKeychain is { } backend)
        {
            return new KeychainCredentialStore(location.FileIn("credentials.json"), backend);
        }

        return new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: autoLockAfter);
    }

    static ISecretBackend? FindOsKeychain()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialBackend();
        if (OperatingSystem.IsMacOS()) return new MacKeychainBackend();
        if (OperatingSystem.IsLinux()) return LinuxSecretServiceBackend.TryCreate();
        return null;
    }
}
