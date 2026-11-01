using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Credentials;

/// <summary>
/// Stands in for Windows Hello or Touch ID: each key is a random secret that only "the authenticator" holds, and the
/// released secret is an HMAC of the challenge under it, so the same key and challenge always give the same answer.
/// </summary>
public sealed class FakeAuthenticator : IPlatformAuthenticator
{
    readonly Dictionary<string, byte[]> _keys = [];

    public string Name => "Test Hello";

    public string Id => "test-hello";

    public string? Unavailable { get; set; }

    /// <summary>What the next prompt answers instead of succeeding; null to succeed.</summary>
    public AuthenticatorStatus? NextAnswer { get; set; }

    public int Prompts { get; private set; }

    public List<string> Deleted { get; } = [];

    public IReadOnlyCollection<string> KeyNames => _keys.Keys;

    /// <summary>Last secret handed out, to check it never reaches a file or log.</summary>
    public byte[]? LastSecret { get; private set; }

    public Task<string?> CheckAvailableAsync() => Task.FromResult(Unavailable);

    public Task<AuthenticatorResult> CreateAsync(string keyName, byte[] challenge, nint window)
    {
        if (Answer() is { } answer)
        {
            return Task.FromResult(answer);
        }

        _keys[keyName] = RandomNumberGenerator.GetBytes(32);
        return Task.FromResult(Sign(keyName, challenge));
    }

    public Task<AuthenticatorResult> ReleaseAsync(string keyName, byte[] challenge, nint window)
    {
        if (Answer() is { } answer)
        {
            return Task.FromResult(answer);
        }

        return Task.FromResult(_keys.ContainsKey(keyName) ? Sign(keyName, challenge) : new AuthenticatorResult(AuthenticatorStatus.KeyGone, Message: "The test key is gone."));
    }

    public Task DeleteAsync(string keyName)
    {
        _keys.Remove(keyName);
        Deleted.Add(keyName);
        return Task.CompletedTask;
    }

    /// <summary>As if the platform replaced the key behind Orrery's back: same name, different secret.</summary>
    public void ReplaceKey(string keyName) => _keys[keyName] = RandomNumberGenerator.GetBytes(32);

    /// <summary>As if Windows Hello were reset or the enrolled fingers changed.</summary>
    public void LoseKeys() => _keys.Clear();

    AuthenticatorResult? Answer()
    {
        Prompts++;
        if (NextAnswer is not { } status)
        {
            return null;
        }

        NextAnswer = null;
        return new AuthenticatorResult(status, Message: $"Test answer {status}.");
    }

    AuthenticatorResult Sign(string keyName, byte[] challenge)
    {
        LastSecret = HMACSHA256.HashData(_keys[keyName], challenge);
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
}

public class BiometricUnlockTests : IDisposable
{
    static readonly VaultFileStore.KdfSettings FastKdf = new(MemoryKiB: 8 * 1024, Iterations: 1);
    const string Password = "correct horse battery staple";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly FakeAuthenticator _hello = new();
    readonly MemorySecrets _records = new();
    readonly Credential _saved = new(Guid.NewGuid(), "corp admin", CredentialKind.Password, "admin", "corp", "Sup3r-Secret!");

    BiometricUnlock Biometric => new(_hello, _records);

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
    public async Task Changing_the_master_password_turns_it_off_and_says_why()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        await vault.ChangePasswordAsync(Password, "a brand new password");
        vault.Lock();
        int prompts = _hello.Prompts;

        var result = await Biometric.UnlockAsync(vault);

        Assert.Equal(BiometricOutcome.TurnedOff, result.Outcome);
        Assert.Contains("master password was changed", result.Message);
        Assert.Equal(prompts, _hello.Prompts); // Found out before asking for a finger.
        Assert.True(vault.IsLocked);
        Assert.False(Biometric.IsOn(vault));
        Assert.Empty(_hello.KeyNames);
        await vault.UnlockAsync("a brand new password");
    }

    [Fact]
    public async Task Settings_finds_a_stale_key_without_a_prompt()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        Assert.Null(await Biometric.TurnOffIfStaleAsync(vault));

        await vault.ChangePasswordAsync(Password, "a brand new password");
        int prompts = _hello.Prompts;

        Assert.Contains("turned off", await Biometric.TurnOffIfStaleAsync(vault));
        Assert.Equal(prompts, _hello.Prompts);
        Assert.False(Biometric.IsOn(vault));
    }

    [Fact]
    public async Task An_old_copy_of_the_record_cant_open_the_vault_after_a_password_change()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        var oldRecord = _records.Items.ToDictionary(r => r.Key, r => r.Value);
        await vault.ChangePasswordAsync(Password, "a brand new password");
        vault.Lock();

        // Put the old record back, even with the authenticator's key: the vault's data key has changed.
        foreach (var (key, value) in oldRecord) _records.Items[key] = value;
        Assert.Equal(BiometricOutcome.TurnedOff, (await Biometric.UnlockAsync(vault)).Outcome);
        Assert.True(vault.IsLocked);
    }

    [Fact]
    public async Task A_lost_key_turns_it_off_with_the_platforms_reason()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.LoseKeys();

        var result = await Biometric.UnlockAsync(vault);

        Assert.Equal(BiometricOutcome.TurnedOff, result.Outcome);
        Assert.StartsWith("The test key is gone.", result.Message);
        Assert.Contains("turn it on again in Settings", result.Message);
        Assert.False(Biometric.IsOn(vault));
        Assert.True(vault.IsLocked);
    }

    [Fact]
    public async Task A_replaced_key_turns_it_off_instead_of_opening_anything()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);
        _hello.ReplaceKey(Assert.Single(_hello.KeyNames));

        var result = await Biometric.UnlockAsync(vault);

        Assert.Equal(BiometricOutcome.TurnedOff, result.Outcome);
        Assert.Contains("no longer matches", result.Message);
        Assert.True(vault.IsLocked);
        Assert.False(Biometric.IsOn(vault));
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
    public async Task Each_vault_has_its_own_key_and_one_cant_open_another()
    {
        using var first = await NewVaultAsync();
        using var second = await NewVaultAsync(Path.Combine(_dir, "other", "vault.frv"));
        await Biometric.TurnOnAsync(first, Password);
        await Biometric.TurnOnAsync(second, Password);
        Assert.Equal(2, _records.Items.Count);
        Assert.Equal(2, _hello.KeyNames.Count);

        // The first vault's record copied into the second's slot is refused.
        var firstRecord = _records.Items.Single(r => r.Key != RecordNameOf(second)).Value;
        _records.Items[RecordNameOf(second)] = firstRecord;
        Assert.Equal(BiometricOutcome.NotOn, (await Biometric.UnlockAsync(second)).Outcome);
        Assert.True(second.IsLocked);

        Assert.Equal(BiometricOutcome.Done, (await Biometric.UnlockAsync(first)).Outcome);
    }

    [Fact]
    public async Task Turning_it_off_deletes_the_record_and_the_authenticators_key()
    {
        using var vault = await NewVaultAsync();
        await Biometric.TurnOnAsync(vault, Password);

        await Biometric.TurnOffAsync(vault);

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

    string RecordNameOf(VaultFileStore vault)
    {
        // The record for a vault is the one that appears when it alone is turned on.
        var probe = new MemorySecrets();
        new BiometricUnlock(new FakeAuthenticator(), probe).TurnOnAsync(vault, Password).GetAwaiter().GetResult();
        return Assert.Single(probe.Items).Key;
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
