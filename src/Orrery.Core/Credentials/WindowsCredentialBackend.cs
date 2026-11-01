using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Core.Credentials;

/// <summary>Windows Credential Manager: generic credentials, protected by the user's Windows login.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialBackend : ISecretBackend
{
    const int CredTypeGeneric = 1;
    const int CredPersistLocalMachine = 2;

    public string Name => "Windows Credential Manager";

    // CRED_MAX_CREDENTIAL_BLOB_SIZE: 5 * 512 bytes.
    public int? MaxSecretSize => 2560;

    public void Write(string key, byte[] secret)
    {
        var handle = GCHandle.Alloc(secret, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = key,
                Comment = "Orrery saved credential",
                CredentialBlobSize = secret.Length,
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

    public byte[]? Read(string key)
    {
        if (!CredRead(key, CredTypeGeneric, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return blob;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Delete(string key) => CredDelete(key, CredTypeGeneric, 0); // Missing is fine.

    public IReadOnlyList<string> List(string prefix)
    {
        // CredEnumerateW treats a trailing "*" as a prefix match. It hands back each blob too; only the names are read.
        if (!CredEnumerate(prefix + "*", 0, out int count, out var list))
        {
            return []; // ERROR_NOT_FOUND when there are none.
        }

        try
        {
            var names = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                var credential = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                if (credential == IntPtr.Zero)
                {
                    continue;
                }

                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(credential, Marshal.OffsetOf<NativeCredential>(nameof(NativeCredential.TargetName)).ToInt32()));
                if (name is not null && name.StartsWith(prefix, StringComparison.Ordinal)
                    && Marshal.ReadInt32(credential, Marshal.OffsetOf<NativeCredential>(nameof(NativeCredential.Type)).ToInt32()) == CredTypeGeneric)
                {
                    names.Add(name);
                }
            }

            return names;
        }
        finally
        {
            CredFree(list);
        }
    }

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

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);
}
