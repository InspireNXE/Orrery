using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Export;
using Orrery.Core.Recording;
using Xunit;

namespace Orrery.Core.Tests.Security;

/// <summary>
/// Attacks on Orrery's own encryption: the vault, the sealed export and the recordings key. Each test plays someone who
/// can read and rewrite the files (a synced data folder, a stolen backup, a shared export) and checks they learn
/// nothing and can't get a changed file accepted. Tamper and header-limit cases already covered next to each format
/// aren't repeated here.
/// </summary>
public sealed class EncryptionAttackTests : IDisposable
{
    static readonly VaultFileStore.KdfSettings FastKdf = new(MemoryKiB: 8 * 1024, Iterations: 1);
    static readonly SealedExport.KdfSettings FastExportKdf = new(MemoryKiB: 8 * 1024, Iterations: 1);
    const string Password = "correct horse battery staple";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    public EncryptionAttackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    string PathOf(string name) => Path.Combine(_dir, name);

    static Credential Sample(string secret = "Sup3r-Secret!") =>
        new(Guid.NewGuid(), "corp-admin-name", CredentialKind.Password, "admin-user-name", "corp-domain-name", secret,
            Passphrase: "key-passphrase-text", Notes: "private-note-text");

    async Task<string> NewVaultAsync(string name, string password = Password, params Credential[] credentials)
    {
        using var store = new VaultFileStore(PathOf(name), FastKdf);
        await store.UnlockAsync(password);
        if (credentials.Length > 0)
        {
            await store.SaveAllAsync(credentials);
        }

        return PathOf(name);
    }

    // ---- The vault ----

