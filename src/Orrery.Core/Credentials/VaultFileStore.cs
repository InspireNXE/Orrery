using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NSec.Cryptography;
using Orrery.Core.Diagnostics;
using Orrery.Core.Storage;

namespace Orrery.Core.Credentials;

/// <summary>
/// An encrypted file of credentials, opened with a master password (design doc §3.3).
/// <para>
/// Master password → Argon2id → key-encryption key, which unwraps a random 256-bit data key; the data key
/// encrypts the credential list with XChaCha20-Poly1305. Changing the password also replaces the data key.
/// The KDF settings and salt are bound into both ciphertexts as associated data, so editing them in the
/// file makes decryption fail instead of weakening the vault.
/// </para>
/// </summary>
public sealed class VaultFileStore : ICredentialStore, IDisposable
{
    static readonly ILogger Logger = Log.For("Vault");

    const string Format = "orrery-vault";
    const int Version = 1;
    const string KdfName = "argon2id";
    const string CipherName = "xchacha20poly1305";
    const int SaltSize = 16;
    const int KeySize = 32;
    const int FreeAttempts = 5;
    const string Damaged = "The vault file is damaged.";

    // Argon2 limits accepted from a file: the defaults for new vaults sit well inside them, and parallelism is the one value written.
    const int MinMemoryKiB = 8 * 1024;
    const int MaxMemoryKiB = 1024 * 1024;
    const int MaxIterations = 100;

    static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string _path;
    readonly KdfSettings _kdfForNewVaults;
    readonly Timer _autoLockTimer;
    readonly object _lock = new();

    // Set while unlocked.
    Key? _dataKey;
    List<Credential>? _items;
    VaultFile? _file;
    DateTime _lastUsed;
    int _failedAttempts;
    readonly List<string> _retiredFingerprints = [];

    // The file as last read or written here, and its credentials then: what the next merge with another computer's
    // save (through a sync app sharing the data folder) measures both sides against. Set while unlocked.
    string? _knownText;
    List<Credential>? _knownItems;
    (long Length, DateTime Written) _knownStamp;
    string? _knownFingerprint;

    public VaultFileStore(string path, KdfSettings? kdf = null, TimeSpan? autoLockAfter = null)
    {
        _path = path;
        _kdfForNewVaults = kdf ?? KdfSettings.Default;
        AutoLockAfter = autoLockAfter;
        _autoLockTimer = new Timer(_ => AutoLockIfIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>How long the vault stays open while unused; null keeps it open until locked by hand.</summary>
    public TimeSpan? AutoLockAfter { get; set; }

    /// <summary>Argon2id cost. Stored in the file, so it can be raised later without breaking old vaults.</summary>
    public sealed record KdfSettings(int MemoryKiB, int Iterations)
    {
        public static readonly KdfSettings Default = new(MemoryKiB: 64 * 1024, Iterations: 3);
    }

    /// <summary>Raised when the vault locks itself after being idle.</summary>
    public event EventHandler? AutoLocked;

    public bool IsLocked
    {
        get { lock (_lock) return _dataKey is null; }
    }

    public bool Exists => File.Exists(_path);

    /// <summary>Open, and the file has changed since this computer last read or wrote it: another computer saved it.</summary>
    public bool ChangedElsewhere
    {
        get { lock (_lock) return _knownText is not null && Stamp() != _knownStamp; }
    }

    /// <summary>Where <c>vault.frv</c> is.</summary>
    public string FilePath => _path;

    public async Task UnlockAsync(string masterPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        await SlowDownAfterFailuresAsync();

        // Argon2id is deliberately slow; keep it off the caller's thread.
        await Task.Run(() =>
        {
            if (Exists)
            {
                Open(masterPassword);
            }
            else
            {
                Create(masterPassword);
            }
        });
    }

    void Create(string masterPassword)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var kdf = new KdfBlock(KdfName, _kdfForNewVaults.MemoryKiB, _kdfForNewVaults.Iterations, 1, Convert.ToBase64String(salt));

        var dataKeyBytes = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            using var kek = DeriveKeyEncryptionKey(masterPassword, kdf);
            var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
            var wrapped = Aead.Encrypt(kek, nonce, AssociatedData(kdf, "key"), dataKeyBytes);

            lock (_lock)
            {
                _file = new VaultFile(Format, Version, kdf, new CipherBlock(CipherName, Convert.ToBase64String(nonce), Convert.ToBase64String(wrapped)), null);
                _dataKey = Key.Import(Aead, dataKeyBytes, KeyBlobFormat.RawSymmetricKey);
                _items = [];
                Touch();
                Save();
            }

            Logger.LogInformation("Created a new vault at {Path}", _path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKeyBytes);
        }
    }

