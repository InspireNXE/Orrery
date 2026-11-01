using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Orrery.Core.Diagnostics;

namespace Orrery.Core.Credentials;

/// <summary>What a platform authenticator did. <see cref="Message"/> is a plain sentence for the user, when there is one.</summary>
public enum AuthenticatorStatus
{
    Success,

    /// <summary>The user cancelled or chose the password instead. Nothing changes.</summary>
    Cancelled,

    /// <summary>The key is gone for good: Windows Hello was reset, or the enrolled fingers changed.</summary>
    KeyGone,

    /// <summary>It didn't work this time (sensor locked out, an error); trying again later may work.</summary>
    Failed,
}

public sealed record AuthenticatorResult(AuthenticatorStatus Status, byte[]? Secret = null, string? Message = null)
{
    public static AuthenticatorResult Released(byte[] secret) => new(AuthenticatorStatus.Success, secret);
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

    /// <summary>Makes a new key under <paramref name="keyName"/>, replacing any old one, and returns its secret for <paramref name="challenge"/>. Prompts.</summary>
    /// <param name="window">The window to show the prompt over, or zero.</param>
    Task<AuthenticatorResult> CreateAsync(string keyName, byte[] challenge, nint window);

    /// <summary>The secret <see cref="CreateAsync"/> returned for the same key and challenge. Prompts.</summary>
    Task<AuthenticatorResult> ReleaseAsync(string keyName, byte[] challenge, nint window);

    /// <summary>Deletes the key; a missing one is fine.</summary>
    Task DeleteAsync(string keyName);
}

public enum BiometricOutcome
{
    /// <summary>Turned on, or the vault is open.</summary>
    Done,

    /// <summary>The user cancelled the prompt. Nothing changed.</summary>
    Cancelled,

    /// <summary>It's been turned off and its key deleted; <see cref="BiometricResult.Message"/> says why.</summary>
    TurnedOff,

    /// <summary>It didn't work this time, and is still on (or still off, when turning on).</summary>
    Failed,

    /// <summary>It isn't on for this vault.</summary>
    NotOn,
}

public sealed record BiometricResult(BiometricOutcome Outcome, string? Message = null);

/// <summary>
/// Unlocks a <see cref="VaultFileStore"/> with Windows Hello or Touch ID (docs/design-biometric-unlock.md). The vault's
/// data key is wrapped a second time, under a key derived from a secret only the platform authenticator releases, and
/// that wrap is kept in <paramref name="records"/> on this computer, never in the data folder. The vault file doesn't
/// change, and the master password keeps working.
/// </summary>
public sealed class BiometricUnlock(IPlatformAuthenticator authenticator, ISecretBackend records)
{
    static readonly ILogger Logger = Log.For("Vault");
    static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    const string Format = "orrery-biometric";
    const int Version = 1;
    const int ChallengeSize = 32;
    const int KeySize = 32;
    public const string RecordPrefix = "orrery:biometric:";

    static readonly Lazy<BiometricUnlock?> ThisSystem = new(Create);

    /// <summary>Windows Hello on Windows, Touch ID on macOS; null on Linux, which has no authenticator that holds a key.</summary>
    public static BiometricUnlock? ForThisSystem() => ThisSystem.Value;

    static BiometricUnlock? Create()
    {
        if (OperatingSystem.IsWindows()) return new(new WindowsHelloAuthenticator(), new WindowsCredentialBackend());
        if (OperatingSystem.IsMacOS()) return new(new MacTouchIdAuthenticator(), new MacKeychainBackend());
        return null;
    }

    public string Name => authenticator.Name;

