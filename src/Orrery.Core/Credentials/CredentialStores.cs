using Orrery.Core.Storage;

namespace Orrery.Core.Credentials;

public static class CredentialStores
{
    public static readonly TimeSpan DefaultAutoLock = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Installed mode on Windows uses Credential Manager. Portable mode always uses the vault file, since
    /// the OS keychain doesn't travel; so do macOS and Linux until their keychains are supported.
    /// </summary>
    public static ICredentialStore For(DataLocation location)
    {
        if (location.UsesOsKeychain && OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore(location.FileIn("credentials.json"));
        }

        return new VaultFileStore(location.FileIn("vault.frv"), autoLockAfter: DefaultAutoLock);
    }
}
