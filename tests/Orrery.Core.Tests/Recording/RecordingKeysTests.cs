using Orrery.Core.Credentials;
using Orrery.Core.Recording;
using Orrery.Core.Tests.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Recording;

public sealed class RecordingKeysTests : IDisposable
{
    static readonly VaultFileStore.KdfSettings FastKdf = new(MemoryKiB: 8 * 1024, Iterations: 1);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    public RecordingKeysTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    async Task<VaultFileStore> NewVaultAsync()
    {
        var vault = new VaultFileStore(Path.Combine(_dir, "vault.frv"), FastKdf);
        await vault.UnlockAsync("correct horse");
        return vault;
    }

    [Fact]
    public async Task The_pair_is_made_once_and_never_replaced()
    {
        using var vault = await NewVaultAsync();
        var first = await RecordingKeys.EnsureAsync(vault);
        var again = await RecordingKeys.EnsureAsync(vault);

        Assert.Equal(first, again);
        Assert.Equal(first, RecordingKeys.PublicKeyOf((await RecordingKeys.PrivateKeyAsync(vault))!));
    }

    [Fact]
    public async Task The_private_key_is_hidden_from_the_credential_list()
    {
        using var vault = await NewVaultAsync();
        await vault.SaveAsync(new Credential(Guid.NewGuid(), "Admin", CredentialKind.Password, "admin", null, "pw"));
        await RecordingKeys.EnsureAsync(vault);

        var listed = await vault.ListAsync();
        Assert.Equal("Admin", Assert.Single(listed).Name);
        Assert.NotNull(await vault.GetAsync(RecordingKeys.CredentialId));

        var keychain = new KeychainCredentialStore(Path.Combine(_dir, "credentials.json"), new MemorySecrets());
        await RecordingKeys.EnsureAsync(keychain);
        Assert.Empty(await keychain.ListAsync());
    }

    [Fact]
    public async Task It_survives_a_master_password_change_and_a_new_unlock()
    {
        var path = Path.Combine(_dir, "vault.frv");
        byte[] publicKey;
        using (var vault = await NewVaultAsync())
        {
            publicKey = await RecordingKeys.EnsureAsync(vault);
            await vault.ChangePasswordAsync("correct horse", "battery staple");
        }

        using var reopened = new VaultFileStore(path, FastKdf);
        await reopened.UnlockAsync("battery staple");
        Assert.Equal(publicKey, RecordingKeys.PublicKeyOf((await RecordingKeys.PrivateKeyAsync(reopened))!));
    }

    [Fact]
    public async Task Switching_stores_carries_the_key_along()
    {
        using var vault = await NewVaultAsync();
        var publicKey = await RecordingKeys.EnsureAsync(vault);
        var keychain = new KeychainCredentialStore(Path.Combine(_dir, "credentials.json"), new MemorySecrets());

        var copied = await CredentialCopier.CopyAllAsync(vault, keychain);

        Assert.Contains(RecordingKeys.CredentialId, copied);
        Assert.Equal(publicKey, RecordingKeys.PublicKeyOf((await RecordingKeys.PrivateKeyAsync(keychain))!));
    }

    [Fact]
    public void A_file_key_opens_only_with_its_private_key()
    {
        var (privateKey, publicKey) = RecordingTestKeys.New();
        var (otherPrivate, _) = RecordingTestKeys.New();
        var fileKey = RecordingKeys.NewFileKey();

        var sealedKey = RecordingKeys.Seal(publicKey, fileKey);

        Assert.Equal(fileKey, RecordingKeys.Open(privateKey, sealedKey));
        Assert.Null(RecordingKeys.Open(otherPrivate, sealedKey));
        var tampered = sealedKey with { Ciphertext = sealedKey.Ciphertext.ToArray() };
        tampered.Ciphertext[0] ^= 1;
        Assert.Null(RecordingKeys.Open(privateKey, tampered));
        Assert.Null(RecordingKeys.Open(privateKey, sealedKey with { Nonce = new byte[3] }));
    }

    [Fact]
    public async Task A_damaged_saved_key_says_so()
    {
        var keychain = new KeychainCredentialStore(Path.Combine(_dir, "credentials.json"), new MemorySecrets());
        await keychain.SaveAsync(new Credential(RecordingKeys.CredentialId, "x", CredentialKind.Password, null, null, "not a key"));

        await Assert.ThrowsAsync<InvalidDataException>(() => RecordingKeys.PrivateKeyAsync(keychain));
    }
}