    /// <inheritdoc cref="IPlatformAuthenticator.CheckAvailableAsync"/>
    public async Task<string?> CheckAvailableAsync()
    {
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

    /// <summary>True when a key is saved for this vault. It may still turn out stale; see <see cref="TurnOffIfStaleAsync"/>.</summary>
    public bool IsOn(VaultFileStore vault) => ReadRecord(vault) is not null;

    /// <summary>
    /// Turns it on for <paramref name="vault"/>: checks the master password against the file, makes a new key (one
    /// prompt) and saves the wrapped data key. Replaces any earlier key for this vault.
    /// </summary>
    /// <exception cref="WrongVaultPasswordException"><paramref name="masterPassword"/> doesn't open the vault.</exception>
    public async Task<BiometricResult> TurnOnAsync(VaultFileStore vault, string masterPassword, nint window = 0)
    {
        var (dataKey, fingerprint) = await vault.DataKeyForAsync(masterPassword);
        byte[]? secret = null;
        try
        {
            var name = KeyName(vault);
            var challenge = RandomNumberGenerator.GetBytes(ChallengeSize);
            var created = await Ask(() => authenticator.CreateAsync(name, challenge, window));
            secret = created.Secret;
            if (created.Status != AuthenticatorStatus.Success || secret is null)
            {
                Logger.LogInformation("{Authenticator} unlock wasn't turned on: {Status}", Name, created.Status);
                return created.Status == AuthenticatorStatus.Cancelled
                    ? new(BiometricOutcome.Cancelled)
                    : new(BiometricOutcome.Failed, created.Message ?? $"{Name} couldn't make a key.");
            }

            var record = new Record(Format, Version, authenticator.Id, name, Convert.ToBase64String(challenge), fingerprint, null!, null!);
            using var key = WrappingKey(secret, record);
            var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
            var wrapped = Aead.Encrypt(key, nonce, AssociatedData(record), dataKey);
            records.Write(RecordName(vault), JsonSerializer.SerializeToUtf8Bytes(
                record with { Nonce = Convert.ToBase64String(nonce), Ciphertext = Convert.ToBase64String(wrapped) }, JsonOptions));
            Logger.LogInformation("{Authenticator} unlock turned on for the vault", Name);
            return new(BiometricOutcome.Done);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Turns it off when the master password has changed since it was turned on (perhaps by another copy of Orrery).
    /// Never prompts. Returns the message to show, or null if nothing changed.
    /// </summary>
    public async Task<string?> TurnOffIfStaleAsync(VaultFileStore vault)
    {
        if (ReadRecord(vault) is not { } record)
        {
            return null;
        }

        string? fingerprint;
        try
        {
            fingerprint = vault.KeyFingerprint();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null; // Can't tell; leave it for the unlock window to report the damaged file.
        }

        if (fingerprint == record.Vault)
        {
            return null;
        }

        await TurnOffAsync(vault, "the master password changed");
        return $"The master password was changed, so {Name} unlock was turned off. Unlock with the master password, then turn it on again in Settings.";
    }

    /// <summary>
    /// Opens the vault with the saved key (one prompt). Turns itself off, and says why, when the key can never work
    /// again: the master password changed, or the authenticator lost or replaced the key.
    /// </summary>
    public async Task<BiometricResult> UnlockAsync(VaultFileStore vault, nint window = 0)
    {
        if (ReadRecord(vault) is not { } record)
        {
            return new(BiometricOutcome.NotOn);
        }

        if (await TurnOffIfStaleAsync(vault) is { } stale)
        {
            return new(BiometricOutcome.TurnedOff, stale);
        }

        var released = await Ask(() => authenticator.ReleaseAsync(record.KeyName, Convert.FromBase64String(record.Challenge), window));
        var secret = released.Secret;
        try
        {
            if (released.Status == AuthenticatorStatus.Cancelled)
            {
                return new(BiometricOutcome.Cancelled);
            }

            if (released.Status == AuthenticatorStatus.KeyGone)
            {
                await TurnOffAsync(vault, "the key is gone");
                return new(BiometricOutcome.TurnedOff, $"{released.Message ?? $"{Name} no longer has Orrery's key."} {Name} unlock was turned off. "
                    + "Unlock with the master password, then turn it on again in Settings.");
            }

            if (released.Status != AuthenticatorStatus.Success || secret is null)
            {
                Logger.LogWarning("{Authenticator} unlock failed: {Message}", Name, released.Message);
                return new(BiometricOutcome.Failed, released.Message ?? $"{Name} didn't work. Use the master password.");
            }

            var dataKey = Unwrap(secret, record);
            if (dataKey is null)
            {
                await TurnOffAsync(vault, "the saved key doesn't decrypt");
                return new(BiometricOutcome.TurnedOff, $"The key saved for {Name} no longer matches, so {Name} unlock was turned off. "
                    + "Unlock with the master password, then turn it on again in Settings.");
            }

            try
            {
                if (!await vault.UnlockWithDataKeyAsync(dataKey, record.Vault))
                {
                    await TurnOffAsync(vault, "the master password changed");
                    return new(BiometricOutcome.TurnedOff, $"The master password was changed, so {Name} unlock was turned off. "
                        + "Unlock with the master password, then turn it on again in Settings.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }

            Logger.LogInformation("Vault unlocked with {Authenticator}", Name);
            return new(BiometricOutcome.Done);
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Turns it off for <paramref name="vault"/>: deletes the saved key here and the authenticator's key.</summary>
    public Task TurnOffAsync(VaultFileStore vault) => TurnOffAsync(vault, "turned off in Settings");

    async Task TurnOffAsync(VaultFileStore vault, string reason)
    {
        records.Delete(RecordName(vault));
        try
        {
            await authenticator.DeleteAsync(KeyName(vault));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The saved key is gone, so the authenticator's key opens nothing; it's only left as clutter.
            Logger.LogWarning("Couldn't delete the {Authenticator} key: {Message}", Name, e.Message);
        }

        Logger.LogInformation("{Authenticator} unlock turned off: {Reason}", Name, reason);
    }

    Record? ReadRecord(VaultFileStore vault)
    {
        var bytes = records.Read(RecordName(vault));
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var record = JsonSerializer.Deserialize<Record>(bytes, JsonOptions);
            if (record is { Format: Format, Version: Version, Vault: not null, Nonce: not null, Ciphertext: not null }
                && record.Authenticator == authenticator.Id && record.KeyName == KeyName(vault) && DecodedLength(record.Challenge) == ChallengeSize)
            {
                return record;
            }
        }
        catch (JsonException)
        {
        }

        // Damaged, or not for this vault: it can't open anything, so drop it. The authenticator's key is replaced the
        // next time this is turned on.
        Logger.LogWarning("The saved {Authenticator} key for the vault isn't readable; removed it", Name);
        records.Delete(RecordName(vault));
        return null;
    }

    static int DecodedLength(string? base64)
    {
        var buffer = new byte[base64?.Length ?? 0];
        return base64 is not null && Convert.TryFromBase64String(base64, buffer, out int written) ? written : -1;
    }

    /// <summary>Platform interop can fail in ways no status covers; report those as a failed attempt.</summary>
    async Task<AuthenticatorResult> Ask(Func<Task<AuthenticatorResult>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Logger.LogWarning("{Authenticator} failed: {Error}", Name, e.Message);
            return new(AuthenticatorStatus.Failed, Message: $"{Name} didn't work: {e.Message}");
        }
    }

    byte[]? Unwrap(byte[] secret, Record record)
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

    static Key WrappingKey(byte[] secret, Record record)
    {
        var bytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeySize, Convert.FromBase64String(record.Challenge),
            Encoding.UTF8.GetBytes($"{Format}|v{Version}|{record.Authenticator}|{record.KeyName}"));
        try
        {
            return Key.Import(Aead, bytes, KeyBlobFormat.RawSymmetricKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    static byte[] AssociatedData(Record record) =>
        Encoding.UTF8.GetBytes($"{Format}|v{Version}|{record.Authenticator}|{record.KeyName}|challenge={record.Challenge}|vault={record.Vault}");

    /// <summary>One per vault file, so an installed vault and a portable one don't share a key.</summary>
    static string VaultId(VaultFileStore vault)
    {
        var path = Path.GetFullPath(vault.FilePath);
        if (OperatingSystem.IsWindows())
        {
            path = path.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32].ToLowerInvariant();
    }

    static string RecordName(VaultFileStore vault) => RecordPrefix + VaultId(vault);

    static string KeyName(VaultFileStore vault) => "Orrery vault " + VaultId(vault);

    sealed record Record(
        string Format,
        int Version,
        string Authenticator,
        string KeyName,
        string Challenge,
        string Vault,
        string Nonce,
        [property: System.Text.Json.Serialization.JsonPropertyName("ct")] string Ciphertext);
}
