using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Orrery.Protocols.Ssh.Agent;

/// <summary>
/// Pageant 0.75 and later also listen on a named pipe and speak the plain agent protocol on it, as the OpenSSH
/// agent does: no window message and no shared memory. The pipe is <c>\\.\pipe\pageant.&lt;user&gt;.&lt;hash&gt;</c>,
/// where the hash is SHA-256 over "Pageant" encrypted with CryptProtectMemory's per-user key, so only this
/// user's processes can work out the name. This is PuTTY's <c>agent_named_pipe_name</c>.
/// </summary>
[SupportedOSPlatform("windows")]
static class PageantPipe
{
    const uint CRYPTPROTECTMEMORY_BLOCK_SIZE = 16, CRYPTPROTECTMEMORY_CROSS_PROCESS = 0x1;
    const int NameUserPrincipal = 8;

    /// <summary>The pipe's name, without <c>\\.\pipe\</c>.</summary>
    public static string Name() => $"pageant.{UserName()}.{ObfuscatedSuffix("Pageant")}";

    /// <summary>As PuTTY's <c>get_username</c>: the user principal name up to its '@', else the logon name.</summary>
    static string UserName()
    {
        int length = 0;
        GetUserNameEx(NameUserPrincipal, null, ref length);
        if (length > 0)
        {
            var name = new StringBuilder(length);
            if (GetUserNameEx(NameUserPrincipal, name, ref length))
            {
                var principal = name.ToString();
                int at = principal.IndexOf('@');
                return at >= 0 ? principal[..at] : principal;
            }
        }

        return Environment.UserName;
    }

    /// <summary>PuTTY's <c>capi_obfuscate_string</c>: the text and a NUL, padded to a block, encrypted, then hashed as an SSH string.</summary>
    static string ObfuscatedSuffix(string text)
    {
        int length = Encoding.ASCII.GetByteCount(text) + 1;
        length = (int)((length + CRYPTPROTECTMEMORY_BLOCK_SIZE - 1) / CRYPTPROTECTMEMORY_BLOCK_SIZE * CRYPTPROTECTMEMORY_BLOCK_SIZE);
        var data = new byte[4 + length];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)length);
        Encoding.ASCII.GetBytes(text, data.AsSpan(4));

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            if (!CryptProtectMemory(handle.AddrOfPinnedObject() + 4, (uint)length, CRYPTPROTECTMEMORY_CROSS_PROCESS))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't work out Pageant's pipe name.");
            }
        }
        finally
        {
            handle.Free();
        }

        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptProtectMemory(IntPtr data, uint length, uint flags);

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserNameExW")]
    static extern bool GetUserNameEx(int format, StringBuilder? name, ref int length);
}
