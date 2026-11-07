using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Credentials;

public class CredentialCopierTests : IDisposable
{
    static readonly VaultFileStore.KdfSettings FastKdf = new(MemoryKiB: 8 * 1024, Iterations: 1);

    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    async Task<VaultFileStore> OpenVaultAsync(string name)
    {
        var store = new VaultFileStore(Path.Combine(_root, name), FastKdf);
        await store.UnlockAsync("correct horse battery");
        return store;
    }

    [Fact]
    public async Task Copies_every_credential_with_its_id()
    {
        using var from = await OpenVaultAsync("a.frv");
        using var to = await OpenVaultAsync("b.frv");
        var password = new Credential(Guid.NewGuid(), "admin", CredentialKind.Password, "root", "CORP", "dummy-pass", Notes: "test");
        var key = new Credential(Guid.NewGuid(), "deploy key", CredentialKind.SshKeyWithPassphrase, "git", null, "dummy-key", "dummy-phrase");
        await from.SaveAsync(password);
        await from.SaveAsync(key);

        var copied = await CredentialCopier.CopyAllAsync(from, to);

        Assert.Equal(2, copied.Count);
        Assert.Equal(password, await to.GetAsync(password.Id));
        Assert.Equal(key, await to.GetAsync(key.Id));
        Assert.Equal(password, await from.GetAsync(password.Id));
    }

    [Fact]
    public async Task A_locked_source_fails_without_copying()
    {
        using var from = await OpenVaultAsync("a.frv");
        using var to = await OpenVaultAsync("b.frv");
        await from.SaveAsync(new Credential(Guid.NewGuid(), "admin", CredentialKind.Password, "root", null, "dummy-pass"));
        from.Lock();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => CredentialCopier.CopyAllAsync(from, to));
        Assert.Empty(await to.ListAsync());
    }

    [Fact]
    public async Task A_credential_too_large_for_the_new_store_is_named()
    {
        using var from = await OpenVaultAsync("a.frv");
        await from.SaveAsync(new Credential(Guid.NewGuid(), "admin", CredentialKind.Password, "root", null, "dummy-pass"));
        await from.SaveAsync(new Credential(Guid.NewGuid(), "big RSA key", CredentialKind.SshKey, "git", null, new string('k', 4000)));
        var to = new KeychainCredentialStore(Path.Combine(_root, "credentials.json"), new MemorySecrets());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CredentialCopier.CopyAllAsync(from, to));

        Assert.StartsWith("\"big RSA key\" couldn't be copied. This credential is too large for test keychain, which holds at most 2.5 KB.", error.Message);
        Assert.Contains("encrypted vault file", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
