using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Orrery.Core.Diagnostics;

namespace Orrery.Core.Credentials;

/// <summary>What a platform authenticator did. <see cref="Message"/> is a plain sentence for the user, when there is one.</summary>
public enum AuthenticatorStatus
{
    Success,

    /// <summary>The user canceled or chose the password instead. Nothing changes.</summary>
    Canceled,

    /// <summary>
    /// The authenticator says it has no such key. That may be for good (Windows Hello was reset, the enrolled fingers
    /// changed) or only for now (a remote session, a profile still loading), so it never deletes anything by itself.
    /// </summary>
    KeyGone,

    /// <summary>It didn't work this time (sensor locked out, an error); trying again later may work.</summary>
    Failed,
}

/// <param name="HardwareBacked">
/// From <see cref="IPlatformAuthenticator.CreateAsync"/>: true only when the platform showed that the new key is held
/// in hardware (a TPM, the Secure Enclave). Biometric unlock refuses a key without it.
/// </param>
public sealed record AuthenticatorResult(AuthenticatorStatus Status, byte[]? Secret = null, string? Message = null, bool HardwareBacked = false)
{
    public static AuthenticatorResult Released(byte[] secret, bool hardwareBacked = false) =>
        new(AuthenticatorStatus.Success, secret, HardwareBacked: hardwareBacked);
}

/// <summary>
/// Windows Hello or Touch ID, as biometric unlock needs it: a key only the platform authenticator can use, and a secret
/// that only comes back after the user passes its prompt. The same key and challenge must always give the same secret.
/// </summary>
public interface IPlatformAuthenticator
{
    /// <summary>What to call it in the app: "Windows Hello", "Touch ID".</summary>
    string Name { get; }

    /// <summary>Recorded with each saved key, e.g. "windows-hello".</summary>
    string Id { get; }

    /// <summary>Null when it can be used here; otherwise why not, as a sentence for Settings. Never prompts.</summary>
    Task<string?> CheckAvailableAsync();

    /// <summary>
    /// Makes a new key under <paramref name="keyName"/>, replacing any old one, and returns its secret for
    /// <paramref name="challenge"/>. Prompts. Sets <see cref="AuthenticatorResult.HardwareBacked"/> only when it can
    /// show the key is held in hardware, and deletes a key it made but couldn't use.
    /// </summary>
    /// <param name="window">The window to show the prompt over, or zero.</param>
    /// <param name="cancel">Dismisses the prompt.</param>
    Task<AuthenticatorResult> CreateAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel);

    /// <summary>The secret <see cref="CreateAsync"/> returned for the same key and challenge. Prompts.</summary>
    Task<AuthenticatorResult> ReleaseAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel);

    /// <summary>Deletes the key; a missing one is fine.</summary>
    Task DeleteAsync(string keyName);
}

public enum BiometricOutcome
{
    /// <summary>Turned on, or the vault is open.</summary>
    Done,

    /// <summary>The user canceled the prompt. Nothing changed.</summary>
    Canceled,

    /// <summary>It's been turned off; <see cref="BiometricResult.Message"/> says why.</summary>
    TurnedOff,

    /// <summary>It didn't work this time, and is still on (or still off, when turning on).</summary>
    Failed,

    /// <summary>It's on, but can't be used now (a remote session, or the key wasn't found); the message says why.</summary>
    Unavailable,

    /// <summary>It isn't on for this vault.</summary>
    NotOn,
}

public sealed record BiometricResult(BiometricOutcome Outcome, string? Message = null);

/// <summary>
/// Unlocks a <see cref="VaultFileStore"/> with Windows Hello or Touch ID (docs/design-biometric-unlock.md). The vault's
/// data key is wrapped a second time, under a key derived from a secret only the platform authenticator releases, and
/// that wrap is kept in <paramref name="records"/> on this computer, never in the data folder. The vault file doesn't
/// change, and the master password keeps working.
/// <para>
/// Records and authenticator keys are named after the vault's key block, not its path, so moving the data folder or a
/// drive letter changing doesn't matter, and a master password change (a new key block) leaves nothing that still
/// matches. The authenticator's key is only deleted when the user turns this off, or when turning it on replaces it.
/// </para>
/// </summary>
public sealed class BiometricUnlock(IPlatformAuthenticator authenticator, ISecretBackend records, IRemoteSession? remoteSession = null)
{
    static readonly ILogger Logger = Log.For("Vault");
    static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    const string Format = "orrery-biometric";
    const int Version = 2;

