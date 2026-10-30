using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Credentials;

public class KeychainCredentialStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    string IndexPath => Path.Combine(_dir, "credentials.json");

    /// <summary>A keychain in memory, so the store's own logic is tested the same on every OS.</summary>
    sealed class MemoryBackend(int? maxSize = null) : ISecretBackend
    {
        public Dictionary<string, byte[]> Items { get; } = [];
        public string Name => "the test keychain";
        public int? MaxSecretSize => maxSize;
        public void Write(string key, byte[] secret) => Items[key] = [.. secret];
        public byte[]? Read(string key) => Items.TryGetValue(key, out var secret) ? [.. secret] : null;
        public void Delete(string key) => Items.Remove(key);
    }

    [Fact]
    public async Task Secrets_go_to_the_keychain_and_only_metadata_to_the_index()
    {
        var backend = new MemoryBackend();
        var key = new Credential(Guid.NewGuid(), "deploy", CredentialKind.SshKeyWithPassphrase, "git", null, "dummy-key", "dummy-phrase", "notes");

        await new KeychainCredentialStore(IndexPath, backend).SaveAsync(key);

        Assert.Equal(key, await new KeychainCredentialStore(IndexPath, backend).GetAsync(key.Id));
        Assert.Equal([KeychainCredentialStore.KeyPrefix + key.Id], backend.Items.Keys);
        var index = await File.ReadAllTextAsync(IndexPath);
        Assert.DoesNotContain("dummy-key", index);
        Assert.DoesNotContain("dummy-phrase", index);
        Assert.Contains("deploy", index);
    }

    [Fact]
    public async Task Deleting_removes_both_and_a_missing_secret_reads_as_none()
    {
        var backend = new MemoryBackend();
        var store = new KeychainCredentialStore(IndexPath, backend);
        var credential = new Credential(Guid.NewGuid(), "admin", CredentialKind.Password, "root", null, "dummy-pass");
        await store.SaveAsync(credential);

        // Removed from the keychain outside Orrery: listed, but can't be read.
        backend.Items.Clear();
        Assert.Single(await store.ListAsync());
        Assert.Null(await store.GetAsync(credential.Id));

        await store.DeleteAsync(credential.Id);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task A_secret_too_big_for_the_keychain_is_refused_with_its_name()
    {
        var store = new KeychainCredentialStore(IndexPath, new MemoryBackend(maxSize: 64));
        var big = new Credential(Guid.NewGuid(), "key", CredentialKind.SshKey, "git", null, new string('k', 200));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(big));
        Assert.Contains("the test keychain", error.Message);
        Assert.Empty(await store.ListAsync());
    }

    /// <summary>The real Windows Credential Manager; removes what it writes.</summary>
    [Fact]
    public async Task Round_trips_through_windows_credential_manager()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new KeychainCredentialStore(IndexPath, new WindowsCredentialBackend());
        var credential = new Credential(Guid.NewGuid(), "test entry", CredentialKind.Password, "admin", "corp", "Sup3r-Secret!");
        try
        {
            await store.SaveAsync(credential);
            Assert.Equal(credential, await new KeychainCredentialStore(IndexPath, new WindowsCredentialBackend()).GetAsync(credential.Id));
            Assert.DoesNotContain("Sup3r-Secret!", await File.ReadAllTextAsync(IndexPath));
        }
        finally
        {
            await store.DeleteAsync(credential.Id);
        }

        Assert.Null(await store.GetAsync(credential.Id));
    }

    [Fact]
    public void This_system_has_a_keychain_where_one_is_expected()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsCredentialBackend>(CredentialStores.OsKeychain);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacKeychainBackend>(CredentialStores.OsKeychain);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
