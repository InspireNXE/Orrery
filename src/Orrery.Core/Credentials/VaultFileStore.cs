using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;
using Orrery.Core.Storage;

namespace Orrery.Core.Credentials;

/// <summary>
/// An encrypted file of credentials, opened with a master password (design doc §3.3).
/// <para>
/// Master password → Argon2id → key-encryption key, which unwraps a random 256-bit data key; the data key
/// encrypts the credential list with XChaCha20-Poly1305. Changing the password only re-wraps the data key.
/// The KDF settings and salt are bound into both ciphertexts as associated data, so editing them in the
/// file makes decryption fail instead of weakening the vault.
/// </para>
/// </summary>
public sealed class VaultFileStore : ICredentialStore, IDisposable
{
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

    public async Task UnlockAsync(string masterPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterPassword);

        // Slow down guessing after a few wrong passwords: 1s, 2s, 4s… up to 30s. No lockout, no wipe.
        int failures;
        lock (_lock) failures = _failedAttempts;
        if (failures >= FreeAttempts)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failures - FreeAttempts))));
        }

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
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKeyBytes);
        }
    }

    void Open(string masterPassword)
    {
        VaultFile? file;
        try
        {
            file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(_path), JsonOptions);
        }
        catch (JsonException)
        {
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

        byte[]? dataKeyBytes;
        using (var kek = DeriveKeyEncryptionKey(masterPassword, file.Kdf))
        {
            dataKeyBytes = Aead.Decrypt(kek, Convert.FromBase64String(file.WrappedKey.Nonce),
                AssociatedData(file.Kdf, "key"), Convert.FromBase64String(file.WrappedKey.Ciphertext));
        }

        if (dataKeyBytes is null)
        {
            lock (_lock) _failedAttempts++;
            throw new WrongVaultPasswordException();
        }

        try
        {
            var dataKey = Key.Import(Aead, dataKeyBytes, KeyBlobFormat.RawSymmetricKey);
            var plaintext = Aead.Decrypt(dataKey, Convert.FromBase64String(file.Payload.Nonce),
                AssociatedData(file.Kdf, "payload"), Convert.FromBase64String(file.Payload.Ciphertext));
            if (plaintext is null)
            {
                dataKey.Dispose();
                throw new InvalidDataException("The vault's contents are damaged; the password was right but the data doesn't decrypt.");
            }

            var items = JsonSerializer.Deserialize<List<Credential>>(plaintext, JsonOptions) ?? [];
            CryptographicOperations.ZeroMemory(plaintext);

            lock (_lock)
            {
                _dataKey?.Dispose();
                _dataKey = dataKey;
                _items = items;
                _file = file;
                _failedAttempts = 0;
                Touch();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKeyBytes);
        }
    }

    /// <summary>
    /// Sets a new master password. Only the data key is re-wrapped (under a fresh salt, with the current KDF
    /// settings) and the contents re-encrypted with the same data key, so saved credentials are untouched.
    /// The current password is checked against the file, not taken on trust from an unlocked vault.
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

            try
            {
                var kdf = new KdfBlock(KdfName, _kdfForNewVaults.MemoryKiB, _kdfForNewVaults.Iterations, 1,
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize)));
                using var kek = DeriveKeyEncryptionKey(newPassword, kdf);
                var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
                var rewrapped = Aead.Encrypt(kek, nonce, AssociatedData(kdf, "key"), dataKeyBytes);

                lock (_lock)
                {
                    // Save re-encrypts the contents, which are bound to the new salt.
                    var previous = _file!;
                    _file = previous with { Kdf = kdf, WrappedKey = new CipherBlock(CipherName, Convert.ToBase64String(nonce), Convert.ToBase64String(rewrapped)) };
                    try
                    {
                        Unlocked();
                        Save();
                    }
                    catch
                    {
                        _file = previous;
                        throw;
                    }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKeyBytes);
            }
        });
    }

    public void Lock()
    {
        lock (_lock)
        {
            _dataKey?.Dispose();
            _dataKey = null;
            _items = null;
        }
    }

    public Task<IReadOnlyList<CredentialSummary>> ListAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<CredentialSummary>>(
                Unlocked().Select(c => c.Summary).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
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

        Touch();
        return _items;
    }

    // Caller holds _lock. Each save uses a fresh nonce; the wrapped key and KDF block are unchanged.
    void Save()
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_items, JsonOptions);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(Aead.NonceSize);
            var ciphertext = Aead.Encrypt(_dataKey!, nonce, AssociatedData(_file!.Kdf, "payload"), plaintext);
            _file = _file with { Payload = new CipherBlock(CipherName, Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext)) };
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_file, JsonOptions));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
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