    /// <summary>The first test builds named records after the vault's path. They're removed, never used.</summary>
    const int PathKeyedVersion = 1;

    const int ChallengeSize = 32;
    const int KeySize = 32;

    /// <summary>Shortest authenticator secret accepted: anything less is a broken authenticator, not a key.</summary>
    public const int MinSecretSize = 32;

    public const string RecordPrefix = "orrery:biometric:";
    const string KeyNamePrefix = "Orrery vault ";

    /// <summary>Authenticator keys whose records were found stale, to delete the next time the user turns this on or off.</summary>
    const string RetiredKeysRecord = "orrery:biometric-retired-keys";

    static readonly Lazy<BiometricUnlock?> ThisSystem = new(Create);

    readonly SemaphoreSlim _prompting = new(1, 1);

    // Records whose key the authenticator couldn't find this run: not offered again until Orrery restarts.
    readonly ConcurrentDictionary<string, string> _offThisSession = new();

    /// <summary>Windows Hello on Windows, Touch ID on macOS; null on Linux, which has no authenticator that holds a key.</summary>
    public static BiometricUnlock? ForThisSystem() => ThisSystem.Value;

    static BiometricUnlock? Create()
    {
        if (OperatingSystem.IsWindows()) return new(new WindowsHelloAuthenticator(), new WindowsCredentialBackend(), new WindowsRemoteSession());
        if (OperatingSystem.IsMacOS()) return new(new MacTouchIdAuthenticator(), new MacKeychainBackend());
        return null;
    }

    public string Name => authenticator.Name;

    /// <summary>
    /// Null when it can be used now; otherwise why not, as a sentence. Covers remote sessions as well as the
    /// authenticator's own checks. Never prompts.
    /// </summary>
    public async Task<string?> CheckAvailableAsync()
    {
        if (RemoteReason() is { } remote)
        {
            return remote;
        }

        try
        {
            return await authenticator.CheckAvailableAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Logger.LogWarning("Couldn't check whether {Authenticator} is available: {Message}", Name, e.Message);
            return $"{Name} couldn't be checked on this computer.";
        }
    }

    string? RemoteReason() => remoteSession?.IsRemote == true
        ? $"This is a remote session. {Name} unlock only works at the computer itself, so use the master password."
        : null;

    /// <summary>True when a key is saved for this vault's current key block.</summary>
    public bool IsOn(VaultFileStore vault) => FingerprintOf(vault) is { } fingerprint && ReadCurrent(fingerprint) is not null;

