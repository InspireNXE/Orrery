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

    [DllImport(CoreFoundation)]
    static extern void CFRelease(IntPtr handle);
}