    void Open(string masterPassword)
    {
        var (file, text) = ReadFileAndText();
        var dataKeyBytes = UnwrapDataKey(file, masterPassword) ?? throw WrongPassword();
        try
        {
            OpenWith(file, dataKeyBytes, text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKeyBytes);
        }
    }

    // Slow down guessing after a few wrong passwords: 1s, 2s, 4s… up to 30s. No lockout, no wipe.
    async Task SlowDownAfterFailuresAsync()
    {
        int failures;
        lock (_lock) failures = _failedAttempts;
        if (failures >= FreeAttempts)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failures - FreeAttempts))));
        }
    }

    WrongVaultPasswordException WrongPassword()
    {
        int attempts;
        lock (_lock) attempts = ++_failedAttempts;
        Logger.LogWarning("Vault unlock failed: wrong master password ({Attempts} in a row)", attempts);
        return new WrongVaultPasswordException();
    }

    VaultFile ReadFile() => ReadFileAndText().File;

    (VaultFile File, string Text) ReadFileAndText()
    {
        var text = File.ReadAllText(_path);
        return (ParseFile(text), text);
    }

    VaultFile ParseFile(string text)
    {
        VaultFile? file;
        try
        {
            file = JsonSerializer.Deserialize<VaultFile>(text, JsonOptions);
        }
        catch (JsonException)
        {
            Logger.LogWarning("The vault at {Path} isn't readable JSON", _path);
            throw new InvalidDataException(Damaged);
        }

        if (file is null)
        {
            throw new InvalidDataException("The vault file is empty.");
        }

        if (file.Format != Format || file.Version != Version)
        {
            throw new InvalidDataException("This isn't an Orrery vault, or it was made by a newer version.");
        }

        // Everything in the header is checked before the password is tried, so a damaged or hostile file can't
        // crash the unlock or start a derivation that never finishes.
        ValidateHeader(file);
        if (file.Kdf.Alg != KdfName || file.WrappedKey.Alg != CipherName || file.Payload!.Alg != CipherName)
        {
            throw new InvalidDataException("This isn't an Orrery vault, or it was made by a newer version.");
        }

        return file;
    }

    /// <summary>The data key, or null if <paramref name="masterPassword"/> doesn't open <paramref name="file"/>.</summary>
    static byte[]? UnwrapDataKey(VaultFile file, string masterPassword)
    {
        using var kek = DeriveKeyEncryptionKey(masterPassword, file.Kdf);
        return Aead.Decrypt(kek, Convert.FromBase64String(file.WrappedKey.Nonce),
            AssociatedData(file.Kdf, "key"), Convert.FromBase64String(file.WrappedKey.Ciphertext));
    }

    // Decrypts the contents with the data key and makes this the open vault. The caller zeroes dataKeyBytes.
    void OpenWith(VaultFile file, byte[] dataKeyBytes, string text)
    {
        var dataKey = Key.Import(Aead, dataKeyBytes, KeyBlobFormat.RawSymmetricKey);
        List<Credential> items;
        try
        {
            items = Decrypt(file, dataKey);
        }
        catch
        {
            dataKey.Dispose();
            throw;
        }

        lock (_lock)
        {
            _dataKey?.Dispose();
            _dataKey = dataKey;
            _items = items;
            _file = file;
            _failedAttempts = 0;
            Known(text, items);
            Touch();
        }

        Logger.LogInformation("Vault unlocked, {Count} credentials", items.Count);
    }

    // ---- For biometric unlock (docs/design-biometric-unlock.md). The vault file itself never changes for it. ----

    /// <summary>
    /// Identifies the vault's current key block: the KDF settings, salt and wrapped data key. Ordinary saves leave it
    /// alone; every master password change makes a new one. Read from the file, so it needs no password.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is damaged or not a vault.</exception>
    internal string? KeyFingerprint() => Exists ? Fingerprint(ReadFile()) : null;

    /// <summary>
    /// The data key and key fingerprint, once <paramref name="masterPassword"/> is checked against the file. Wrong
    /// guesses count towards the same slow-down as the unlock window. The caller zeroes the key.
    /// </summary>
    /// <exception cref="WrongVaultPasswordException"></exception>
    internal async Task<(byte[] DataKey, string Fingerprint)> DataKeyForAsync(string masterPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);
        await SlowDownAfterFailuresAsync();
        return await Task.Run(() =>
        {
            var file = ReadFile();
            var dataKey = UnwrapDataKey(file, masterPassword) ?? throw WrongPassword();
            lock (_lock) _failedAttempts = 0;
            return (dataKey, Fingerprint(file));
        });
    }

    /// <summary>
    /// The key fingerprints this vault had before each master password change made through this object, oldest first.
    /// Biometric unlock uses them to find saved keys left over from before the change.
    /// </summary>
    internal IReadOnlyList<string> RetiredKeyFingerprints()
    {
        lock (_lock) return [.. _retiredFingerprints];
    }

    /// <summary>
    /// Opens the vault with a data key saved for biometric unlock. False, and still locked, when the file's key block
    /// isn't <paramref name="fingerprint"/> any more (the master password was changed). True without doing anything
    /// when the vault is already open. If <paramref name="cancel"/> fires, the vault is left locked.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> fired; the vault is locked.</exception>
    /// <exception cref="InvalidDataException">The key block matches but the contents don't decrypt with this key.</exception>
    internal Task<bool> UnlockWithDataKeyAsync(byte[] dataKey, string fingerprint, CancellationToken cancel = default) => Task.Run(() =>
    {
        var (file, text) = ReadFileAndText();
        if (Fingerprint(file) != fingerprint)
        {
            return false;
        }

        if (!IsLocked)
        {
            return true;
        }

        cancel.ThrowIfCancellationRequested();
        OpenWith(file, dataKey, text);
        if (cancel.IsCancellationRequested)
        {
            // Asked to stop while opening (the unlock window closed): don't leave it open behind the user's back.
            Lock();
            cancel.ThrowIfCancellationRequested();
        }

        return true;
    });

    static string Fingerprint(VaultFile file) => Convert.ToBase64String(SHA256.HashData(
        AssociatedData(file.Kdf, $"key|{file.WrappedKey.Nonce}|{file.WrappedKey.Ciphertext}")));

    /// <summary>
    /// Sets a new master password. A new random data key is wrapped under the new password (with a fresh salt
    /// and the current KDF settings) and the contents re-encrypted with it, so an old copy of the file opened
    /// with the old password can't read anything saved after the change. Saved credentials are untouched, and
    /// if the file can't be written the vault stays as it was. The current password is checked against the
    /// file, not taken on trust from an unlocked vault.
    /// </summary>
    /// <exception cref="WrongVaultPasswordException"><paramref name="currentPassword"/> doesn't open the vault.</exception>
    public async Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentPassword);
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        if (IsLocked)
        {
            await UnlockAsync(currentPassword);
        }

        await Task.Run(() =>
        {
            KdfBlock oldKdf;
            CipherBlock wrapped;
            lock (_lock)
            {
                if (_file is null || _dataKey is null)
                {
                    throw new VaultLockedException();
                }

                (oldKdf, wrapped) = (_file.Kdf, _file.WrappedKey);
            }

            byte[]? dataKeyBytes;
            using (var oldKek = DeriveKeyEncryptionKey(currentPassword, oldKdf))
            {
                dataKeyBytes = Aead.Decrypt(oldKek, Convert.FromBase64String(wrapped.Nonce),
                    AssociatedData(oldKdf, "key"), Convert.FromBase64String(wrapped.Ciphertext));
            }

            if (dataKeyBytes is null)
            {
                throw new WrongVaultPasswordException();
            }

            // Only needed to prove the current password; the contents move to a new data key.
            CryptographicOperations.ZeroMemory(dataKeyBytes);
            var newKeyBytes = RandomNumberGenerator.GetBytes(KeySize);
            try
            {
                var kdf = new KdfBlock(KdfName, _kdfForNewVaults.MemoryKiB, _kdfForNewVaults.Iterations, 1,
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize)));
                using var kek = DeriveKeyEncryptionKey(newPassword, kdf);
                var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
                var wrappedNew = Aead.Encrypt(kek, nonce, AssociatedData(kdf, "key"), newKeyBytes);

                lock (_lock)
                {
                    // What another computer saved since this one last read the file is taken in first, while the key
                    // it was saved with is the one in use: read with the new key, it didn't decrypt, counted as part
                    // written, and was saved over.
                    Unlocked();

                    // Save re-encrypts the contents under the new data key, bound to the new salt, so an old copy
                    // of the file and the old password can't read anything saved from now on.
                    var (previousFile, previousKey) = (_file!, _dataKey);
                    var newKey = Key.Import(Aead, newKeyBytes, KeyBlobFormat.RawSymmetricKey);
                    _file = previousFile with { Kdf = kdf, WrappedKey = new CipherBlock(CipherName, Convert.ToBase64String(nonce), Convert.ToBase64String(wrappedNew)) };
                    _dataKey = newKey;
                    try
                    {
                        Save();
                    }
                    catch
                    {
                        (_file, _dataKey) = (previousFile, previousKey);
                        newKey.Dispose();
                        throw;
                    }

                    previousKey?.Dispose();
                    _retiredFingerprints.Add(Fingerprint(previousFile));
                    Logger.LogInformation("Vault master password changed");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(newKeyBytes);
            }
        });
    }

    public void Lock()
    {
        bool wasOpen;
        lock (_lock)
        {
            wasOpen = _dataKey is not null;
            _dataKey?.Dispose();
            _dataKey = null;
            _items = null;
            _knownItems = null;
            _knownText = null;
        }

        if (wasOpen)
        {
            Logger.LogInformation("Vault locked");
        }
    }

    public Task<IReadOnlyList<CredentialSummary>> ListAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<CredentialSummary>>(
                Unlocked().Where(c => !Credential.ReservedIds.Contains(c.Id)).Select(c => c.Summary).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        }
    }

    public Task<Credential?> GetAsync(Guid id)
    {
        lock (_lock)
        {
            return Task.FromResult(Unlocked().FirstOrDefault(c => c.Id == id));
        }
    }

    public Task SaveAsync(Credential credential)
    {
        lock (_lock)
        {
            var items = Unlocked();
            items.RemoveAll(c => c.Id == credential.Id);
            items.Add(credential);
            Save();
        }

        return Task.CompletedTask;
    }

    public Task SaveAllAsync(IReadOnlyList<Credential> credentials)
    {
        lock (_lock)
        {
            var items = Unlocked();
            var before = items.ToList();
            var ids = credentials.Select(c => c.Id).ToHashSet();
            items.RemoveAll(c => ids.Contains(c.Id));
            items.AddRange(credentials);
            try
            {
                Save();
            }
            catch
            {
                items.Clear();
                items.AddRange(before);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id)
    {
        lock (_lock)
        {
            if (Unlocked().RemoveAll(c => c.Id == id) > 0)
            {
                Save();
            }
        }

        return Task.CompletedTask;
    }

    // Caller holds _lock.
    List<Credential> Unlocked()
    {
        if (_items is null)
        {
            throw new VaultLockedException();
        }

        TakeInChangesFromDisk();
        if (_items is null)
        {
            throw new VaultLockedException();
        }

        Touch();
        return _items;
    }

    // Caller holds _lock. Each save uses a fresh nonce; the wrapped key and KDF block are unchanged. Another computer's
    // save since this one last read the file is merged in first, so neither computer's credentials are lost.
    void Save()
    {
        TakeInChangesFromDisk();
        if (_items is null)
        {
            throw new VaultLockedException();
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_items, JsonOptions);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
            var ciphertext = Aead.Encrypt(_dataKey!, nonce, AssociatedData(_file!.Kdf, "payload"), plaintext);
            _file = _file with { Payload = new CipherBlock(CipherName, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext)) };
            var text = JsonSerializer.Serialize(_file, JsonOptions);
            AtomicFile.WriteAllText(_path, text);
            Known(text, _items!);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // Caller holds _lock.
    void Known(string text, List<Credential> items)
    {
        _knownText = text;
        _knownItems = [.. items];
        _knownStamp = Stamp();
        _knownFingerprint = Fingerprint(_file!);
    }

    (long, DateTime) Stamp()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (-1, default);
        }
    }

    static List<Credential> Decrypt(VaultFile file, Key dataKey)
    {
        var plaintext = Aead.Decrypt(dataKey, Convert.FromBase64String(file.Payload!.Nonce),
            AssociatedData(file.Kdf, "payload"), Convert.FromBase64String(file.Payload.Ciphertext))
            ?? throw new InvalidDataException("The vault's contents are damaged; the key was right but the data doesn't decrypt.");
        try
        {
            return JsonSerializer.Deserialize<List<Credential>>(plaintext, JsonOptions) ?? [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Caller holds _lock, with the vault open. When the file isn't as this computer last read or wrote it (another
    /// computer saved it through a sync app), its credentials are merged into the open ones by id. If the other computer
    /// changed the master password the file can't be read with this key: the vault locks, to be opened with the new one.
    /// A file that can't be read yet (part written) is tried again next time, or saved over on a save.
    /// </summary>
    void TakeInChangesFromDisk()
    {
        if (_knownText is null || _dataKey is null || _items is null || Stamp() == _knownStamp)
        {
            return;
        }

        string disk;
        VaultFile file;
        try
        {
            disk = File.ReadAllText(_path);
            if (disk == _knownText)
            {
                _knownStamp = Stamp();
                return;
            }

            file = ParseFile(disk);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Logger.LogWarning("The vault file changed on disk but can't be read yet: {Problem}", e.Message);
            return;
        }

        if (Fingerprint(file) != _knownFingerprint)
        {
            Logger.LogWarning("The vault's master password was changed on another computer; locking it to be opened with the new one");
            // Whoever asked unlocks it again, with the new password, and tries once more; that merges as usual.
            Lock();
            AutoLocked?.Invoke(this, EventArgs.Empty);
            return;
        }

        List<Credential> theirs;
        try
        {
            theirs = Decrypt(file, _dataKey);
        }
        catch (InvalidDataException e)
        {
            Logger.LogWarning("The vault file changed on disk but can't be read yet: {Problem}", e.Message);
            return;
        }

        var merged = MergeCredentials(_knownItems ?? [], _items, theirs, out int conflicts);
        _items.Clear();
        _items.AddRange(merged);
        Known(disk, theirs);
        Logger.LogInformation("Took in changes to the vault from another computer ({Count} credentials now, {Conflicts} changed on both; this computer's kept)",
            merged.Count, conflicts);
    }

    /// <summary>
    /// A three-way merge of credential lists by id. Added on either side: kept. Changed on one side: that side's.
    /// Changed on both: this computer's. Deleted on one side: deleted, unless the other side changed it.
    /// </summary>
    internal static List<Credential> MergeCredentials(IReadOnlyList<Credential> baseline, IReadOnlyList<Credential> mine, IReadOnlyList<Credential> theirs, out int conflicts)
    {
        static string Json(Credential c) => JsonSerializer.Serialize(c, JsonOptions);
        var b = baseline.ToDictionary(c => c.Id);
        var t = theirs.ToDictionary(c => c.Id);
        var result = new List<Credential>();
        conflicts = 0;
        foreach (var m in mine)
        {
            b.TryGetValue(m.Id, out var bc);
            if (t.TryGetValue(m.Id, out var tc))
            {
                bool mineChanged = bc is null || Json(bc) != Json(m);
                bool theirsChanged = bc is null || Json(bc) != Json(tc);
                if (mineChanged && theirsChanged && Json(m) != Json(tc))
                {
                    conflicts++;
                }

                result.Add(!mineChanged && theirsChanged ? tc : m);
            }
            else if (bc is null || Json(bc) != Json(m))
            {
                result.Add(m); // Added here, or changed here while deleted there.
            }
        }

        var mineIds = mine.Select(c => c.Id).ToHashSet();
        foreach (var tc in theirs.Where(c => !mineIds.Contains(c.Id)))
        {
            if (!b.TryGetValue(tc.Id, out var bc) || Json(bc) != Json(tc))
            {
                result.Add(tc); // Added there, or changed there while deleted here.
            }
        }

        return result;
    }

    static void ValidateHeader(VaultFile file)
    {
        bool valid = file.Kdf is { } kdf && file.WrappedKey is { } wrapped && file.Payload is { } payload
            && kdf.MemoryKiB is >= MinMemoryKiB and <= MaxMemoryKiB
            && kdf.Iterations is >= 1 and <= MaxIterations
            && kdf.Parallelism == 1
            && DecodedLength(kdf.Salt) == SaltSize
            && DecodedLength(wrapped.Nonce) == Aead.NonceSize
            && DecodedLength(payload.Nonce) == Aead.NonceSize
            && DecodedLength(wrapped.Ciphertext) >= Aead.TagSize
            && DecodedLength(payload.Ciphertext) >= Aead.TagSize;
        if (!valid)
        {
            throw new InvalidDataException(Damaged);
        }
    }

    /// <summary>The byte length of a base64 string, or -1 if it is missing or not valid base64.</summary>
    static int DecodedLength(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return -1;
        }

        var buffer = new byte[base64.Length];
        return Convert.TryFromBase64String(base64, buffer, out int written) ? written : -1;
    }

    void Touch() => _lastUsed = DateTime.UtcNow;

    void AutoLockIfIdle()
    {
        bool locked = false;
        lock (_lock)
        {
            if (_dataKey is not null && DateTime.UtcNow - _lastUsed > AutoLockAfter)
            {
                _dataKey.Dispose();
                _dataKey = null;
                _items = null;
                locked = true;
            }
        }

        if (locked)
        {
            Logger.LogInformation("Vault locked after {Minutes:0} minutes unused", AutoLockAfter?.TotalMinutes);
            AutoLocked?.Invoke(this, EventArgs.Empty);
        }
    }

    static Key DeriveKeyEncryptionKey(string masterPassword, KdfBlock kdf)
    {
        var parameters = new Argon2Parameters
        {
            DegreeOfParallelism = kdf.Parallelism,
            MemorySize = kdf.MemoryKiB,
            NumberOfPasses = kdf.Iterations,
        };

        var bytes = PasswordBasedKeyDerivationAlgorithm.Argon2id(parameters)
            .DeriveBytes(masterPassword, Convert.FromBase64String(kdf.Salt), KeySize);
        try
        {
            return Key.Import(Aead, bytes, KeyBlobFormat.RawSymmetricKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    static byte[] AssociatedData(KdfBlock kdf, string part) =>
        Encoding.UTF8.GetBytes($"{Format}|v{Version}|{kdf.Alg}|m={kdf.MemoryKiB}|t={kdf.Iterations}|p={kdf.Parallelism}|salt={kdf.Salt}|{part}");

    public void Dispose()
    {
        _autoLockTimer.Dispose();
        Lock();
    }

    sealed record VaultFile(string Format, int Version, KdfBlock Kdf, CipherBlock WrappedKey, CipherBlock? Payload);

    sealed record KdfBlock(string Alg, int MemoryKiB, int Iterations, int Parallelism, string Salt);

    sealed record CipherBlock(
        string Alg,
        string Nonce,
        [property: JsonPropertyName("ct")] string Ciphertext);
}
