using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Orrery.Core.Credentials;

/// <summary>
/// Touch ID through the data-protection keychain: the secret is 32 random bytes in a generic-password item whose
/// access control is <c>kSecAccessControlBiometryCurrentSet</c>, so the keychain hands it over only after Touch ID
/// matches a finger enrolled now (the Secure Enclave enforces it), and adding or removing a finger makes the item
/// unreadable. See docs/design-biometric-unlock.md §2.2.
/// <para>
/// Not yet run on a Mac. The data-protection keychain only serves apps signed with a keychain-access-groups
/// entitlement, and Orrery's macOS builds aren't signed yet, so until they are this reports that Touch ID unlock needs
/// a signed copy.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacTouchIdAuthenticator : IPlatformAuthenticator
{
    const string Security = "/System/Library/Frameworks/Security.framework/Security";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const string LocalAuthentication = "/System/Library/Frameworks/LocalAuthentication.framework/LocalAuthentication";
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    const string Service = "Orrery biometric unlock";
    const int SecretSize = 32;

    const int ErrSecSuccess = 0, ErrSecUserCanceled = -128, ErrSecAuthFailed = -25293, ErrSecItemNotFound = -25300,
        ErrSecInteractionNotAllowed = -25308, ErrSecMissingEntitlement = -34018;
    const nuint BiometryCurrentSet = 1 << 3; // kSecAccessControlBiometryCurrentSet
    const long BiometricsPolicy = 1; // LAPolicyDeviceOwnerAuthenticationWithBiometrics
    const long LaErrorPasscodeNotSet = -5, LaErrorBiometryNotAvailable = -6, LaErrorBiometryNotEnrolled = -7;
    const uint Utf8 = 0x08000100;

    const string NeedsSigning = "Touch ID unlock needs a signed copy of Orrery, and this one isn't signed yet.";

    public string Name => "Touch ID";

    public string Id => "touch-id";

    public Task<string?> CheckAvailableAsync() => Task.Run<string?>(() =>
    {
        if (BiometryProblem() is { } problem)
        {
            return problem;
        }

        // Looks for Orrery's item without asking for a finger: an unsigned app is refused before the search.
        using var query = Query(null);
        query.Add("kSecUseAuthenticationUI", Constant(Security, "kSecUseAuthenticationUIFail"));
        int status = SecItemCopyMatching(query.Create(), out var found);
        Release(found);
        return status == ErrSecMissingEntitlement ? NeedsSigning : null;
    });

    public Task<AuthenticatorResult> CreateAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel) => Task.Run(() =>
    {
        Delete(keyName);
        var secret = RandomNumberGenerator.GetBytes(SecretSize);
        IntPtr access = SecAccessControlCreateWithFlags(IntPtr.Zero, Constant(Security, "kSecAttrAccessibleWhenPasscodeSetThisDeviceOnly"),
            BiometryCurrentSet, IntPtr.Zero);
        if (access == IntPtr.Zero)
        {
            return new AuthenticatorResult(AuthenticatorStatus.Failed, Message: "Touch ID couldn't protect a key on this Mac.");
        }

        int status;
        using (var item = Query(keyName))
        {
            item.Add("kSecAttrAccessControl", access);
            item.AddData("kSecValueData", secret);
            status = SecItemAdd(item.Create(), IntPtr.Zero);
        }

        Release(access);
        CryptographicOperations.ZeroMemory(secret);
        if (status != ErrSecSuccess)
        {
            return new AuthenticatorResult(AuthenticatorStatus.Failed,
                Message: status == ErrSecMissingEntitlement ? NeedsSigning : $"The keychain didn't save Touch ID's key (error {status}).");
        }

        // Read it straight back, so the user's first Touch ID prompt is now and a key that can't be read isn't kept.
        // The item's access control is enforced by the Secure Enclave, so a key that reads back is held in hardware.
        var released = Read(keyName, cancel);
        if (released.Status != AuthenticatorStatus.Success)
        {
            Delete(keyName);
            return released;
        }

        return released with { HardwareBacked = true };
    });

    public Task<AuthenticatorResult> ReleaseAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel) =>
        Task.Run(() => Read(keyName, cancel));

    public Task DeleteAsync(string keyName) => Task.Run(() => Delete(keyName));

    /// <summary>
    /// Reads the item, which shows the Touch ID prompt. The prompt runs under an LAContext so that
    /// <paramref name="cancel"/> can dismiss it (<c>invalidate</c>); not yet checked on a Mac.
    /// </summary>
    static AuthenticatorResult Read(string keyName, CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested)
        {
            return new(AuthenticatorStatus.Cancelled);
        }

        int status;
        IntPtr data;
        IntPtr context = NativeLibrary.TryLoad(LocalAuthentication, out _) ? objc_msgSend(objc_getClass("LAContext"), sel_registerName("new")) : IntPtr.Zero;
        try
        {
            using var dismiss = context == IntPtr.Zero ? default : cancel.Register(() => objc_msgSend(context, sel_registerName("invalidate")));
            using var query = Query(keyName);
            query.Add("kSecReturnData", Constant(CoreFoundation, "kCFBooleanTrue"));
            query.Add("kSecMatchLimit", Constant(Security, "kSecMatchLimitOne"));
            query.AddString("kSecUseOperationPrompt", "unlock the Orrery vault"); // Shown as "Orrery is trying to …"
            if (context != IntPtr.Zero)
            {
                query.Add("kSecUseAuthenticationContext", context);
            }

            status = SecItemCopyMatching(query.Create(), out data);
        }
        finally
        {
            if (context != IntPtr.Zero) objc_msgSend(context, sel_registerName("release"));
        }

        try
        {
            if (cancel.IsCancellationRequested)
            {
                return new(AuthenticatorStatus.Cancelled); // Even if it was read: the caller has gone.
            }

            // An item made with BiometryCurrentSet can't be read once the enrolled fingers change. Apple's documents
            // say the item becomes inaccessible without naming the status; reports differ between errSecItemNotFound
            // and errSecAuthFailed, so both count as "not found", which only stops offering it until Orrery restarts.
            // Not yet checked on a Mac.
            return status switch
            {
                ErrSecSuccess when data != IntPtr.Zero => AuthenticatorResult.Released(Bytes(data)),
                ErrSecUserCanceled => new(AuthenticatorStatus.Cancelled),
                ErrSecItemNotFound or ErrSecAuthFailed => new(AuthenticatorStatus.KeyGone,
                    Message: "Touch ID didn't release Orrery's key. That happens if your finger isn't recognised, and for good once the enrolled fingers change."),
                ErrSecInteractionNotAllowed => new(AuthenticatorStatus.Failed, Message: "Touch ID can't ask right now. Use the master password."),
                ErrSecMissingEntitlement => new(AuthenticatorStatus.Failed, Message: NeedsSigning),
                _ => new(AuthenticatorStatus.Failed, Message: $"Touch ID didn't work (keychain error {status}). Use the master password."),
            };
        }
        finally
        {
            Release(data);
        }
    }

    static void Delete(string keyName)
    {
        using var query = Query(keyName);
        SecItemDelete(query.Create()); // Missing is fine.
    }

    /// <summary>Why Touch ID can't be used, or null when it can. Asks LocalAuthentication, which doesn't prompt.</summary>
    static string? BiometryProblem()
    {
        if (!NativeLibrary.TryLoad(LocalAuthentication, out _))
        {
            return "Touch ID isn't available on this Mac.";
        }

        // canEvaluatePolicy:error: hands back an autoreleased NSError, and this thread has no pool of its own.
        IntPtr pool = objc_autoreleasePoolPush();
        IntPtr context = objc_msgSend(objc_getClass("LAContext"), sel_registerName("new"));
        if (context == IntPtr.Zero)
        {
            objc_autoreleasePoolPop(pool);
            return "Touch ID isn't available on this Mac.";
        }

        try
        {
            IntPtr error = IntPtr.Zero;
            if (objc_msgSend_canEvaluate(context, sel_registerName("canEvaluatePolicy:error:"), BiometricsPolicy, ref error))
            {
                return null;
            }

            long code = error == IntPtr.Zero ? 0 : objc_msgSend_long(error, sel_registerName("code"));
            return code switch
            {
                LaErrorBiometryNotEnrolled => "No fingers are enrolled in Touch ID. Add one in System Settings › Touch ID & Password.",
                LaErrorPasscodeNotSet => "This Mac has no login password, which Touch ID needs.",
                LaErrorBiometryNotAvailable => "This Mac has no Touch ID, or it's turned off.",
                _ => "Touch ID isn't available on this Mac.",
            };
        }
        finally
        {
            objc_msgSend(context, sel_registerName("release"));
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>A keychain query for Orrery's item (or every Orrery item, when <paramref name="keyName"/> is null) in the data-protection keychain.</summary>
    static Dictionary Query(string? keyName)
    {
        var query = new Dictionary();
        query.Add("kSecClass", Constant(Security, "kSecClassGenericPassword"));
        query.AddString("kSecAttrService", Service);
        if (keyName is not null)
        {
            query.AddString("kSecAttrAccount", keyName);
        }

        query.Add("kSecUseDataProtectionKeychain", Constant(CoreFoundation, "kCFBooleanTrue"));
        return query;
    }

    /// <summary>The value of a framework's exported constant, such as <c>kSecClass</c>.</summary>
    static IntPtr Constant(string framework, string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(framework), symbol));

    /// <summary>
    /// A copy of the bytes in <paramref name="data"/>, which the caller clears. The keychain hands the secret back as an
    /// immutable CFData; CoreFoundation gives no way to clear one, and writing through its read-only pointer isn't
    /// allowed (CF may share that storage), so the keychain's own copy is only released, not wiped.
    /// </summary>
    static byte[] Bytes(IntPtr data)
    {
        var bytes = new byte[(int)CFDataGetLength(data)];
        Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
        return bytes;
    }

    static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero) CFRelease(handle);
    }

    /// <summary>Builds a CFDictionary of Security.framework keys; owns what it creates and releases it when disposed.</summary>
    sealed class Dictionary : IDisposable
    {
        readonly List<IntPtr> _keys = [], _values = [], _owned = [], _wipe = [];

        public void Add(string key, IntPtr value)
        {
            _keys.Add(Constant(Security, key));
            _values.Add(value);
        }

        public void AddString(string key, string value)
        {
            var text = CFStringCreateWithCString(IntPtr.Zero, Encoding.UTF8.GetBytes(value + "\0"), Utf8);
            _owned.Add(text);
            Add(key, text);
        }

        /// <summary>Adds bytes (the secret) as a CFMutableData of fixed capacity, so its copy can be cleared before it's released.</summary>
        public void AddData(string key, byte[] value)
        {
            var data = CFDataCreateMutable(IntPtr.Zero, value.Length);
            if (data == IntPtr.Zero)
            {
                throw new OutOfMemoryException("CoreFoundation couldn't make a data object.");
            }

            CFDataAppendBytes(data, value, value.Length);
            _owned.Add(data);
            _wipe.Add(data);
            Add(key, data);
        }

        public IntPtr Create()
        {
            var dictionary = CFDictionaryCreate(IntPtr.Zero, [.. _keys], [.. _values], _keys.Count,
                NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeDictionaryValueCallBacks"));
            _owned.Add(dictionary);
            return dictionary;
        }

        public void Dispose()
        {
            foreach (var data in _wipe)
            {
                int length = (int)CFDataGetLength(data);
                if (length > 0) Marshal.Copy(new byte[length], 0, CFDataGetMutableBytePtr(data), length);
            }

            _wipe.Clear();
            foreach (var handle in _owned) Release(handle);
            _owned.Clear();
        }
    }

    [DllImport(Security)]
    static extern IntPtr SecAccessControlCreateWithFlags(IntPtr allocator, IntPtr protection, nuint flags, IntPtr error);

    [DllImport(Security)]
    static extern int SecItemAdd(IntPtr attributes, IntPtr result);

    [DllImport(Security)]
    static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(Security)]
    static extern int SecItemDelete(IntPtr query);

    [DllImport(CoreFoundation)]
    static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundation)]
    static extern IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] text, uint encoding);

    [DllImport(CoreFoundation)]
    static extern IntPtr CFDataCreateMutable(IntPtr allocator, nint capacity);

    [DllImport(CoreFoundation)]
    static extern void CFDataAppendBytes(IntPtr data, byte[] bytes, nint length);

    [DllImport(CoreFoundation)]
    static extern IntPtr CFDataGetMutableBytePtr(IntPtr data);

    [DllImport(CoreFoundation)]
    static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundation)]
    static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundation)]
    static extern void CFRelease(IntPtr handle);

    [DllImport(ObjC)]
    static extern IntPtr objc_getClass(string name);

    [DllImport(ObjC)]
    static extern IntPtr sel_registerName(string name);

    [DllImport(ObjC)]
    static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC)]
    static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    static extern long objc_msgSend_long(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool objc_msgSend_canEvaluate(IntPtr receiver, IntPtr selector, long policy, ref IntPtr error);
}
