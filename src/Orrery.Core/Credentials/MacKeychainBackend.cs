using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Orrery.Core.Credentials;

/// <summary>
/// The user's login keychain on macOS: generic passwords under the service "Orrery", one account per key.
/// Uses Security.framework's SecKeychain generic-password calls, which take plain C arguments.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainBackend : ISecretBackend
{
    const string Security = "/System/Library/Frameworks/Security.framework/Security";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const int ErrSecSuccess = 0, ErrSecItemNotFound = -25300;
    const uint Utf8 = 0x08000100;
    static readonly byte[] Service = Encoding.UTF8.GetBytes("Orrery");

    public string Name => "the macOS Keychain";

    public int? MaxSecretSize => null;

    public void Write(string key, byte[] secret)
    {
        var account = Encoding.UTF8.GetBytes(key);
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out _, IntPtr.Zero, out var item);
        if (status == ErrSecSuccess)
        {
            try
            {
                Check(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret), "update");
            }
            finally
            {
                CFRelease(item);
            }

            return;
        }

        if (status != ErrSecItemNotFound)
        {
            Check(status, "look up");
        }

        Check(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            (uint)secret.Length, secret, out var added), "save to");
        CFRelease(added);
    }

    public byte[]? Read(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);

        // Find writes the password's address into this slot; FreeContent releases it.
        var dataSlot = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(dataSlot, IntPtr.Zero);
        IntPtr item = IntPtr.Zero, data = IntPtr.Zero;
        try
        {
            int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                out uint length, dataSlot, out item);
            if (status == ErrSecItemNotFound)
            {
                return null;
            }

            Check(status, "read from");
            data = Marshal.ReadIntPtr(dataSlot);
            var secret = new byte[length];
            Marshal.Copy(data, secret, 0, (int)length);
            return secret;
        }
        finally
        {
            if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
            Marshal.FreeHGlobal(dataSlot);
        }
    }

    public void Delete(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out _, IntPtr.Zero, out var item);
        if (status == ErrSecItemNotFound)
        {
            return;
        }

        Check(status, "look up");
        try
        {
            Check(SecKeychainItemDelete(item), "delete from");
        }
        finally
        {
            CFRelease(item);
        }
    }

    /// <summary>
    /// Lists the accounts under service "Orrery" through SecItemCopyMatching, asking for attributes only, which reads no
    /// secret and so doesn't prompt. Not yet checked on a Mac.
    /// </summary>
    public IReadOnlyList<string> List(string prefix)
    {
        var library = NativeLibrary.Load(Security);
        IntPtr Key(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
        var cf = NativeLibrary.Load(CoreFoundation);
        var service = CFStringCreateWithCString(IntPtr.Zero, [.. Service, 0], Utf8);
        IntPtr query = IntPtr.Zero, found = IntPtr.Zero;
        try
        {
            IntPtr[] keys = [Key("kSecClass"), Key("kSecAttrService"), Key("kSecReturnAttributes"), Key("kSecMatchLimit")];
            IntPtr[] values = [Key("kSecClassGenericPassword"), service, Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue")), Key("kSecMatchLimitAll")];
            query = CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length,
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
            int status = SecItemCopyMatching(query, out found);
            if (status == ErrSecItemNotFound)
            {
                return [];
            }

            Check(status, "list");
            var names = new List<string>();
            var account = Key("kSecAttrAccount");
            for (nint i = 0, count = CFArrayGetCount(found); i < count; i++)
            {
                var attributes = CFArrayGetValueAtIndex(found, i);
                if (Text(CFDictionaryGetValue(attributes, account)) is { } name && name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    names.Add(name);
                }
            }

            return names;
        }
        finally
        {
            if (found != IntPtr.Zero) CFRelease(found);
            if (query != IntPtr.Zero) CFRelease(query);
            CFRelease(service);
        }
    }

    static string? Text(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero)
        {
            return null;
        }

        var buffer = new byte[CFStringGetMaximumSizeForEncoding(CFStringGetLength(cfString), Utf8) + 1];
        return CFStringGetCString(cfString, buffer, buffer.Length, Utf8) ? Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0)) : null;
    }

    static void Check(int status, string action)
    {
        if (status != ErrSecSuccess)
        {
            throw new IOException($"Couldn't {action} the macOS Keychain (error {status}).");
        }
    }

    [DllImport(Security)]
    static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        uint passwordLength, byte[] password, out IntPtr item);

    // passwordData is a void** the caller supplies; IntPtr.Zero when the password isn't wanted.
    [DllImport(Security)]
    static extern int SecKeychainFindGenericPassword(IntPtr keychains, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        out uint passwordLength, IntPtr passwordData, out IntPtr item);

    [DllImport(Security)]
    static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);

    [DllImport(Security)]
    static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

    [DllImport(Security)]
    static extern int SecKeychainItemDelete(IntPtr item);

    [DllImport(Security)]
    static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(CoreFoundation)]
    static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundation)]
    static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

    [DllImport(CoreFoundation)]
    static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundation)]
    static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] text, uint encoding);

    [DllImport(CoreFoundation)]
    static extern nint CFStringGetLength(IntPtr text);

    [DllImport(CoreFoundation)]
    static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [DllImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool CFStringGetCString(IntPtr text, byte[] buffer, nint size, uint encoding);

    [DllImport(CoreFoundation)]
    static extern void CFRelease(IntPtr handle);
}
