using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orrery.Core.Storage;

namespace Orrery.Core.Credentials;

/// <summary>
/// The operating system's own secret storage, reduced to what Orrery needs: one secret per key.
/// Implemented by Windows Credential Manager, the macOS Keychain and the Secret Service on Linux.
/// </summary>
public interface ISecretBackend
{
    /// <summary>What to call it in the app ("Windows Credential Manager", "the macOS Keychain").</summary>
    string Name { get; }

    /// <summary>Largest secret it takes, in bytes, or null for no practical limit.</summary>
    int? MaxSecretSize { get; }

    /// <summary>Stores or replaces the secret under <paramref name="key"/>.</summary>
    void Write(string key, byte[] secret);

    /// <summary>The secret, or null when there isn't one.</summary>
    byte[]? Read(string key);

    /// <summary>Removes the secret; a missing one is fine.</summary>
    void Delete(string key);

    /// <summary>
    /// The keys that start with <paramref name="prefix"/>, read without touching the secrets. Used by biometric unlock
    /// to find its own records; a backend that can't list returns none.
    /// </summary>
    IReadOnlyList<string> List(string prefix) => [];
}

/// <summary>
/// Installed mode: each secret lives in the OS keychain under <c>orrery:&lt;id&gt;</c>, protected by the
/// user's login. Names, usernames and the like live in <c>credentials.json</c> in the data folder, so the
/// list shows without touching the secrets.
/// </summary>
public sealed class KeychainCredentialStore(string indexPath, ISecretBackend backend) : ICredentialStore
{
    public const string KeyPrefix = "orrery:";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly object _lock = new();

    /// <summary>The keychain in use, for messages.</summary>
    public string BackendName => backend.Name;

    public bool IsLocked => false;
    public bool Exists => true;

    public Task UnlockAsync(string masterPassword) => Task.CompletedTask;

    public void Lock()
    {
    }

    public Task<IReadOnlyList<CredentialSummary>> ListAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<CredentialSummary>>(
                LoadIndex().Where(c => !Credential.ReservedIds.Contains(c.Id)).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        }
    }

    public Task<Credential?> GetAsync(Guid id)
    {
        lock (_lock)
        {
            var summary = LoadIndex().FirstOrDefault(c => c.Id == id);
            if (summary is null || ReadSecret(id) is not { } secret)
            {
                return Task.FromResult<Credential?>(null);
            }

            return Task.FromResult<Credential?>(new Credential(summary.Id, summary.Name, summary.Kind,
                summary.Username, summary.Domain, secret.Secret, secret.Passphrase, summary.Notes));
        }
    }

    public Task SaveAsync(Credential credential)
    {
        lock (_lock)
        {
            WriteSecret(credential.Id, new SecretBlob(credential.Secret, credential.Passphrase));
            var index = LoadIndex();
            index.RemoveAll(c => c.Id == credential.Id);
            index.Add(credential.Summary);
            SaveIndex(index);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes each secret, then the index once. If a write fails, the secrets this call added are removed
    /// again and the index is left alone; a secret it replaced for an existing id isn't put back.
    /// </summary>
    public Task SaveAllAsync(IReadOnlyList<Credential> credentials)
    {
        lock (_lock)
        {
            var index = LoadIndex();
            var known = index.Select(c => c.Id).ToHashSet();
            var added = new List<Guid>();
            try
            {
                foreach (var credential in credentials)
                {
                    WriteSecret(credential.Id, new SecretBlob(credential.Secret, credential.Passphrase));
                    if (!known.Contains(credential.Id))
                    {
                        added.Add(credential.Id);
                    }
                }

                var ids = credentials.Select(c => c.Id).ToHashSet();
                index.RemoveAll(c => ids.Contains(c.Id));
                index.AddRange(credentials.Select(c => c.Summary));
                SaveIndex(index);
            }
            catch
            {
                foreach (var id in added)
                {
                    backend.Delete(KeyPrefix + id);
                }

                throw;
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id)
    {
        lock (_lock)
        {
            var index = LoadIndex();
            index.RemoveAll(c => c.Id == id);
            SaveIndex(index);
            backend.Delete(KeyPrefix + id);
        }

        return Task.CompletedTask;
    }

    List<CredentialSummary> LoadIndex() =>
        File.Exists(indexPath)
            ? JsonSerializer.Deserialize<List<CredentialSummary>>(File.ReadAllText(indexPath), JsonOptions) ?? []
            : [];

    void SaveIndex(List<CredentialSummary> index) =>
        AtomicFile.WriteAllText(indexPath, JsonSerializer.Serialize(index, JsonOptions));

    void WriteSecret(Guid id, SecretBlob secret)
    {
        var blob = JsonSerializer.SerializeToUtf8Bytes(secret);
        try
        {
            if (backend.MaxSecretSize is { } max && blob.Length > max)
            {
                throw new InvalidOperationException(
                    $"This secret is too large for {backend.Name} ({max / 1024.0:0.#} KB). Store it in an encrypted vault instead.");
            }

            backend.Write(KeyPrefix + id, blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    SecretBlob? ReadSecret(Guid id)
    {
        if (backend.Read(KeyPrefix + id) is not { } blob)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SecretBlob>(blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    sealed record SecretBlob(string Secret, string? Passphrase);
}
