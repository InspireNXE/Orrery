using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orrery.Core.Storage;

namespace Orrery.Core.Credentials;

/// <summary>
/// Installed mode on Windows: each secret is a Windows Credential Manager entry named
/// <c>orrery:&lt;id&gt;</c>, protected by the user's Windows login. Names, usernames and the like live in
/// <c>credentials.json</c> in the data folder, so the list shows without touching the secrets.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore(string indexPath) : ICredentialStore
{
    const string TargetPrefix = "orrery:";

    // CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 * 512 bytes.
    const int MaxBlobSize = 2560;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    readonly object _lock = new();

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
                LoadIndex().OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
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

    public Task DeleteAsync(Guid id)
    {
        lock (_lock)
        {
            var index = LoadIndex();
            index.RemoveAll(c => c.Id == id);
            SaveIndex(index);
            CredDelete(TargetPrefix + id, CredTypeGeneric, 0); // Missing is fine.
        }

        return Task.CompletedTask;
    }

    List<CredentialSummary> LoadIndex() =>
        File.Exists(indexPath)
            ? JsonSerializer.Deserialize<List<CredentialSummary>>(File.ReadAllText(indexPath), JsonOptions) ?? []
            : [];

    void SaveIndex(List<CredentialSummary> index) =>
        AtomicFile.WriteAllText(indexPath, JsonSerializer.Serialize(index, JsonOptions));

    static void WriteSecret(Guid id, SecretBlob secret)
    {
        var blob = JsonSerializer.SerializeToUtf8Bytes(secret);
        try
        {
            if (blob.Length > MaxBlobSize)
            {
                throw new InvalidOperationException(
                    "This secret is too large for Windows Credential Manager (2.5 KB). Store it in an encrypted vault instead.");
            }

            var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
            try
            {
                var credential = new NativeCredential
                {
                    Type = CredTypeGeneric,
                    TargetName = TargetPrefix + id,
                    Comment = "Orrery saved credential",
                    CredentialBlobSize = blob.Length,
                    CredentialBlob = handle.AddrOfPinnedObject(),
                    Persist = CredPersistLocalMachine,
                    UserName = "orrery",
                };

                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't save to Windows Credential Manager.");
                }
            }
            finally
            {
                handle.Free();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    static SecretBlob? ReadSecret(Guid id)
    {
        if (!CredRead(TargetPrefix + id, CredTypeGeneric, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            try
            {
                return JsonSerializer.Deserialize<SecretBlob>(blob);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(blob);
            }
        }
        finally
        {
            CredFree(pointer);
        }
    }

    sealed record SecretBlob(string Secret, string? Passphrase);

    const int CredTypeGeneric = 1;
    const int CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NativeCredential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