    /// <summary>
    /// Turns it on for <paramref name="vault"/>: checks it can be used here, checks the master password against the
    /// file, makes a new key (with the system prompt) and saves the wrapped data key. Replaces any earlier key for
    /// this vault.
    /// </summary>
    /// <exception cref="WrongVaultPasswordException"><paramref name="masterPassword"/> doesn't open the vault.</exception>
    public async Task<BiometricResult> TurnOnAsync(VaultFileStore vault, string masterPassword, nint window = 0, CancellationToken cancel = default)
    {
        if (await CheckAvailableAsync() is { } unavailable)
        {
            return new(BiometricOutcome.Failed, unavailable);
        }

        var (dataKey, fingerprint) = await vault.DataKeyForAsync(masterPassword);
        byte[]? secret = null;
        if (!await _prompting.WaitAsync(0, CancellationToken.None))
        {
            CryptographicOperations.ZeroMemory(dataKey);
            return new(BiometricOutcome.Failed, AlreadyAsking);
        }

        try
        {
            var name = KeyNameFor(fingerprint);
            var challenge = RandomNumberGenerator.GetBytes(ChallengeSize);
            var created = await Ask(() => authenticator.CreateAsync(name, challenge, window, cancel), cancel);
            secret = created.Secret;
            if (cancel.IsCancellationRequested || created.Status == AuthenticatorStatus.Canceled)
            {
                if (secret is not null) await DeleteKeyQuietlyAsync(name); // Made just now, and not wanted.
                Logger.LogInformation("{Authenticator} unlock wasn't turned on: canceled", Name);
                return new(BiometricOutcome.Canceled);
            }

            if (created.Status != AuthenticatorStatus.Success || secret is null)
            {
                Logger.LogInformation("{Authenticator} unlock wasn't turned on: {Status}", Name, created.Status);
                return new(BiometricOutcome.Failed, created.Message ?? $"{Name} couldn't make a key.");
            }

            if (!created.HardwareBacked)
            {
                await DeleteKeyQuietlyAsync(name);
                Logger.LogWarning("{Authenticator} unlock wasn't turned on: the key isn't shown to be in hardware", Name);
                return new(BiometricOutcome.Failed, $"{Name} couldn't show that its key is held in hardware on this computer, so nothing was turned on.");
            }

            if (secret.Length < MinSecretSize)
            {
                await DeleteKeyQuietlyAsync(name);
                Logger.LogWarning("{Authenticator} unlock wasn't turned on: its secret is too short", Name);
                return new(BiometricOutcome.Failed, $"{Name} made a key Orrery can't use, so nothing was turned on.");
            }

            var record = new Record(Format, Version, authenticator.Id, name, Convert.ToBase64String(challenge), fingerprint, null!, null!, PlaceOf(vault));
            using var key = WrappingKey(secret, record);
            var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
            var wrapped = Aead.Encrypt(key, nonce, AssociatedData(record), dataKey);
            records.Write(RecordNameFor(fingerprint), JsonSerializer.SerializeToUtf8Bytes(
                record with { Nonce = Convert.ToBase64String(nonce), Ciphertext = Convert.ToBase64String(wrapped) }, JsonOptions));
            _offThisSession.TryRemove(RecordNameFor(fingerprint), out _);

            // Anything saved for this vault before is replaced now. A record found only by its path is left: it may be
            // another vault's, and turning this on for one vault shouldn't take it away from another.
            await RemoveRelatedAsync(vault, fingerprint, deleteKeys: true, placeOnlyToo: false);
            await DeleteRetiredKeysAsync(keep: name);
            Logger.LogInformation("{Authenticator} unlock turned on for the vault", Name);
            return new(BiometricOutcome.Done);
        }
        finally
        {
            _prompting.Release();
            CryptographicOperations.ZeroMemory(dataKey);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Removes keys saved for this vault before a master password change made in this run, or by the first test
    /// builds, and says so when that leaves it off. A record found only by the vault's path (another copy of Orrery
    /// changed the password, or a different vault was here before) is left alone, and reported as either. Never
    /// prompts and never deletes the authenticator's key. Returns the message to show, or null if there's nothing to say.
    /// </summary>
    public Task<string?> TurnOffIfStaleAsync(VaultFileStore vault)
    {
        if (FingerprintOf(vault) is not { } fingerprint)
        {
            return Task.FromResult<string?>(null); // Can't tell; leave it for the unlock window to report the damaged file.
        }

        bool on = ReadCurrent(fingerprint) is not null;
        var stale = Related(vault, fingerprint);
        if (stale.Count == 0)
        {
            return Task.FromResult<string?>(null);
        }

        // A record found only by the vault's path is left, key and all: it may be another vault's, or open a copy kept elsewhere.
        var removed = stale.Where(r => !r.PlaceOnly).ToList();
        foreach (var record in removed)
        {
            records.Delete(record.Name);
            Retire(record.KeyName);
        }

        if (removed.Count > 0)
        {
            Logger.LogInformation("Removed {Count} stale {Authenticator} unlock record(s) for the vault", removed.Count, Name);
        }

        if (on)
        {
            return Task.FromResult<string?>(null);
        }

        bool earlierBuild = stale.Any(r => r.PathKeyed && r.Vault == fingerprint);
        if (!earlierBuild && removed.Count == 0)
        {
            // Only a record for this path, so it can't be told which vault it was for: say both, and claim neither.
            Logger.LogInformation("{Authenticator} unlock is off: only a record for a vault once at this path was found", Name);
            return Task.FromResult<string?>(PlaceOnlyMessage);
        }

        Logger.LogInformation("{Authenticator} unlock turned off: {Reason}", Name, earlierBuild ? "set up by an earlier build" : "the master password changed");
        return Task.FromResult<string?>(earlierBuild
            ? $"{Name} unlock was set up by an earlier test version of Orrery, so it was turned off. Unlock with the master password, then turn it on again in Settings."
            : $"The master password was changed, so {Name} unlock was turned off. Unlock with the master password, then turn it on again in Settings.");
    }

    string PlaceOnlyMessage =>
        $"{Name} unlock was set up for a vault that used to be at this location, or for this vault before another copy of Orrery "
        + $"changed its master password, so it's off for this vault. Unlock with the master password, and turn it on again in Settings if you want it.";

    /// <summary>
    /// True when a record found only by this vault's path is saved: one left by a vault that used to be here, or by
    /// this vault before another copy of Orrery changed its password. Settings offers to remove those. Never prompts.
    /// </summary>
    public bool HasLeftovers(VaultFileStore vault) => Leftovers(vault).Count > 0;

    /// <summary>
    /// Removes the records <see cref="HasLeftovers"/> finds, and their authenticator keys: whatever they opened (an
    /// old vault, a backup from before a password change) won't open with this any more. The user asks for this.
    /// Returns how many were removed.
    /// </summary>
    public async Task<int> RemoveLeftoversAsync(VaultFileStore vault)
    {
        var leftovers = Leftovers(vault);
        foreach (var record in leftovers)
        {
            records.Delete(record.Name);
            if (IsOurKeyName(record.KeyName)) await DeleteKeyQuietlyAsync(record.KeyName!);
        }

        if (leftovers.Count > 0)
        {
            Logger.LogInformation("Removed {Count} old {Authenticator} unlock record(s) left at the vault's path", leftovers.Count, Name);
        }

        return leftovers.Count;
    }

    /// <summary>
    /// Opens the vault with the saved key (one prompt). Turns itself off, and says why, only when the saved record
    /// provably can't work again: the master password changed, or it no longer decrypts. If
    /// <paramref name="cancel"/> fires, the prompt is dismissed and the vault stays locked.
    /// </summary>
    public async Task<BiometricResult> UnlockAsync(VaultFileStore vault, nint window = 0, CancellationToken cancel = default)
    {
        if (RemoteReason() is { } remote)
        {
            return new(BiometricOutcome.Unavailable, remote);
        }

        if (FingerprintOf(vault) is not { } fingerprint)
        {
            return new(BiometricOutcome.NotOn);
        }

        var stale = await TurnOffIfStaleAsync(vault);
        var name = RecordNameFor(fingerprint);
        if (ReadCurrent(fingerprint) is not { } record)
        {
            return stale is not null ? new(BiometricOutcome.TurnedOff, stale) : new(BiometricOutcome.NotOn);
        }

        if (_offThisSession.TryGetValue(name, out var reason))
        {
            return new(BiometricOutcome.Unavailable, reason);
        }

        if (!await _prompting.WaitAsync(0, CancellationToken.None))
        {
            return new(BiometricOutcome.Failed, AlreadyAsking);
        }

        byte[]? secret = null;
        try
        {
            var released = await Ask(() => authenticator.ReleaseAsync(record.KeyName, Convert.FromBase64String(record.Challenge), window, cancel), cancel);
            secret = released.Secret;
            if (cancel.IsCancellationRequested || released.Status == AuthenticatorStatus.Canceled)
            {
                return new(BiometricOutcome.Canceled);
            }

            if (released.Status == AuthenticatorStatus.KeyGone)
            {
                // Perhaps for good, perhaps not (a profile still loading, a remote session missed): stop offering it
                // until Orrery restarts, but keep the record and the key. Turning it off in Settings deletes both.
                var message = $"{released.Message ?? $"{Name} couldn't find Orrery's key."} Use the master password this time. "
                    + $"If this keeps happening, turn {Name} unlock off and on again in Settings.";
                _offThisSession[name] = message;
                Logger.LogWarning("{Authenticator} didn't find the vault's key; not offered again this session", Name);
                return new(BiometricOutcome.Unavailable, message);
            }

            if (released.Status != AuthenticatorStatus.Success || secret is null)
            {
                Logger.LogWarning("{Authenticator} unlock failed: {Message}", Name, released.Message);
                return new(BiometricOutcome.Failed, released.Message ?? $"{Name} didn't work. Use the master password.");
            }

            if (secret.Length < MinSecretSize)
            {
                Logger.LogWarning("{Authenticator} unlock failed: its secret is too short", Name);
                return new(BiometricOutcome.Failed, $"{Name} answered with something Orrery can't use. Use the master password.");
            }

            var dataKey = Unwrap(secret, record);
            if (dataKey is null)
            {
                RemoveStale(name, record, "the saved key doesn't decrypt");
                return new(BiometricOutcome.TurnedOff, $"The key saved for {Name} no longer matches, so {Name} unlock was turned off. "
                    + "Unlock with the master password, then turn it on again in Settings.");
            }

            bool opened;
            try
            {
                opened = await vault.UnlockWithDataKeyAsync(dataKey, record.Vault, cancel);
            }
            catch (OperationCanceledException)
            {
                return new(BiometricOutcome.Canceled);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }

            if (!opened)
            {
                RemoveStale(name, record, "the master password changed");
                return new(BiometricOutcome.TurnedOff, $"The master password was changed, so {Name} unlock was turned off. "
                    + "Unlock with the master password, then turn it on again in Settings.");
            }

            NotePlace(name, record, vault);
            Logger.LogInformation("Vault unlocked with {Authenticator}", Name);
            return new(BiometricOutcome.Done);
        }
        finally
        {
            _prompting.Release();
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Turns it off for <paramref name="vault"/>: deletes the saved record and the authenticator's key, along with
    /// any left from before a master password change, by the first test builds, or last used at the vault's path (which
    /// may be another vault's: the user turning it off is the only thing that removes those). True if anything was on.
    /// </summary>
    public async Task<bool> TurnOffAsync(VaultFileStore vault)
    {
        var fingerprint = FingerprintOf(vault);
        bool any = false;
        if (fingerprint is not null)
        {
            var name = RecordNameFor(fingerprint);
            any = records.Read(name) is not null;
            records.Delete(name);
            _offThisSession.TryRemove(name, out _);
            await DeleteKeyQuietlyAsync(KeyNameFor(fingerprint));
        }

        any |= await RemoveRelatedAsync(vault, fingerprint, deleteKeys: true, placeOnlyToo: true);
        await DeleteRetiredKeysAsync();
        if (any)
        {
            Logger.LogInformation("{Authenticator} unlock turned off", Name);
        }

        return any;
    }

    // Without the vault's own fingerprint its own record would look like one only found by path, so none are listed.
    List<Saved> Leftovers(VaultFileStore vault) =>
        FingerprintOf(vault) is { } fingerprint ? Related(vault, fingerprint).Where(r => r.PlaceOnly).ToList() : [];

    /// <summary>
    /// Records for this vault other than the current one: an earlier key block this run saw, the old path-keyed
    /// format, or (marked <see cref="Saved.PlaceOnly"/>) one last used at this vault's path.
    /// </summary>
    List<Saved> Related(VaultFileStore vault, string? fingerprint)
    {
        var retired = vault.RetiredKeyFingerprints();
        var place = PlaceOf(vault);
        var current = fingerprint is null ? null : RecordNameFor(fingerprint);
        var related = new List<Saved>();
        IReadOnlyList<string> names;
        try
        {
            names = records.List(RecordPrefix);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Logger.LogWarning("Couldn't list the saved {Authenticator} keys: {Message}", Name, e.Message);
            return related;
        }

        foreach (var name in names)
        {
            if (name == current || !name.StartsWith(RecordPrefix, StringComparison.Ordinal) || Parse(records.Read(name)) is not { } record)
            {
                continue;
            }

            bool pathKeyed = record.Version == PathKeyedVersion;
            bool thisFormat = record.Version == Version && record.Authenticator == authenticator.Id;
            bool known = pathKeyed
                ? name == RecordPrefix + place || record.Vault == fingerprint
                : thisFormat && (record.Vault == fingerprint || retired.Contains(record.Vault));

            // Only in the same place, with a key block this run never saw: perhaps this vault before another copy
            // changed its password, perhaps a different vault that was here before (and may be restored). Only the
            // user turning it off removes it.
            bool placeOnly = !known && thisFormat && record.Place == place;
            if (known || placeOnly)
            {
                // Only touch the authenticator key the record's own name implies, whatever the record says.
                var ownKey = pathKeyed ? KeyNamePrefix + name[RecordPrefix.Length..]
                    : record.Vault is not null && name == RecordNameFor(record.Vault) && name != current ? KeyNameFor(record.Vault) : null;
                related.Add(new Saved(name, record.KeyName == ownKey ? ownKey : null, record.Vault, pathKeyed, placeOnly));
            }
        }

        return related;
    }

    /// <param name="placeOnlyToo">Also records found only by the vault's path: only when the user turns it off.</param>
    async Task<bool> RemoveRelatedAsync(VaultFileStore vault, string? fingerprint, bool deleteKeys, bool placeOnlyToo)
    {
        var related = Related(vault, fingerprint).Where(r => placeOnlyToo || !r.PlaceOnly).ToList();
        foreach (var record in related)
        {
            records.Delete(record.Name);
            if (deleteKeys)
            {
                if (IsOurKeyName(record.KeyName)) await DeleteKeyQuietlyAsync(record.KeyName!);
            }
            else
            {
                Retire(record.KeyName);
            }
        }

        return related.Count > 0;
    }

    /// <summary>A record that can provably never open the vault again: delete it, but only note its key for later.</summary>
    void RemoveStale(string name, Record record, string reason)
    {
        records.Delete(name);
        Retire(record.KeyName);
        Logger.LogInformation("{Authenticator} unlock turned off: {Reason}", Name, reason);
    }

    /// <summary>Keeps the record's note of where the vault is up to date, so it's found if the vault's password changes there.</summary>
    void NotePlace(string name, Record record, VaultFileStore vault)
    {
        var place = PlaceOf(vault);
        if (record.Place == place)
        {
            return;
        }

        try
        {
            records.Write(name, JsonSerializer.SerializeToUtf8Bytes(record with { Place = place }, JsonOptions));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Logger.LogWarning("Couldn't update the saved {Authenticator} key: {Message}", Name, e.Message);
        }
    }

    /// <summary>Notes an authenticator key whose record is gone, to delete when the user next turns this on or off.</summary>
    void Retire(string? keyName)
    {
        if (!IsOurKeyName(keyName))
        {
            return;
        }

        try
        {
            var retired = RetiredKeys();
            if (!retired.Contains(keyName!))
            {
                retired.Add(keyName!);
                records.Write(RetiredKeysRecord, JsonSerializer.SerializeToUtf8Bytes(retired, JsonOptions));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Logger.LogWarning("Couldn't note an old {Authenticator} key for deletion: {Message}", Name, e.Message);
        }
    }

    List<string> RetiredKeys()
    {
        try
        {
            return records.Read(RetiredKeysRecord) is { } bytes ? JsonSerializer.Deserialize<List<string>>(bytes, JsonOptions) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <param name="keep">The key just made by turning it on, never deleted here.</param>
    async Task DeleteRetiredKeysAsync(string? keep = null)
    {
        var retired = RetiredKeys();
        foreach (var keyName in retired)
        {
            if (IsOurKeyName(keyName) && keyName != keep) await DeleteKeyQuietlyAsync(keyName);
        }

        if (retired.Count > 0)
        {
            records.Delete(RetiredKeysRecord);
        }
    }

    static bool IsOurKeyName(string? keyName) => keyName is not null && keyName.StartsWith(KeyNamePrefix, StringComparison.Ordinal);

    async Task DeleteKeyQuietlyAsync(string keyName)
    {
        try
        {
            await authenticator.DeleteAsync(keyName);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // With its record gone the key opens nothing; it's only left as clutter.
            Logger.LogWarning("Couldn't delete the {Authenticator} key: {Message}", Name, e.Message);
        }
    }

    /// <summary>The record for <paramref name="fingerprint"/>, if one is saved and fits. A damaged one is dropped.</summary>
    Record? ReadCurrent(string fingerprint)
    {
        var name = RecordNameFor(fingerprint);
        var bytes = records.Read(name);
        if (bytes is null)
        {
            return null;
        }

        if (VersionOf(bytes) is { } version && version != Version)
        {
            // Another version's record (a newer build's, say): this build can't use it, and it isn't this build's to
            // remove. It reads as off here.
            return null;
        }

        var record = Parse(bytes);
        if (record is { Format: Format, Version: Version, Nonce: not null, Ciphertext: not null }
            && record.Authenticator == authenticator.Id && record.KeyName == KeyNameFor(fingerprint) && record.Vault == fingerprint
            && DecodedLength(record.Challenge) == ChallengeSize)
        {
            return record;
        }

        // Damaged, or not for this vault: it can't open anything, so drop it. If it named this vault's own key, that's
        // deleted the next time this is turned on or off; a key named for another vault is left alone.
        Logger.LogWarning("The saved {Authenticator} key for the vault isn't readable; removed it", Name);
        records.Delete(name);
        if (record?.KeyName == KeyNameFor(fingerprint))
        {
            Retire(record.KeyName);
        }

        return null;
    }

    static Record? Parse(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Record>(bytes, JsonOptions) is { Format: Format } record ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The version a record of this format claims, read without the rest of it, so a newer layout is still recognized.</summary>
    static int? VersionOf(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == Format
                && root.TryGetProperty("version", out var version) && version.TryGetInt32(out int number) ? number : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    static string? FingerprintOf(VaultFileStore vault)
    {
        try
        {
            return vault.KeyFingerprint();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static int DecodedLength(string? base64)
    {
        var buffer = new byte[base64?.Length ?? 0];
        return base64 is not null && Convert.TryFromBase64String(base64, buffer, out int written) ? written : -1;
    }

    string AlreadyAsking => $"{Name} is already asking in another window. Answer it there, or use the master password.";

    /// <summary>Platform interop can fail in ways no status covers; report those as a failed attempt.</summary>
    async Task<AuthenticatorResult> Ask(Func<Task<AuthenticatorResult>> call, CancellationToken cancel)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return new(AuthenticatorStatus.Canceled);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Logger.LogWarning("{Authenticator} failed: {Error}", Name, e.Message);
            return new(AuthenticatorStatus.Failed, Message: $"{Name} didn't work: {e.Message}");
        }
    }

    static byte[]? Unwrap(byte[] secret, Record record)
    {
        try
        {
            using var key = WrappingKey(secret, record);
            return Aead.Decrypt(key, Convert.FromBase64String(record.Nonce), AssociatedData(record), Convert.FromBase64String(record.Ciphertext));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The data key in a saved record, given the authenticator's secret for it, without checking which vault it's
    /// for. Tests use it to show an old record really holds an old data key.
    /// </summary>
    internal static byte[]? UnwrapForTests(byte[] recordBytes, byte[] secret) => Parse(recordBytes) is { } record ? Unwrap(secret, record) : null;

    static Key WrappingKey(byte[] secret, Record record)
    {
        var bytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize, Convert.FromBase64String(record.Challenge),
            Encoding.UTF8.GetBytes($"{Format}|v{record.Version}|{record.Authenticator}|{record.KeyName}"));
        try
        {
            return Key.Import(Aead, bytes, KeyBlobFormat.RawSymmetricKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // The place is left out: it's only a hint for finding stale records, and is updated as the vault moves.
    static byte[] AssociatedData(Record record) =>
        Encoding.UTF8.GetBytes($"{Format}|v{record.Version}|{record.Authenticator}|{record.KeyName}|challenge={record.Challenge}|vault={record.Vault}");

    /// <summary>
    /// The vault's identity for records and keys: derived from its key block, so it's the same wherever the file is
    /// and new after every master password change.
    /// </summary>
    static string VaultId(string fingerprint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Format}|vault-id|{fingerprint}")))[..32].ToLowerInvariant();

    internal static string RecordNameFor(string fingerprint) => RecordPrefix + VaultId(fingerprint);

    internal static string KeyNameFor(string fingerprint) => KeyNamePrefix + VaultId(fingerprint);

    /// <summary>Where the vault file is, hashed: the name the first test builds gave the record, and the hint kept in each record now.</summary>
    internal static string PlaceOf(VaultFileStore vault)
    {
        var path = Path.GetFullPath(vault.FilePath);
        if (OperatingSystem.IsWindows())
        {
            path = path.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32].ToLowerInvariant();
    }

    /// <param name="PlaceOnly">Found only by the vault's path: reported, and only removed when the user turns it off.</param>
    sealed record Saved(string Name, string? KeyName, string? Vault, bool PathKeyed, bool PlaceOnly);

    sealed record Record(
        string Format,
        int Version,
        string Authenticator,
        string KeyName,
        string Challenge,
        string Vault,
        string Nonce,
        [property: JsonPropertyName("ct")] string Ciphertext,
        string? Place = null);
}
