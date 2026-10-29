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

    static readonly AeadAlgorithm Aead = AeadAlgorithm.XChaCha20Poly1305;
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly string _path;
    readonly KdfSettings _kdfForNewVaults;
    readonly TimeSpan? _autoLockAfter;
    readonly Timer? _autoLockTimer;
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
        _autoLockAfter = autoLockAfter;
        if (autoLockAfter is not null)
        {
            _autoLockTimer = new Timer(_ => AutoLockIfIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

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
        var file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(_path), JsonOptions)
            ?? throw new InvalidDataException("The vault file is empty.");

        if (file.Format != Format || file.Version != Version || file.Kdf.Alg != KdfName
            || file.WrappedKey.Alg != CipherName || file.Payload?.Alg != CipherName)
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

    void Touch() => _lastUsed = DateTime.UtcNow;

    void AutoLockIfIdle()
    {
        bool locked = false;
        lock (_lock)
        {
            if (_dataKey is not null && DateTime.UtcNow - _lastUsed > _autoLockAfter)
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
        _autoLockTimer?.Dispose();
        Lock();
    }

    sealed record VaultFile(string Format, int Version, KdfBlock Kdf, CipherBlock WrappedKey, CipherBlock? Payload);

    sealed record KdfBlock(string Alg, int MemoryKiB, int Iterations, int Parallelism, string Salt);

    sealed record CipherBlock(
        string Alg,
        string Nonce,
        [property: JsonPropertyName("ct")] string Ciphertext);
}
