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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
