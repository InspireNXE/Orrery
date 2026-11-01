using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Orrery.Core.Credentials;
using Orrery.Core.Storage;
using Xunit;

namespace Orrery.Core.Tests.Credentials;

/// <summary>
/// Stands in for Windows Hello or Touch ID: each key is a random secret that only "the authenticator" holds, and the
/// released secret is an HMAC of the challenge under it, so the same key and challenge always give the same answer.
/// Never touches the real Windows Hello.
/// </summary>
public sealed class FakeAuthenticator : IPlatformAuthenticator
{
    readonly Dictionary<string, byte[]> _keys = [];

    public string Name => "Test Hello";

    public string Id => "test-hello";

    public string? Unavailable { get; set; }

    /// <summary>What the next prompt answers instead of succeeding; null to succeed.</summary>
    public AuthenticatorStatus? NextAnswer { get; set; }

    /// <summary>Whether a new key is shown to be held in hardware.</summary>
    public bool InHardware { get; set; } = true;

    /// <summary>How many bytes of each secret to hand out; null for all 32.</summary>
    public int? SecretLength { get; set; }

    /// <summary>
    /// While set, a prompt stays up until this is completed (or, when <see cref="HonoursCancel"/>, cancelled), as a
    /// person deciding would.
    /// </summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Whether a held prompt gives up when cancelled. False is an authenticator that answers anyway.</summary>
    public bool HonoursCancel { get; set; } = true;

    public int Prompts { get; private set; }

    public bool SawCancel { get; private set; }

    public List<string> Deleted { get; } = [];

    public IReadOnlyCollection<string> KeyNames => _keys.Keys;

    /// <summary>Last secret handed out, to check it never reaches a file or log.</summary>
    public byte[]? LastSecret { get; private set; }

    public Task<string?> CheckAvailableAsync() => Task.FromResult(Unavailable);

    public async Task<AuthenticatorResult> CreateAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel)
    {
        if (await Answer(cancel) is { } answer)
        {
            return answer;
        }

        _keys[keyName] = RandomNumberGenerator.GetBytes(32);
        return Sign(keyName, challenge) with { HardwareBacked = InHardware };
    }

    public async Task<AuthenticatorResult> ReleaseAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel)
    {
        if (await Answer(cancel) is { } answer)
        {
            return answer;
        }

        return _keys.ContainsKey(keyName) ? Sign(keyName, challenge) : new AuthenticatorResult(AuthenticatorStatus.KeyGone, Message: "The test key is gone.");
    }

    public Task DeleteAsync(string keyName)
    {
        _keys.Remove(keyName);
        Deleted.Add(keyName);
        return Task.CompletedTask;
    }

    /// <summary>The secret for a key and challenge, as a prompt the user passed would give it.</summary>
    public byte[] SecretFor(string keyName, byte[] challenge) => HMACSHA256.HashData(_keys[keyName], challenge);

    /// <summary>As if the platform replaced the key behind Orrery's back: same name, different secret.</summary>
    public void ReplaceKey(string keyName) => _keys[keyName] = RandomNumberGenerator.GetBytes(32);

    /// <summary>As if Windows Hello were reset, or couldn't find its keys this time.</summary>
    public void LoseKeys() => _keys.Clear();

    async Task<AuthenticatorResult?> Answer(CancellationToken cancel)
    {
        Prompts++;
        if (Hold is { } hold)
        {
            using (cancel.Register(() => SawCancel = true))
            {
                if (HonoursCancel)
                {
                    try
                    {
                        await hold.Task.WaitAsync(cancel);
                    }
                    catch (OperationCanceledException)
                    {
                        SawCancel = true;
                        return new AuthenticatorResult(AuthenticatorStatus.Cancelled);
                    }
                }
                else
                {
                    await hold.Task;
                }
            }
        }

        if (NextAnswer is not { } status)
        {
            return null;
        }

        NextAnswer = null;
        return new AuthenticatorResult(status, Message: $"Test answer {status}.");
    }

    AuthenticatorResult Sign(string keyName, byte[] challenge)
    {
        LastSecret = SecretFor(keyName, challenge)[..(SecretLength ?? 32)];
        return AuthenticatorResult.Released(LastSecret.ToArray());
    }
}