    [Fact]
    public async Task Nothing_about_a_credential_is_readable_in_the_vault_file()
    {
        var path = await NewVaultAsync("vault.frv", Password, Sample());
        var text = await File.ReadAllTextAsync(path);
        foreach (var plain in new[] { "Sup3r-Secret!", "corp-admin-name", "admin-user-name", "corp-domain-name", "key-passphrase-text", "private-note-text", Password })
        {
            Assert.DoesNotContain(plain, text);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)), text);
        }
    }

    [Fact]
    public async Task A_wrapped_key_moved_from_another_vault_with_the_same_password_opens_nothing()
    {
        var mine = await NewVaultAsync("mine.frv", Password, Sample("mine-secret"));
        var theirs = await NewVaultAsync("theirs.frv", Password, Sample("theirs-secret"));

        // The attacker's key block (KDF settings, salt and wrapped key) over my contents: their key unwraps, but it
        // isn't my data key, and my contents are bound to my salt.
        var file = JsonNode.Parse(await File.ReadAllTextAsync(mine))!;
        var donor = JsonNode.Parse(await File.ReadAllTextAsync(theirs))!;
        file["kdf"] = donor["kdf"]!.DeepClone();
        file["wrappedKey"] = donor["wrappedKey"]!.DeepClone();
        await File.WriteAllTextAsync(mine, file.ToJsonString());

        using var store = new VaultFileStore(mine, FastKdf);
        await Assert.ThrowsAnyAsync<Exception>(() => store.UnlockAsync(Password));
        Assert.True(store.IsLocked);
    }

    [Fact]
    public async Task Contents_moved_from_another_vault_with_the_same_password_are_refused()
    {
        var mine = await NewVaultAsync("mine.frv", Password, Sample("mine-secret"));
        var theirs = await NewVaultAsync("theirs.frv", Password, Sample("planted-secret"));

        var file = JsonNode.Parse(await File.ReadAllTextAsync(mine))!;
        file["payload"] = JsonNode.Parse(await File.ReadAllTextAsync(theirs))!["payload"]!.DeepClone();
        await File.WriteAllTextAsync(mine, file.ToJsonString());

        using var store = new VaultFileStore(mine, FastKdf);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password));
        Assert.True(store.IsLocked);
    }

    [Fact]
    public async Task The_wrapped_key_and_the_contents_cant_stand_in_for_each_other()
    {
        var path = await NewVaultAsync("vault.frv", Password, Sample());
        var file = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        (file["wrappedKey"], file["payload"]) = (file["payload"]!.DeepClone(), file["wrappedKey"]!.DeepClone());
        await File.WriteAllTextAsync(path, file.ToJsonString());

        using var store = new VaultFileStore(path, FastKdf);
        await Assert.ThrowsAnyAsync<Exception>(() => store.UnlockAsync(Password));
        Assert.True(store.IsLocked);
    }

    [Theory]
    [InlineData("kdf")]
    [InlineData("key cipher")]
    [InlineData("payload cipher")]
    [InlineData("format")]
    [InlineData("version")]
    public async Task Naming_another_algorithm_or_format_is_refused(string field)
    {
        var path = await NewVaultAsync("vault.frv", Password, Sample());
        var file = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        switch (field)
        {
            case "kdf": file["kdf"]!["alg"] = "pbkdf2"; break;
            case "key cipher": file["wrappedKey"]!["alg"] = "none"; break;
            case "payload cipher": file["payload"]!["alg"] = "none"; break;
            case "format": file["format"] = "something-else"; break;
            case "version": file["version"] = 0; break;
        }

        await File.WriteAllTextAsync(path, file.ToJsonString());
        using var store = new VaultFileStore(path, FastKdf);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password));
        Assert.True(store.IsLocked);
    }

    [Fact]
    public async Task Any_changed_byte_in_the_contents_or_its_nonce_is_caught()
    {
        var path = await NewVaultAsync("vault.frv", Password, Sample());
        var original = await File.ReadAllTextAsync(path);
        var ciphertext = Convert.FromBase64String(JsonNode.Parse(original)!["payload"]!["ct"]!.GetValue<string>());

        // First and last bytes, the tag, and spots in between; plus each byte of the nonce.
        var places = new List<(string Field, int Index)>();
        foreach (int i in new[] { 0, 1, ciphertext.Length / 3, ciphertext.Length / 2, ciphertext.Length - 17, ciphertext.Length - 16, ciphertext.Length - 1 })
        {
            places.Add(("ct", i));
        }

        for (int i = 0; i < 24; i += 5)
        {
            places.Add(("nonce", i));
        }

        foreach (var (field, index) in places)
        {
            var file = JsonNode.Parse(original)!;
            var bytes = Convert.FromBase64String(file["payload"]![field]!.GetValue<string>());
            bytes[index] ^= 0x01;
            file["payload"]![field] = Convert.ToBase64String(bytes);
            await File.WriteAllTextAsync(path, file.ToJsonString());

            using var store = new VaultFileStore(path, FastKdf);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password));
            Assert.True(store.IsLocked, $"{field}[{index}] changed and the vault still opened");
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Contents_cut_short_or_extended_are_caught(int change)
    {
        var path = await NewVaultAsync("vault.frv", Password, Sample());
        var file = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var bytes = Convert.FromBase64String(file["payload"]!["ct"]!.GetValue<string>());
        var changed = change < 0 ? bytes[..^1] : [.. bytes, 0];
        file["payload"]!["ct"] = Convert.ToBase64String(changed);
        await File.WriteAllTextAsync(path, file.ToJsonString());

        using var store = new VaultFileStore(path, FastKdf);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password));
    }

    [Fact]
    public async Task Every_save_and_password_change_uses_a_fresh_nonce_and_salt()
    {
        var path = PathOf("vault.frv");
        var payloadNonces = new HashSet<string>();
        var keyNonces = new HashSet<string>();
        var salts = new HashSet<string>();
        var ciphertexts = new HashSet<string>();
        var credential = Sample();

        void Note()
        {
            var file = JsonNode.Parse(File.ReadAllText(path))!;
            payloadNonces.Add(file["payload"]!["nonce"]!.GetValue<string>());
            ciphertexts.Add(file["payload"]!["ct"]!.GetValue<string>());
            keyNonces.Add(file["wrappedKey"]!["nonce"]!.GetValue<string>());
            salts.Add(file["kdf"]!["salt"]!.GetValue<string>());
        }

        using var store = new VaultFileStore(path, FastKdf);
        await store.UnlockAsync(Password);
        Note();
        for (int i = 0; i < 50; i++)
        {
            // The same contents saved again must still look different.
            await store.SaveAsync(credential);
            Note();
        }

        Assert.Equal(51, payloadNonces.Count);
        Assert.Equal(51, ciphertexts.Count);

        string current = Password;
        for (int i = 0; i < 3; i++)
        {
            var next = $"{Password} {i}";
            await store.ChangePasswordAsync(current, next);
            current = next;
            Note();
        }

        Assert.Equal(4, keyNonces.Count);
        Assert.Equal(4, salts.Count);
    }

    [Fact]
    public async Task A_new_vault_uses_the_full_key_derivation_cost()
    {
        // Argon2id at 64 MiB, 3 passes: above OWASP's minimums (19 MiB × 2 passes, or 46 MiB × 1).
        var path = PathOf("vault.frv");
        using (var store = new VaultFileStore(path))
        {
            await store.UnlockAsync(Password);
        }

        var kdf = JsonNode.Parse(await File.ReadAllTextAsync(path))!["kdf"]!;
        Assert.Equal("argon2id", kdf["alg"]!.GetValue<string>());
        Assert.True(kdf["memoryKiB"]!.GetValue<int>() >= 64 * 1024);
        Assert.True(kdf["iterations"]!.GetValue<int>() >= 3);
        Assert.Equal(16, Convert.FromBase64String(kdf["salt"]!.GetValue<string>()).Length);
    }

    [Fact]
    public async Task Changing_the_password_on_a_cheap_vault_moves_it_to_the_full_cost()
    {
        // A vault made with a low cost (an early build, or the minimum a file may claim) shouldn't keep it forever.
        var path = await NewVaultAsync("vault.frv", Password, Sample());
        using (var store = new VaultFileStore(path))
        {
            await store.ChangePasswordAsync(Password, Password + " new");
        }

        var kdf = JsonNode.Parse(await File.ReadAllTextAsync(path))!["kdf"]!;
        Assert.Equal(VaultFileStore.KdfSettings.Default.MemoryKiB, kdf["memoryKiB"]!.GetValue<int>());
        Assert.Equal(VaultFileStore.KdfSettings.Default.Iterations, kdf["iterations"]!.GetValue<int>());
    }

    [Fact]
    public async Task Locking_on_idle_lets_go_of_every_decrypted_credential()
    {
        using var store = new VaultFileStore(PathOf("vault.frv"), FastKdf, autoLockAfter: TimeSpan.Zero);
        await store.UnlockAsync(Password);
        await store.SaveAsync(Sample());
        await Task.Delay(20);

        typeof(VaultFileStore).GetMethod("AutoLockIfIdle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, null);

        Assert.True(store.IsLocked);
        foreach (var field in new[] { "_items", "_knownItems", "_knownText" })
        {
            Assert.True(typeof(VaultFileStore).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store) is null,
                $"{field} still holds what the vault decrypted after it locked itself");
        }
    }

    [Fact]
    public async Task Locking_by_hand_lets_go_of_every_decrypted_credential()
    {
        using var store = new VaultFileStore(PathOf("vault.frv"), FastKdf);
        await store.UnlockAsync(Password);
        await store.SaveAsync(Sample());
        store.Lock();

        foreach (var field in new[] { "_items", "_knownItems", "_knownText" })
        {
            Assert.True(typeof(VaultFileStore).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store) is null,
                $"{field} still holds what the vault decrypted after it was locked");
        }
    }

    [Fact]
    public async Task Wrong_guesses_are_slowed_down()
    {
        var path = await NewVaultAsync("vault.frv");
        using var store = new VaultFileStore(path, FastKdf);
        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<WrongVaultPasswordException>(() => store.UnlockAsync("wrong " + i));
        }

        // The sixth wrong guess waits a second before it's even tried.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<WrongVaultPasswordException>(() => store.UnlockAsync("wrong 6"));
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(900), $"Took only {timer.ElapsedMilliseconds} ms");
    }

    // ---- Rollback: an older copy of the vault put back ----

    /// <summary>One computer's keychain.</summary>
    sealed class MemoryKeychain : ISecretBackend
    {
        public readonly Dictionary<string, byte[]> Items = [];
        public string Name => "test keychain";
        public int? MaxSecretSize => null;
        public void Write(string key, byte[] secret) => Items[key] = secret;
        public byte[]? Read(string key) => Items.GetValueOrDefault(key);
        public void Delete(string key) => Items.Remove(key);
    }

    [Fact]
    public async Task An_older_copy_put_back_is_refused_and_can_be_opened_when_asked()
    {
        var keychain = new MemoryKeychain();
        var path = PathOf("vault.frv");
        var kept = Sample("kept");
        string older;
        using (var store = new VaultFileStore(path, FastKdf, seenSaves: keychain))
        {
            await store.UnlockAsync(Password);
            await store.SaveAsync(kept);
            older = await File.ReadAllTextAsync(path);
            await store.SaveAsync(Sample("added later"));
            await store.DeleteAsync(kept.Id);
        }

        await File.WriteAllTextAsync(path, older);
        using var reopened = new VaultFileStore(path, FastKdf, seenSaves: keychain);
        var refused = await Assert.ThrowsAsync<VaultRollbackException>(() => reopened.UnlockAsync(Password));
        Assert.True(refused.FileSave < refused.SeenSave);
        Assert.True(reopened.IsLocked);

        // A rollback isn't a wrong guess.
        await Assert.ThrowsAsync<VaultRollbackException>(() => reopened.UnlockAsync(Password));

        reopened.AllowOlderCopy = true;
        await reopened.UnlockAsync(Password);
        Assert.NotNull(await reopened.GetAsync(kept.Id));

        // Saving the older copy counts on past what was seen, so it isn't refused again afterwards.
        await reopened.SaveAsync(Sample("after restore"));
        reopened.Lock();
        await reopened.UnlockAsync(Password);
        Assert.False(reopened.AllowOlderCopy);
    }

    [Fact]
    public async Task Another_computer_that_never_saw_the_newer_copy_opens_it()
    {
        var path = PathOf("vault.frv");
        string older;
        using (var store = new VaultFileStore(path, FastKdf, seenSaves: new MemoryKeychain()))
        {
            await store.UnlockAsync(Password);
            older = await File.ReadAllTextAsync(path);
            await store.SaveAsync(Sample());
        }

        await File.WriteAllTextAsync(path, older);
        using var elsewhere = new VaultFileStore(path, FastKdf, seenSaves: new MemoryKeychain());
        await elsewhere.UnlockAsync(Password);
    }

    [Fact]
    public async Task Stripping_or_moving_the_save_count_doesnt_get_an_older_copy_in()
    {
        var keychain = new MemoryKeychain();
        var path = PathOf("vault.frv");
        string older;
        using (var store = new VaultFileStore(path, FastKdf, seenSaves: keychain))
        {
            await store.UnlockAsync(Password);
            older = await File.ReadAllTextAsync(path);
            await store.SaveAsync(Sample());
            await store.SaveAsync(Sample());
        }

        var newer = JsonNode.Parse(await File.ReadAllTextAsync(path))!;

        // Without a count it's save 0.
        var stripped = JsonNode.Parse(older)!;
        stripped.AsObject().Remove("saves");
        await File.WriteAllTextAsync(path, stripped.ToJsonString());
        using (var store = new VaultFileStore(path, FastKdf, seenSaves: keychain))
        {
            await Assert.ThrowsAsync<VaultRollbackException>(() => store.UnlockAsync(Password));
        }

        // The newer file's count on the older contents doesn't check out.
        var moved = JsonNode.Parse(older)!;
        moved["saves"] = newer["saves"]!.DeepClone();
        await File.WriteAllTextAsync(path, moved.ToJsonString());
        using (var store = new VaultFileStore(path, FastKdf, seenSaves: keychain))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password));
            Assert.True(store.IsLocked);
        }

        // A raised count without its tag doesn't either.
        var raised = JsonNode.Parse(older)!;
        raised["saves"]!["count"] = 1000;
        await File.WriteAllTextAsync(path, raised.ToJsonString());
        using (var store = new VaultFileStore(path, FastKdf, seenSaves: keychain))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => store.UnlockAsync(Password));
        }
    }

    [Fact]
    public async Task An_older_copy_synced_in_while_open_isnt_taken_in_and_the_next_save_replaces_it()
    {
        var keychain = new MemoryKeychain();
        var path = PathOf("vault.frv");
        using var store = new VaultFileStore(path, FastKdf, seenSaves: keychain);
        await store.UnlockAsync(Password);
        var old = Sample("old");
        await store.SaveAsync(old);
        var older = await File.ReadAllTextAsync(path);
        await store.SaveAsync(old with { Secret = "changed" });
        var added = Sample("added");
        await store.SaveAsync(added);

        await File.WriteAllTextAsync(path, older);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal("changed", (await store.GetAsync(old.Id))!.Secret);
        Assert.NotNull(await store.GetAsync(added.Id));

        await store.SaveAsync(Sample("next"));
        store.Lock();
        await store.UnlockAsync(Password);
        Assert.Equal("changed", (await store.GetAsync(old.Id))!.Secret);
        Assert.NotNull(await store.GetAsync(added.Id));
    }

    [Fact]
    public async Task A_copy_from_before_a_password_change_is_still_caught_with_the_old_password()
    {
        var keychain = new MemoryKeychain();
        var path = PathOf("vault.frv");
        string older;
        using (var store = new VaultFileStore(path, FastKdf, seenSaves: keychain))
        {
            await store.UnlockAsync(Password);
            older = await File.ReadAllTextAsync(path);
            await store.SaveAsync(Sample());
            await store.ChangePasswordAsync(Password, Password + " new");
            await store.SaveAsync(Sample());
        }

        await File.WriteAllTextAsync(path, older);
        using var reopened = new VaultFileStore(path, FastKdf, seenSaves: keychain);
        await Assert.ThrowsAsync<VaultRollbackException>(() => reopened.UnlockAsync(Password));
    }

    [Fact]
    public async Task The_keychain_holds_only_a_count_and_a_name_that_says_nothing()
    {
        var keychain = new MemoryKeychain();
        using var store = new VaultFileStore(PathOf("vault.frv"), FastKdf, seenSaves: keychain);
        await store.UnlockAsync(Password);
        await store.SaveAsync(Sample());

        var (name, value) = Assert.Single(keychain.Items);
        Assert.StartsWith("orrery:vault-seen:", name);
        Assert.Matches("^[0-9]+$", Encoding.ASCII.GetString(value));
    }

    // ---- The sealed export ----

    static SealedExport.SealedFile Parse(string text) => JsonSerializer.Deserialize<SealedExport.SealedFile>(text, ConnectionStore.JsonOptions)!;

    [Fact]
    public void A_sealed_export_holds_nothing_readable_and_differs_every_time()
    {
        var plain = Encoding.UTF8.GetBytes("{\"host\":\"db01.corp.example\",\"secret\":\"Sup3r-Secret!\"}");
        var first = SealedExport.Seal(plain, Password, FastExportKdf);
        var second = SealedExport.Seal(plain, Password, FastExportKdf);

        foreach (var text in new[] { first, second })
        {
            Assert.DoesNotContain("db01", text);
            Assert.DoesNotContain("Sup3r-Secret!", text);
            Assert.DoesNotContain(Password, text);
        }

        Assert.NotEqual(Parse(first).Kdf.Salt, Parse(second).Kdf.Salt);
        Assert.NotEqual(Parse(first).Payload.Nonce, Parse(second).Payload.Nonce);
        Assert.Equal(plain, SealedExport.Open(Parse(first), Password));
    }

    [Fact]
    public void Contents_moved_between_two_exports_with_the_same_password_dont_open()
    {
        var first = Parse(SealedExport.Seal("{\"a\":1}"u8.ToArray(), Password, FastExportKdf));
        var second = Parse(SealedExport.Seal("{\"planted\":1}"u8.ToArray(), Password, FastExportKdf));

        Assert.Null(SealedExport.Open(first with { Payload = second.Payload }, Password));
    }

    [Fact]
    public void Any_changed_byte_in_a_sealed_export_is_caught()
    {
        var file = Parse(SealedExport.Seal(Encoding.UTF8.GetBytes(new string('x', 200)), Password, FastExportKdf));
        var ciphertext = Convert.FromBase64String(file.Payload.Ciphertext);
        foreach (int i in new[] { 0, ciphertext.Length / 2, ciphertext.Length - 16, ciphertext.Length - 1 })
        {
            var changed = (byte[])ciphertext.Clone();
            changed[i] ^= 0x80;
            Assert.Null(SealedExport.Open(file with { Payload = file.Payload with { Ciphertext = Convert.ToBase64String(changed) } }, Password));
        }

        var nonce = Convert.FromBase64String(file.Payload.Nonce);
        nonce[0] ^= 0x01;
        Assert.Null(SealedExport.Open(file with { Payload = file.Payload with { Nonce = Convert.ToBase64String(nonce) } }, Password));

        var salt = Convert.FromBase64String(file.Kdf.Salt);
        salt[0] ^= 0x01;
        Assert.Null(SealedExport.Open(file with { Kdf = file.Kdf with { Salt = Convert.ToBase64String(salt) } }, Password));
    }

    [Theory]
    [InlineData("alg")]
    [InlineData("cipher")]
    [InlineData("parallelism")]
    [InlineData("memory")]
    [InlineData("iterations")]
    public void A_sealed_export_asking_for_another_scheme_or_a_huge_cost_is_refused(string field)
    {
        var file = Parse(SealedExport.Seal("{}"u8.ToArray(), Password, FastExportKdf));
        var changed = field switch
        {
            "alg" => file with { Kdf = file.Kdf with { Alg = "pbkdf2" } },
            "cipher" => file with { Payload = file.Payload with { Alg = "none" } },
            "parallelism" => file with { Kdf = file.Kdf with { Parallelism = 64 } },
            "memory" => file with { Kdf = file.Kdf with { MemoryKiB = int.MaxValue } },
            "iterations" => file with { Kdf = file.Kdf with { Iterations = int.MaxValue } },
            _ => throw new ArgumentException(field),
        };

        Assert.Throws<InvalidDataException>(() => SealedExport.Open(changed, Password));
    }

    // ---- The recordings key ----

    // X25519 public keys of small order (and ones that reduce to them): an agreement with any of them gives a shared
    // secret an attacker knows, so it must be refused rather than used.
    public static TheoryData<string> SmallOrderPoints => new()
    {
        "0000000000000000000000000000000000000000000000000000000000000000",
        "0100000000000000000000000000000000000000000000000000000000000000",
        "e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800",
        "5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157",
        "ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
        "edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
        "eeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
    };

    static byte[] NewPrivateKey()
    {
        using var key = NSec.Cryptography.Key.Create(NSec.Cryptography.KeyAgreementAlgorithm.X25519,
            new NSec.Cryptography.KeyCreationParameters { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport });
        return key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey);
    }

    [Theory]
    [MemberData(nameof(SmallOrderPoints))]
    public void A_small_order_ephemeral_key_in_a_recording_opens_nothing(string point)
    {
        var privateKey = NewPrivateKey();
        var sealedKey = RecordingKeys.Seal(RecordingKeys.PublicKeyOf(privateKey), RecordingKeys.NewFileKey());

        Assert.Null(RecordingKeys.Open(privateKey, sealedKey with { EphemeralPublicKey = Convert.FromHexString(point) }));
    }

    [Theory]
    [MemberData(nameof(SmallOrderPoints))]
    public void Recordings_are_never_sealed_to_a_small_order_public_key(string point)
    {
        Assert.Throws<InvalidDataException>(() => RecordingKeys.Seal(Convert.FromHexString(point), RecordingKeys.NewFileKey()));
    }

    [Fact]
    public void A_sealed_file_key_with_any_part_changed_opens_nothing()
    {
        var privateKey = NewPrivateKey();
        var fileKey = RecordingKeys.NewFileKey();
        var sealedKey = RecordingKeys.Seal(RecordingKeys.PublicKeyOf(privateKey), fileKey);
        Assert.Equal(fileKey, RecordingKeys.Open(privateKey, sealedKey));

        static byte[] Flip(byte[] bytes, int at)
        {
            var copy = (byte[])bytes.Clone();
            copy[at] ^= 0x01;
            return copy;
        }

        var other = RecordingKeys.Seal(RecordingKeys.PublicKeyOf(privateKey), RecordingKeys.NewFileKey());
        Assert.Null(RecordingKeys.Open(privateKey, sealedKey with { EphemeralPublicKey = Flip(sealedKey.EphemeralPublicKey, 0) }));
        Assert.Null(RecordingKeys.Open(privateKey, sealedKey with { EphemeralPublicKey = other.EphemeralPublicKey }));
        Assert.Null(RecordingKeys.Open(privateKey, sealedKey with { Nonce = Flip(sealedKey.Nonce, 3) }));
        Assert.Null(RecordingKeys.Open(privateKey, sealedKey with { Ciphertext = Flip(sealedKey.Ciphertext, 0) }));
        Assert.Null(RecordingKeys.Open(privateKey, sealedKey with { Ciphertext = Flip(sealedKey.Ciphertext, sealedKey.Ciphertext.Length - 1) }));
        Assert.Null(RecordingKeys.Open(privateKey, sealedKey with { Ciphertext = sealedKey.Ciphertext[..^1] }));
        Assert.Null(RecordingKeys.Open(NewPrivateKey(), sealedKey));
    }

    [Fact]
    public void Each_seal_uses_a_fresh_ephemeral_key_and_nonce()
    {
        var publicKey = RecordingKeys.PublicKeyOf(NewPrivateKey());
        var fileKey = RecordingKeys.NewFileKey();
        var seals = Enumerable.Range(0, 20).Select(_ => RecordingKeys.Seal(publicKey, fileKey)).ToList();

        Assert.Equal(20, seals.Select(s => Convert.ToHexString(s.EphemeralPublicKey)).Distinct().Count());
        Assert.Equal(20, seals.Select(s => Convert.ToHexString(s.Nonce)).Distinct().Count());
        Assert.Equal(20, seals.Select(s => Convert.ToHexString(s.Ciphertext)).Distinct().Count());
    }
}
