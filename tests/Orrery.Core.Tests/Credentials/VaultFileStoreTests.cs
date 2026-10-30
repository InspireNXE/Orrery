using System.Text;
using System.Text.Json.Nodes;
using NSec.Cryptography;
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

    [Theory]
    [InlineData("truncated")]
    [InlineData("not json")]
    [InlineData("wrong type")]
    [InlineData("kdf missing")]
    [InlineData("kdf null")]
    [InlineData("wrappedKey missing")]
    [InlineData("payload missing")]
    [InlineData("salt not base64")]
    [InlineData("salt wrong length")]
    [InlineData("salt missing")]
    [InlineData("key nonce wrong length")]
    [InlineData("key nonce not base64")]
    [InlineData("payload nonce wrong length")]
    [InlineData("key ciphertext not base64")]
    [InlineData("key ciphertext too short")]
    [InlineData("payload ciphertext not base64")]
    [InlineData("iterations 0")]
    [InlineData("iterations 1000000")]
    [InlineData("iterations max")]
    [InlineData("parallelism 2")]
    [InlineData("parallelism 0")]
    [InlineData("memory 7")]
    [InlineData("memory max")]
    public async Task A_damaged_header_is_rejected_as_damaged_without_a_long_derivation(string damage)
    {
        using (var store = NewStore())
        {
            await store.UnlockAsync(Password);
            await store.SaveAsync(Sample());
        }

        var text = await File.ReadAllTextAsync(VaultPath);
        if (damage == "truncated")
        {
            text = text[..(text.Length / 2)];
        }
        else if (damage == "not json")
        {
            text = "this is not json";
        }
        else
        {
            var json = JsonNode.Parse(text)!;
            switch (damage)
            {
                case "wrong type": json["version"] = "one"; break;
                case "kdf missing": json.AsObject().Remove("kdf"); break;
                case "kdf null": json["kdf"] = null; break;
                case "wrappedKey missing": json.AsObject().Remove("wrappedKey"); break;
                case "payload missing": json.AsObject().Remove("payload"); break;
                case "salt not base64": json["kdf"]!["salt"] = "!!! not base64 !!!"; break;
                case "salt wrong length": json["kdf"]!["salt"] = Convert.ToBase64String(new byte[8]); break;
                case "salt missing": json["kdf"]!.AsObject().Remove("salt"); break;
                case "key nonce wrong length": json["wrappedKey"]!["nonce"] = Convert.ToBase64String(new byte[8]); break;
                case "key nonce not base64": json["wrappedKey"]!["nonce"] = "!!!"; break;
                case "payload nonce wrong length": json["payload"]!["nonce"] = Convert.ToBase64String(new byte[8]); break;
                case "key ciphertext not base64": json["wrappedKey"]!["ct"] = "!!!"; break;
                case "key ciphertext too short": json["wrappedKey"]!["ct"] = Convert.ToBase64String(new byte[4]); break;
                case "payload ciphertext not base64": json["payload"]!["ct"] = "!!!"; break;
                case "iterations 0": json["kdf"]!["iterations"] = 0; break;
                case "iterations 1000000": json["kdf"]!["iterations"] = 1_000_000; break;
                case "iterations max": json["kdf"]!["iterations"] = int.MaxValue; break;
                case "parallelism 2": json["kdf"]!["parallelism"] = 2; break;
                case "parallelism 0": json["kdf"]!["parallelism"] = 0; break;
                case "memory 7": json["kdf"]!["memoryKiB"] = 7; break;
                case "memory max": json["kdf"]!["memoryKiB"] = int.MaxValue; break;
                default: throw new ArgumentException(damage);
            }

            text = json.ToJsonString();
        }

        await File.WriteAllTextAsync(VaultPath, text);

        using var reopened = NewStore();
        var unlock = reopened.UnlockAsync(Password);
        if (await Task.WhenAny(unlock, Task.Delay(TimeSpan.FromSeconds(5))) != unlock)
        {
            Assert.Fail("Unlocking a damaged vault started a long key derivation instead of rejecting the header.");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => unlock);
        Assert.True(reopened.IsLocked);
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
    public async Task A_batch_is_saved_together_and_reads_back()
    {
        var batch = Enumerable.Range(1, 5).Select(i => Sample($"imported {i}", $"secret-{i}")).ToList();
        using (var store = NewStore())
        {
            await store.UnlockAsync(Password);
            await store.SaveAllAsync(batch);
        }

        using var reopened = NewStore();
        await reopened.UnlockAsync(Password);
        Assert.Equal(5, (await reopened.ListAsync()).Count);
        foreach (var credential in batch)
        {
            Assert.Equal(credential, await reopened.GetAsync(credential.Id));
        }
    }

    [Fact]
    public async Task A_batch_that_cannot_be_written_leaves_the_vault_as_it_was()
    {
        using var store = NewStore();
        await store.UnlockAsync(Password);
        var earlier = Sample("earlier");
        await store.SaveAsync(earlier);

        // A folder where the vault file should be makes the write fail.
        File.Delete(VaultPath);
        Directory.CreateDirectory(VaultPath);
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAllAsync([Sample("one"), Sample("two")]));

        Assert.Equal(earlier.Id, Assert.Single(await store.ListAsync()).Id);
    }

    [Fact]
    public async Task A_batch_into_a_locked_vault_is_refused()
    {
        using var store = NewStore();
        await store.UnlockAsync(Password);
        store.Lock();

        await Assert.ThrowsAsync<VaultLockedException>(() => store.SaveAllAsync([Sample()]));
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

    [Fact]
    public async Task An_old_copy_and_the_old_password_cannot_read_what_is_saved_after_a_password_change()
    {
        using var store = NewStore();
        await store.UnlockAsync("first password");
        await store.SaveAsync(Sample("before"));
        var oldCopy = JsonNode.Parse(await File.ReadAllTextAsync(VaultPath))!;

        await store.ChangePasswordAsync("first password", "second password");
        await store.SaveAsync(Sample("after", "pw-ADDED-AFTER-CHANGE"));
        var current = JsonNode.Parse(await File.ReadAllTextAsync(VaultPath))!;

        // The old copy still opens with the old password; that can't be helped.
        using var oldDataKey = VaultFormat.UnwrapDataKey(oldCopy, "first password");
        Assert.NotNull(VaultFormat.DecryptPayload(oldCopy, oldDataKey));

        // But its data key must not open anything written since, whichever salt the payload is bound to.
        Assert.Null(VaultFormat.DecryptPayload(current, oldDataKey));
        Assert.Null(VaultFormat.DecryptPayload(current, oldDataKey, kdfFrom: oldCopy));
    }

    [Fact]
    public async Task A_password_change_that_cannot_be_written_leaves_the_old_password_and_credentials_working()
    {
        using var store = NewStore();
        await store.UnlockAsync("first password");
        var earlier = Sample("earlier");
        await store.SaveAsync(earlier);

        // A folder where the vault file should be makes the write fail.
        File.Delete(VaultPath);
        Directory.CreateDirectory(VaultPath);
        await Assert.ThrowsAnyAsync<Exception>(() => store.ChangePasswordAsync("first password", "second password"));
        Directory.Delete(VaultPath);

        // The open store still saves under the old password and data key.
        Assert.False(store.IsLocked);
        Assert.Equal(earlier, await store.GetAsync(earlier.Id));
        var later = Sample("later");
        await store.SaveAsync(later);

        using var reopened = NewStore();
        await Assert.ThrowsAsync<WrongVaultPasswordException>(() => reopened.UnlockAsync("second password"));
        await reopened.UnlockAsync("first password");
        Assert.Equal(earlier, await reopened.GetAsync(earlier.Id));
        Assert.Equal(later, await reopened.GetAsync(later.Id));
    }

    /// <summary>Reads a vault file by its format (design doc §3.3), without the store, as an attacker with a copy would.</summary>
    static class VaultFormat
    {
        static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;

        public static Key UnwrapDataKey(JsonNode file, string password)
        {
            var kdf = file["kdf"]!;
            var parameters = new Argon2Parameters
            {
                DegreeOfParallelism = kdf["parallelism"]!.GetValue<int>(),
                MemorySize = kdf["memoryKiB"]!.GetValue<int>(),
                NumberOfPasses = kdf["iterations"]!.GetValue<int>(),
            };
            using var kek = PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters).DeriveKey(
                password, Convert.FromBase64String(kdf["salt"]!.GetValue<string>()), Aead);
            var dataKeyBytes = Decrypt(kek, file["wrappedKey"]!, AssociatedData(kdf, "key"));
            Assert.NotNull(dataKeyBytes);
            return Key.Import(Aead, dataKeyBytes, KeyBlobFormat.RawSymmetricKey);
        }

        public static byte[]? DecryptPayload(JsonNode file, Key dataKey, JsonNode? kdfFrom = null) =>
            Decrypt(dataKey, file["payload"]!, AssociatedData((kdfFrom ?? file)["kdf"]!, "payload"));

        static byte[]? Decrypt(Key key, JsonNode block, byte[] associatedData) =>
            Aead.Decrypt(key, Convert.FromBase64String(block["nonce"]!.GetValue<string>()), associatedData,
                Convert.FromBase64String(block["ct"]!.GetValue<string>()));

        static byte[] AssociatedData(JsonNode kdf, string part) => Encoding.UTF8.GetBytes(
            $"orrery-vault|v1|{kdf["alg"]}|m={kdf["memoryKiB"]}|t={kdf["iterations"]}|p={kdf["parallelism"]}|salt={kdf["salt"]}|{part}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