/// <summary>An OS keychain kept in memory.</summary>
public sealed class MemorySecrets : ISecretBackend
{
    public Dictionary<string, byte[]> Items { get; } = [];

    public string Name => "test keychain";

    public int? MaxSecretSize => 2560;

    public void Write(string key, byte[] secret)
    {
        Assert.True(secret.Length <= MaxSecretSize);
        Items[key] = secret.ToArray();
    }

    public byte[]? Read(string key) => Items.TryGetValue(key, out var secret) ? secret.ToArray() : null;

    public void Delete(string key) => Items.Remove(key);

    public IReadOnlyList<string> List(string prefix) => Items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    /// <summary>The biometric unlock records, without the list of keys waiting to be deleted.</summary>
    public List<KeyValuePair<string, byte[]>> Records => Items.Where(i => i.Key.StartsWith(BiometricUnlock.RecordPrefix, StringComparison.Ordinal)).ToList();
}

public sealed class FakeRemoteSession : IRemoteSession
{
    public bool IsRemote { get; set; }
}

public class BiometricUnlockTests : IDisposable
{
    static readonly VaultFileStore.KdfSettings FastKdf = new(MemoryKiB: 8 * 1024, Iterations: 1);
    const string Password = "correct horse battery staple";
    const string NewPassword = "a brand new password";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly FakeAuthenticator _hello = new();
    readonly MemorySecrets _records = new();
    readonly FakeRemoteSession _remote = new();
    readonly Credential _saved = new(Guid.NewGuid(), "corp admin", CredentialKind.Password, "admin", "corp", "Sup3r-Secret!");

    BiometricUnlock Biometric => new(_hello, _records, _remote);

    string VaultPath => Path.Combine(_dir, "vault.frv");

    async Task<VaultFileStore> NewVaultAsync(string? path = null)
    {
        var vault = new VaultFileStore(path ?? VaultPath, FastKdf);
        await vault.UnlockAsync(Password);
        await vault.SaveAsync(_saved);
        vault.Lock();
        return vault;
    }

