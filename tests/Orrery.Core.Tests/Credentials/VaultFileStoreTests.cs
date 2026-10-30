using System.Text.Json.Nodes;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Credentials;

public class VaultFileStoreTests : IDisposable
{
    // Cheap settings keep the tests fast; real vaults use KdfSettings.Default.
    static readonly VaultFileStore.KdfSettings FastKdf = new(MemoryKiB: 8 * 1024, Iterations: 1);
    const string Password = "correct horse battery staple";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    string VaultPath => Path.Combine(_dir, "vault.frv");

    VaultFileStore NewStore() => new(VaultPath, FastKdf);

    static Credential Sample(string name = "corp admin", string secret = "Sup3r-Secret!") =>
        new(Guid.NewGuid(), name, CredentialKind.Password, "admin", "corp", secret);

    [Fact]
    public async Task First_unlock_creates_the_vault_and_later_opens_read_it_back()
    {
        var credential = Sample();
        using (var store = NewStore())
        {
            Assert.False(store.Exists);
            await store.UnlockAsync(Password);
            await store.SaveAsync(credential);
        }

        using var reopened = NewStore();
        Assert.True(reopened.Exists);
        Assert.True(reopened.IsLocked);
        await reopened.UnlockAsync(Password);

        Assert.Equal(credential, await reopened.GetAsync(credential.Id));
        Assert.Equal("corp admin", Assert.Single(await reopened.ListAsync()).Name);
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_leaves_the_store_locked()
    {
        using (var store = NewStore())
        {
            await store.UnlockAsync(Password);
            await store.SaveAsync(Sample());
        }

        using var reopened = NewStore();
        await Assert.ThrowsAsync<WrongVaultPasswordException>(() => reopened.UnlockAsync("not it"));
        Assert.True(reopened.IsLocked);
    }

    [Fact]
    public async Task Secrets_never_appear_in_the_file()
    {
        using var store = NewStore();
        await store.UnlockAsync(Password);
        await store.SaveAsync(Sample(name: "visible-name", secret: "Sup3r-Secret!"));

        var text = await File.ReadAllTextAsync(VaultPath);
        Assert.DoesNotContain("Sup3r-Secret!", text);
        Assert.DoesNotContain("visible-name", text);
        Assert.DoesNotContain(Password, text);
    }

    [Fact]
    public async Task Locked_store_refuses_reads_and_writes()
    {
        using var store = NewStore();
        await store.UnlockAsync(Password);
        store.Lock();

        await Assert.ThrowsAsync<VaultLockedException>(() => store.ListAsync());
        await Assert.ThrowsAsync<VaultLockedException>(() => store.SaveAsync(Sample()));
    }

    [Fact]
    public async Task Lowering_the_kdf_cost_in_the_file_breaks_decryption()
    {
        using (var store = NewStore())
        {
            await store.UnlockAsync(Password);
        }

        var json = JsonNode.Parse(await File.ReadAllTextAsync(VaultPath))!;
        json["kdf"]!["iterations"] = 2;
        await File.WriteAllTextAsync(VaultPath, json.ToJsonString());

        using var reopened = NewStore();
        await Assert.ThrowsAsync<WrongVaultPasswordException>(() => reopened.UnlockAsync(Password));
    }

    [Fact]
    public async Task Tampered_payload_is_reported_as_damaged()
    {
        using (var store = NewStore())
        {
            await store.UnlockAsync(Password);
            await store.SaveAsync(Sample());
        }

        var json = JsonNode.Parse(await File.ReadAllTextAsync(VaultPath))!;
        var ct = Convert.FromBase64String(json["payload"]!["ct"]!.GetValue<string>());
        ct[0] ^= 0xFF;
        json["payload"]!["ct"] = Convert.ToBase64String(ct);
        await File.WriteAllTextAsync(VaultPath, json.ToJsonString());

        using var reopened = NewStore();
        await Assert.ThrowsAsync<InvalidDataException>(() => reopened.UnlockAsync(Password));
    }

    [Fact]
    public async Task Delete_removes_the_credential()
    {
        var credential = Sample();
        using var store = NewStore();
        await store.UnlockAsync(Password);
        await store.SaveAsync(credential);
        await store.DeleteAsync(credential.Id);

        Assert.Null(await store.GetAsync(credential.Id));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public void Credential_ToString_hides_the_secret()
    {
        Assert.DoesNotContain("Sup3r-Secret!", Sample().ToString());
    }

    [Fact]
    public async Task Changing_the_master_password_keeps_the_credentials()
    {
        var credential = new Credential(Guid.NewGuid(), "admin", CredentialKind.Password, "root", null, "dummy-secret");
        using (var store = NewStore())
        {
            await store.UnlockAsync("first password");
            await store.SaveAsync(credential);
            await Assert.ThrowsAsync<WrongVaultPasswordException>(() => store.ChangePasswordAsync("not it", "second password"));
            await store.ChangePasswordAsync("first password", "second password");
            Assert.Equal(credential, await store.GetAsync(credential.Id));
        }

        using var reopened = NewStore();
        await Assert.ThrowsAsync<WrongVaultPasswordException>(() => reopened.UnlockAsync("first password"));
        await reopened.UnlockAsync("second password");
        Assert.Equal(credential, await reopened.GetAsync(credential.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