    [Fact]
    public async Task Turned_on_with_the_master_password_it_unlocks_the_vault_without_it()
    {
        using var vault = await NewVaultAsync();
        var biometric = Biometric;
        Assert.False(biometric.IsOn(vault));

        Assert.Equal(BiometricOutcome.Done, (await biometric.TurnOnAsync(vault, Password)).Outcome);
        Assert.True(biometric.IsOn(vault));
        Assert.True(vault.IsLocked);

        using var reopened = new VaultFileStore(VaultPath, FastKdf);
        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(reopened)).Outcome);
        Assert.False(reopened.IsLocked);
        Assert.Equal(_saved, await reopened.GetAsync(_saved.Id));

        // It saves as usual afterwards, and the next biometric unlock still works.
        await reopened.SaveAsync(_saved with { Name = "renamed" });
        reopened.Lock();
        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(reopened)).Outcome);
        Assert.Equal("renamed", (await reopened.GetAsync(_saved.Id))!.Name);
    }

    [Fact]
    public async Task The_master_password_still_opens_the_vault()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);

        await vault.UnlockAsync(Password);
        Assert.Equal(_saved, await vault.GetAsync(_saved.Id));
    }

    [Fact]
    public async Task A_wrong_master_password_turns_nothing_on_and_shows_no_prompt()
    {
        using var vault = await NewVaultAsync();

        await Assert.ThrowsAsync<WrongVaultPasswordException>(() => Biometric.TurnOnAsync(vault, "not it"));

        Assert.False(Biometric.IsOn(vault));
        Assert.Empty(_records.Items);
        Assert.Equal(0, _hello.Prompts);
    }

    [Fact]
    public async Task Turning_it_on_leaves_the_data_folder_exactly_as_it_was()
    {
        using var vault = await NewVaultAsync();
        var before = Snapshot();

        await Biometric.TurnOnAsync(vault, Password);
        await Biometric.UnlockAsync(vault);

        // A stolen data folder holds nothing more than it would without biometric unlock.
        Assert.Equal(before, Snapshot());
        var record = Encoding.UTF8.GetString(Assert.Single(_records.Items).Value);
        Assert.DoesNotContain(Convert.ToBase64String(_hello.LastSecret!), record);
        Assert.DoesNotContain(Password, record);
    }

    [Fact]
    public async Task The_saved_record_never_holds_the_data_key_itself()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var (dataKey, _) = await vault.DataKeyForAsync(Password);

        var record = Assert.Single(_records.Items).Value;
        Assert.DoesNotContain(Convert.ToBase64String(dataKey), Encoding.UTF8.GetString(record));
        Assert.False(record.AsSpan().IndexOf(dataKey) >= 0);
    }

    [Fact]
    public async Task Cancelling_the_prompt_keeps_it_on_and_the_vault_locked()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);

        _hello.NextAnswer = AuthenticatorStatus.Cancelled;
        Assert.Equal(BiometricOutcome.Cancelled, (await Biometric.UnlockAsync(vault)).Outcome);
        Assert.True(vault.IsLocked);
        Assert.True(Biometric.IsOn(vault));

        _hello.NextAnswer = AuthenticatorStatus.Failed;
        var failed = await Biometric.UnlockAsync(vault);
        Assert.Equal(BiometricOutcome.Failed, failed.Outcome);
        Assert.True(Biometric.IsOn(vault));

        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(vault)).Outcome);
    }

    [Fact]
    public async Task Cancelling_while_turning_on_saves_nothing()
    {
        using var vault = await NewVaultAsync();
        _hello.NextAnswer = AuthenticatorStatus.Cancelled;

        Assert.Equal(BiometricOutcome.Cancelled, (await Biometric.TurnOnAsync(vault, Password)).Outcome);
        Assert.False(Biometric.IsOn(vault));
    }

    [Fact]
    public async Task Cancelling_dismisses_the_prompt_and_leaves_the_vault_locked()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.Hold = new TaskCompletionSource();
        using var cancel = new CancellationTokenSource();

        var unlocking = Biometric.UnlockAsync(vault, cancel: cancel.Token);
        Assert.False(unlocking.IsCompleted);
        cancel.Cancel();

        Assert.Equal(BiometricOutcome.Cancelled, (await unlocking.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        Assert.True(_hello.SawCancel);
        Assert.True(vault.IsLocked);
        Assert.True(Biometric.IsOn(vault));
    }

    [Fact]
    public async Task An_answer_that_comes_after_cancelling_doesnt_unlock_the_vault()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.Hold = new TaskCompletionSource();
        _hello.HonoursCancel = false; // The user passed the prompt just as the window closed.
        using var cancel = new CancellationTokenSource();

        var unlocking = Biometric.UnlockAsync(vault, cancel: cancel.Token);
        cancel.Cancel();
        _hello.Hold.SetResult();

        Assert.Equal(BiometricOutcome.Cancelled, (await unlocking.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        Assert.True(vault.IsLocked);
    }

    [Fact]
    public async Task Cancelling_while_the_vault_opens_locks_it_again()
    {
        using var vault = await NewVaultAsync();
        var (dataKey, fingerprint) = await vault.DataKeyForAsync(Password);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.UnlockWithDataKeyAsync(dataKey, fingerprint, cancel.Token));

        Assert.True(vault.IsLocked);
    }

    [Fact]
    public async Task Only_one_prompt_at_a_time()
    {
        using var vault = await NewVaultAsync();
        var biometric = Biometric;
        await biometric.TurnOnAsync(vault, Password);
        _hello.Hold = new TaskCompletionSource();
        int prompts = _hello.Prompts;

        var first = biometric.UnlockAsync(vault);
        var second = await biometric.UnlockAsync(vault);

        Assert.Equal(BiometricOutcome.Failed, second.Outcome);
        Assert.Contains("already asking", second.Message);
        Assert.Equal(prompts + 1, _hello.Prompts);
        _hello.Hold.SetResult();
        Assert.Equal(BiometricOutcome.Done, (await first.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
    }

    [Fact]
    public async Task Changing_the_master_password_turns_it_off_and_says_why()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var oldKey = Assert.Single(_hello.KeyNames);
        await vault.ChangePasswordAsync(Password, NewPassword);
        vault.Lock();
        int prompts = _hello.Prompts;

        var result = await Biometric.UnlockAsync(vault);

        Assert.Equal(BiometricOutcome.TurnedOff, result.Outcome);
        Assert.Contains("master password was changed", result.Message);
        Assert.Equal(prompts, _hello.Prompts); // Found out before asking for a finger.
        Assert.True(vault.IsLocked);
        Assert.False(Biometric.IsOn(vault));
        Assert.Empty(_records.Records);
        await vault.UnlockAsync(NewPassword);

        // The authenticator's key isn't deleted by itself, only when the user next turns this on or off.
        Assert.Empty(_hello.Deleted);
        vault.Lock();
        Assert.Equal(BiometricOutcome.Done, (await Biometric.TurnOnAsync(vault, NewPassword)).Outcome);
        Assert.Contains(oldKey, _hello.Deleted);
        Assert.DoesNotContain(oldKey, _hello.KeyNames);
        Assert.Single(_records.Items);
    }

    [Fact]
    public async Task A_password_change_made_by_another_copy_of_Orrery_is_noticed_where_the_vault_is()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);

        // Another copy, which knows nothing of this one's history, changes the password.
        using (var elsewhere = new VaultFileStore(VaultPath, FastKdf))
        {
            await elsewhere.ChangePasswordAsync(Password, NewPassword);
        }

        // It's off, and said so, but the old record is only found by where it was used: it could be another vault's,
        // so it and its key are left until the user turns this off.
        using var reopened = new VaultFileStore(VaultPath, FastKdf);
        Assert.Contains("master password was changed", await Biometric.TurnOffIfStaleAsync(reopened));
        Assert.False(Biometric.IsOn(reopened));
        Assert.Equal(BiometricOutcome.TurnedOff, (await Biometric.UnlockAsync(reopened)).Outcome);
        Assert.Single(_records.Records);
        Assert.False(_records.Items.ContainsKey("orrery:biometric-retired-keys"));

        // Turning it on for the new password leaves the old record too; turning it off removes both, with their keys.
        await Biometric.TurnOnAsync(reopened, NewPassword);
        Assert.Null(await Biometric.TurnOffIfStaleAsync(reopened));
        Assert.Equal(2, _records.Records.Count);
        Assert.True(await Biometric.TurnOffAsync(reopened));
        Assert.Empty(_records.Items);
        Assert.Empty(_hello.KeyNames);
    }

    [Fact]
    public async Task A_different_vault_made_where_one_was_doesnt_take_away_the_first_ones_unlock()
    {
        var keptCopy = Path.Combine(_dir, "kept", "vault.frv");
        using (var first = await NewVaultAsync())
        {
            await Biometric.TurnOnAsync(first, Password);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(keptCopy)!);
        File.Move(VaultPath, keptCopy);
        var firstKey = Assert.Single(_hello.KeyNames);

        // A new vault in the same place: it reads as off, and neither checking nor turning it on touches the first's.
        using (var second = await NewVaultAsync())
        {
            await Biometric.TurnOffIfStaleAsync(second);
            Assert.Equal(BiometricOutcome.TurnedOff, (await Biometric.UnlockAsync(second)).Outcome);
            Assert.Equal(BiometricOutcome.Done, (await Biometric.TurnOnAsync(second, Password)).Outcome);
            Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(second)).Outcome);
            second.Lock();
            await Biometric.TurnOffIfStaleAsync(second);
        }

        Assert.DoesNotContain(firstKey, _hello.Deleted);
        Assert.False(_records.Items.ContainsKey("orrery:biometric-retired-keys"));

        // The first vault, put back, still opens with it.
        File.Delete(VaultPath);
        File.Move(keptCopy, VaultPath);
        using var restored = new VaultFileStore(VaultPath, FastKdf);
        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(restored)).Outcome);
        Assert.Equal(_saved, await restored.GetAsync(_saved.Id));
    }

    [Fact]
    public async Task Settings_finds_a_stale_key_without_a_prompt()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        Assert.Null(await Biometric.TurnOffIfStaleAsync(vault));

        await vault.ChangePasswordAsync(Password, NewPassword);
        int prompts = _hello.Prompts;

        Assert.Contains("turned off", await Biometric.TurnOffIfStaleAsync(vault));
        Assert.Equal(prompts, _hello.Prompts);
        Assert.False(Biometric.IsOn(vault));
        Assert.Empty(_hello.Deleted);
    }

    [Fact]
    public async Task Turning_it_off_after_a_password_change_deletes_the_old_record_and_key()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var oldKey = Assert.Single(_hello.KeyNames);
        await vault.ChangePasswordAsync(Password, NewPassword);

        // What Settings does after the change.
        Assert.True(await Biometric.TurnOffAsync(vault));

        Assert.Empty(_records.Items);
        Assert.Contains(oldKey, _hello.Deleted);
        Assert.Empty(_hello.KeyNames);
        Assert.False(await Biometric.TurnOffAsync(vault)); // Nothing left.
    }

    [Fact]
    public async Task An_old_copy_of_the_record_holds_a_data_key_that_no_longer_opens_the_vault()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var oldRecord = Assert.Single(_records.Items).Value;
        await vault.ChangePasswordAsync(Password, NewPassword);
        vault.Lock();

        // Someone kept the old record and can still pass the prompt for its key: the record decrypts, and holds the
        // data key from before the change.
        var record = JsonNode.Parse(oldRecord)!;
        var secret = _hello.SecretFor((string)record["keyName"]!, Convert.FromBase64String((string)record["challenge"]!));
        var oldDataKey = BiometricUnlock.UnwrapForTests(oldRecord, secret);
        Assert.NotNull(oldDataKey);

        // Even pointed at the vault's current key block, so no fingerprint check stops it, that key can't decrypt
        // anything: the change made a new data key.
        var current = vault.KeyFingerprint()!;
        await Assert.ThrowsAsync<InvalidDataException>(() => vault.UnlockWithDataKeyAsync(oldDataKey!, current));
        Assert.True(vault.IsLocked);

        // And put back where Orrery would look for it now, it's refused without a prompt.
        _records.Items[BiometricUnlock.RecordNameFor(current)] = oldRecord;
        int prompts = _hello.Prompts;
        Assert.Equal(BiometricOutcome.TurnedOff, (await Biometric.UnlockAsync(vault)).Outcome);
        Assert.Equal(prompts, _hello.Prompts);
        Assert.True(vault.IsLocked);
        Assert.Empty(_records.Records);
    }

    [Fact]
    public async Task A_key_the_authenticator_cant_find_is_kept_and_only_stops_for_this_session()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var biometric = Biometric;
        _hello.LoseKeys();

        var result = await biometric.UnlockAsync(vault);

        Assert.Equal(BiometricOutcome.Unavailable, result.Outcome);
        Assert.StartsWith("The test key is gone.", result.Message);
        Assert.Contains("off and on again in Settings", result.Message);
        Assert.True(vault.IsLocked);

        // Nothing is deleted: the record stays, and the authenticator's key is never deleted by itself.
        Assert.True(biometric.IsOn(vault));
        Assert.Single(_records.Records);
        Assert.Empty(_hello.Deleted);

        // Not asked again this session.
        int prompts = _hello.Prompts;
        Assert.Equal(BiometricOutcome.Unavailable, (await biometric.UnlockAsync(vault)).Outcome);
        Assert.Equal(prompts, _hello.Prompts);
    }

    [Fact]
    public async Task A_replaced_key_turns_it_off_instead_of_opening_anything()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var key = Assert.Single(_hello.KeyNames);
        _hello.ReplaceKey(key);

        var result = await Biometric.UnlockAsync(vault);

        Assert.Equal(BiometricOutcome.TurnedOff, result.Outcome);
        Assert.Contains("no longer matches", result.Message);
        Assert.True(vault.IsLocked);
        Assert.False(Biometric.IsOn(vault));
        Assert.Empty(_hello.Deleted); // The record goes; the key waits for the user.
    }

    [Theory]
    [InlineData("ct")]
    [InlineData("nonce")]
    [InlineData("challenge")]
    public async Task An_edited_record_doesnt_open_the_vault(string field)
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var (name, bytes) = Assert.Single(_records.Items);
        var record = JsonNode.Parse(bytes)!;
        var value = Convert.FromBase64String((string)record[field]!);
        value[0] ^= 1;
        record[field] = Convert.ToBase64String(value);
        _records.Items[name] = Encoding.UTF8.GetBytes(record.ToJsonString());

        Assert.Equal(BiometricOutcome.TurnedOff, (await Biometric.UnlockAsync(vault)).Outcome);
        Assert.True(vault.IsLocked);
    }

    [Fact]
    public async Task A_damaged_record_is_dropped()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var name = Assert.Single(_records.Items).Key;
        _records.Items[name] = Encoding.UTF8.GetBytes("{ not json");

        Assert.False(Biometric.IsOn(vault));
        Assert.Equal(BiometricOutcome.NotOn, (await Biometric.UnlockAsync(vault)).Outcome);
        Assert.Empty(_records.Items);
    }

    [Fact]
    public async Task A_version_2_record_missing_a_field_is_dropped()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var (name, bytes) = Assert.Single(_records.Items);
        var record = JsonNode.Parse(bytes)!.AsObject();
        record.Remove("nonce");
        _records.Items[name] = Encoding.UTF8.GetBytes(record.ToJsonString());

        Assert.False(Biometric.IsOn(vault));
        Assert.Empty(_records.Records);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(42)]
    public async Task A_record_from_a_newer_version_is_left_alone_and_reads_as_off(int version)
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var (name, bytes) = Assert.Single(_records.Items);
        var record = JsonNode.Parse(bytes)!.AsObject();
        record["version"] = version;
        record["challenge"] = 12345; // A layout this build doesn't know.
        var newer = Encoding.UTF8.GetBytes(record.ToJsonString());
        _records.Items[name] = newer;
        int prompts = _hello.Prompts;

        Assert.False(Biometric.IsOn(vault));
        Assert.Null(await Biometric.TurnOffIfStaleAsync(vault));
        Assert.Equal(BiometricOutcome.NotOn, (await Biometric.UnlockAsync(vault)).Outcome);

        Assert.Equal(prompts, _hello.Prompts);
        Assert.True(vault.IsLocked);
        Assert.Equal(newer, _records.Items[name]);
        Assert.Empty(_hello.Deleted);
        Assert.False(_records.Items.ContainsKey("orrery:biometric-retired-keys"));
    }

    [Fact]
    public async Task Each_vault_has_its_own_key_and_one_cant_open_another()
    {
        using var first = await NewVaultAsync();
        using var second = await NewVaultAsync(Path.Combine(_dir, "other", "vault.frv"));
        await Biometric.TurnOnAsync(first, Password);
        await Biometric.TurnOnAsync(second, Password);
        Assert.Equal(2, _records.Records.Count);
        Assert.Equal(2, _hello.KeyNames.Count);

        // The first vault's record copied into the second's slot is refused, and the first's key is left alone.
        var secondName = BiometricUnlock.RecordNameFor(second.KeyFingerprint()!);
        _records.Items[secondName] = _records.Records.Single(r => r.Key != secondName).Value;
        Assert.Equal(BiometricOutcome.NotOn, (await Biometric.UnlockAsync(second)).Outcome);
        Assert.True(second.IsLocked);

        await Biometric.TurnOnAsync(second, Password);
        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(first)).Outcome);
    }

    [Fact]
    public async Task Turning_it_off_deletes_the_record_and_the_authenticators_key()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);

        Assert.True(await Biometric.TurnOffAsync(vault));

        Assert.False(Biometric.IsOn(vault));
        Assert.Empty(_records.Items);
        Assert.Empty(_hello.KeyNames);
        Assert.Single(_hello.Deleted);
        Assert.Equal(BiometricOutcome.NotOn, (await Biometric.UnlockAsync(vault)).Outcome);
    }

    [Fact]
    public async Task Turning_it_on_again_replaces_the_old_key()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        await Biometric.TurnOnAsync(vault, Password);

        Assert.Single(_records.Items);
        Assert.Single(_hello.KeyNames);
        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(vault)).Outcome);
    }

    // ---- Where the vault is doesn't matter ----

    [Fact]
    public async Task It_keeps_working_after_the_vault_file_moves()
    {
        using (var vault = await NewVaultAsync())
        {
            await Biometric.TurnOnAsync(vault, Password);
        }

        var moved = Path.Combine(_dir, "moved", "elsewhere", "vault.frv");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(VaultPath, moved);

        using var there = new VaultFileStore(moved, FastKdf);
        Assert.True(Biometric.IsOn(there));
        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(there)).Outcome);
        Assert.Equal(_saved, await there.GetAsync(_saved.Id));

        // The record now notes the new place, so a password change there is found too.
        var record = JsonNode.Parse(Assert.Single(_records.Items).Value)!;
        Assert.Equal(BiometricUnlock.PlaceOf(there), (string?)record["place"]);
    }

    [WindowsFact]
    public async Task It_keeps_working_when_the_drive_letter_changes()
    {
        using (var vault = await NewVaultAsync())
        {
            await Biometric.TurnOnAsync(vault, Password);
        }

        // The same file reached through a path that differs from the root up, as it does when Windows gives the
        // drive a new letter. (Changing a real drive letter needs an administrator, so the \\?\ form stands in.)
        var differentRoot = @"\\?\" + Path.GetFullPath(VaultPath);
        using var there = new VaultFileStore(differentRoot, FastKdf);
        using var here = new VaultFileStore(VaultPath, FastKdf);
        Assert.NotEqual(BiometricUnlock.PlaceOf(here), BiometricUnlock.PlaceOf(there));

        Assert.True(Biometric.IsOn(there));
        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(there)).Outcome);
    }

    [Fact]
    public async Task It_keeps_working_after_move_data_and_turning_it_off_covers_both_copies()
    {
        var oldDir = Path.Combine(_dir, "old");
        var newDir = Path.Combine(_dir, "new");
        Directory.CreateDirectory(oldDir);
        using (var vault = await NewVaultAsync(Path.Combine(oldDir, "vault.frv")))
        {
            await Biometric.TurnOnAsync(vault, Password);
        }

        DataMover.Copy(oldDir, newDir);

        using var moved = new VaultFileStore(Path.Combine(newDir, "vault.frv"), FastKdf);
        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(moved)).Outcome);
        Assert.Equal(_saved, await moved.GetAsync(_saved.Id));

        // The old copy is the same vault, so until it's deleted it opens too, and turning it off covers both.
        using var old = new VaultFileStore(Path.Combine(oldDir, "vault.frv"), FastKdf);
        Assert.True(Biometric.IsOn(old));
        Assert.True(await Biometric.TurnOffAsync(moved));
        Assert.False(Biometric.IsOn(old));
        Assert.Empty(_records.Items);
    }

    [Fact]
    public async Task Turning_it_off_leaves_nothing_saved_for_the_vault_or_its_earlier_passwords()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        await vault.ChangePasswordAsync(Password, NewPassword);
        vault.Lock();
        await Biometric.TurnOnAsync(vault, NewPassword);
        Assert.Equal(2, _hello.Deleted.Count + _hello.KeyNames.Count); // The first key was replaced.

        await Biometric.TurnOffAsync(vault);

        Assert.Empty(_records.Items);
        Assert.Empty(_hello.KeyNames);
        Assert.Equal(BiometricOutcome.NotOn, (await Biometric.UnlockAsync(vault)).Outcome);
    }

    // ---- Records from the first test builds, named after the vault's path ----

    [Fact]
    public async Task A_path_keyed_record_is_removed_and_the_user_told()
    {
        using var vault = await NewVaultAsync();
        var place = BiometricUnlock.PlaceOf(vault);
        var legacyKey = "Orrery vault " + place;
        var fingerprint = vault.KeyFingerprint()!;
        _records.Items[BiometricUnlock.RecordPrefix + place] = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = "orrery-biometric", ["version"] = 1, ["authenticator"] = "test-hello", ["keyName"] = legacyKey,
            ["challenge"] = Convert.ToBase64String(new byte[32]), ["vault"] = fingerprint,
            ["nonce"] = Convert.ToBase64String(new byte[24]), ["ct"] = Convert.ToBase64String(new byte[48]),
        }.ToJsonString());
        int prompts = _hello.Prompts;

        var result = await Biometric.UnlockAsync(vault);

        Assert.Equal(BiometricOutcome.TurnedOff, result.Outcome);
        Assert.Contains("earlier test version", result.Message);
        Assert.Equal(prompts, _hello.Prompts);
        Assert.Empty(_records.Records);

        // Its key goes when the user turns it on again.
        Assert.Empty(_hello.Deleted);
        await Biometric.TurnOnAsync(vault, Password);
        Assert.Contains(legacyKey, _hello.Deleted);
    }

    // ---- What's refused ----

    [Fact]
    public async Task Nothing_is_offered_or_prompted_in_a_remote_session()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        int prompts = _hello.Prompts;
        _remote.IsRemote = true;

        Assert.Contains("remote session", await Biometric.CheckAvailableAsync());
        var unlock = await Biometric.UnlockAsync(vault);
        Assert.Equal(BiometricOutcome.Unavailable, unlock.Outcome);
        Assert.Contains("master password", unlock.Message);
        Assert.Equal(prompts, _hello.Prompts);
        Assert.True(Biometric.IsOn(vault)); // Still on for when the user is back at the computer.

        await Biometric.TurnOffAsync(vault);
        var turnOn = await Biometric.TurnOnAsync(vault, Password);
        Assert.Equal(BiometricOutcome.Failed, turnOn.Outcome);
        Assert.Contains("remote session", turnOn.Message);
        Assert.Equal(prompts, _hello.Prompts);
        Assert.Empty(_records.Items);
    }

    [Fact]
    public async Task Turning_on_is_refused_where_the_authenticator_isnt_available()
    {
        using var vault = await NewVaultAsync();
        _hello.Unavailable = "No sensor.";

        var result = await Biometric.TurnOnAsync(vault, Password);

        Assert.Equal(BiometricOutcome.Failed, result.Outcome);
        Assert.Equal("No sensor.", result.Message);
        Assert.Equal(0, _hello.Prompts);
        Assert.Empty(_records.Items);
    }

    [Fact]
    public async Task A_key_not_shown_to_be_in_hardware_is_refused_and_deleted()
    {
        using var vault = await NewVaultAsync();
        _hello.InHardware = false;

        var result = await Biometric.TurnOnAsync(vault, Password);

        Assert.Equal(BiometricOutcome.Failed, result.Outcome);
        Assert.Contains("hardware", result.Message);
        Assert.Empty(_records.Items);
        Assert.Empty(_hello.KeyNames);
        Assert.Single(_hello.Deleted);
    }

    [Fact]
    public async Task A_secret_shorter_than_32_bytes_is_refused_when_turning_on()
    {
        using var vault = await NewVaultAsync();
        _hello.SecretLength = BiometricUnlock.MinSecretSize - 1;

        Assert.Equal(BiometricOutcome.Failed, (await Biometric.TurnOnAsync(vault, Password)).Outcome);
        Assert.Empty(_records.Items);
        Assert.Empty(_hello.KeyNames);
    }

    [Fact]
    public async Task A_secret_shorter_than_32_bytes_is_refused_when_unlocking()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.SecretLength = 16;

        Assert.Equal(BiometricOutcome.Failed, (await Biometric.UnlockAsync(vault)).Outcome);
        Assert.True(vault.IsLocked);
        Assert.True(Biometric.IsOn(vault));
    }

    [Fact]
    public async Task Says_why_when_the_authenticator_isnt_available()
    {
        _hello.Unavailable = "No sensor.";
        Assert.Equal("No sensor.", await Biometric.CheckAvailableAsync());
        _hello.Unavailable = null;
        Assert.Null(await Biometric.CheckAvailableAsync());
    }

    [Fact]
    public void Linux_has_no_biometric_unlock()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.Null(BiometricUnlock.ForThisSystem());
        }
        else if (OperatingSystem.IsWindows())
        {
            Assert.Equal("Windows Hello", BiometricUnlock.ForThisSystem()!.Name);
        }
    }

    Dictionary<string, string> Snapshot() => Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(_dir, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
