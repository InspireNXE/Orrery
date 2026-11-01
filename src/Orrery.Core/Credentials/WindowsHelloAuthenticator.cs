using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Orrery.Core.Credentials;

/// <summary>
/// Windows Hello through <c>Windows.Security.Credentials.KeyCredentialManager</c>: a per-user RSA key held by the TPM,
/// which signs only after a Hello gesture (face, fingerprint or PIN). The secret is the key's signature over the
/// challenge. RSASSA-PKCS1-v1_5 is deterministic, so the same key and challenge always sign the same; turning it on
/// checks that the key is RSA and the signature verifies, so a key type that signs differently each time is refused.
/// See docs/design-biometric-unlock.md §2.1.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsHelloAuthenticator : IPlatformAuthenticator
{
    const string ManagerClass = "Windows.Security.Credentials.KeyCredentialManager";

    // Interface IDs and vtable slots from windows.security.credentials.idl, Windows SDK 10.0.26100.
    static readonly Guid IKeyCredentialManagerStatics = new("6AAC468B-0EF1-4CE0-8290-4106DA6A63B5");
    const int IsSupportedAsync = 6, RequestCreateAsync = 8, OpenAsync = 9, DeleteKeyAsync = 10;

    // Windows 11 24H2 (UniversalApiContract 19): the same calls with an owner window for the prompt.
    static readonly Guid IKeyCredentialManagerCreateWithWindowStatics = new("30B1B9C9-61EF-43E8-88AC-CC433B38D1A6");
    const int RequestCreateForWindowAsync = 6;
    static readonly Guid IKeyCredentialWithWindow = new("050DEB3B-B19C-4635-9DF6-5650D66C62B1");
    const int RequestSignForWindowAsync = 6;

    // IKeyCredential (the default interface of KeyCredential).
    const int RetrievePublicKeyWithBlobType = 8, RequestSignAsync = 9;
    const int X509SubjectPublicKeyInfo = 0;

    // IKeyCredentialRetrievalResult and IKeyCredentialOperationResult both have the value at 6 and the status at 7.
    const int ResultValue = 6, ResultStatus = 7;

    const int ReplaceExisting = 0;

    enum KeyCredentialStatus
    {
        Success = 0,
        UnknownError = 1,
        NotFound = 2,
        UserCanceled = 3,
        UserPrefersPassword = 4,
        CredentialAlreadyExists = 5,
        SecurityDeviceLocked = 6,
        AlgorithmNotSupported = 7,
    }

    // A person may take a while over the prompt; after this long Orrery stops waiting and cancels it.
    static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(30);

    const int NteUserCancelled = unchecked((int)0x80090036);
    static readonly int[] NotFoundErrors = [unchecked((int)0x80090011), unchecked((int)0x8009000D), unchecked((int)0x80070002), unchecked((int)0x80070490)];

    public string Name => "Windows Hello";

    public string Id => "windows-hello";

    public Task<string?> CheckAvailableAsync() => WinRt.RunAsync<string?>(() =>
    {
        if (!HasTpm2())
        {
            return "This computer has no TPM 2.0 security chip, so Windows Hello would keep Orrery's key in software. "
                + "Orrery only offers Windows Hello unlock when the key is held in hardware.";
        }

        var statics = WinRt.GetFactory(ManagerClass, IKeyCredentialManagerStatics);
        IntPtr operation = 0;
        try
        {
            operation = WinRt.GetObject(statics, IsSupportedAsync);
            WinRt.Wait(operation, QuickTimeout);
            return WinRt.GetBooleanResult(operation)
                ? null
                : "Windows Hello isn't set up for your account. Set up a PIN, face or fingerprint in Windows Settings › Accounts › Sign-in options.";
        }
        finally
        {
            WinRt.Release(operation);
            WinRt.Release(statics);
        }
    });

    public Task<AuthenticatorResult> CreateAsync(string keyName, byte[] challenge, nint window) => WinRt.RunAsync(() =>
    {
        ulong windowId = WinRt.WindowIdFor(window);
        var name = WinRt.CreateString(keyName);
        IntPtr operation = 0;
        try
        {
            var withWindow = windowId != 0 ? WinRt.TryGetFactory(ManagerClass, IKeyCredentialManagerCreateWithWindowStatics) : IntPtr.Zero;
            if (withWindow != IntPtr.Zero)
            {
                try
                {
                    // Refused (an unexpected window, say): fall back to the call without an owner below.
                    if (((delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, int, IntPtr*, int>)WinRt.Method(withWindow, RequestCreateForWindowAsync))(
                            withWindow, windowId, name, ReplaceExisting, &operation) < 0)
                    {
                        operation = IntPtr.Zero;
                    }
                }
                finally
                {
                    WinRt.Release(withWindow);
                }
            }

            if (operation == IntPtr.Zero)
            {
                windowId = 0;
                var statics = WinRt.GetFactory(ManagerClass, IKeyCredentialManagerStatics);
                try
                {
                    WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)WinRt.Method(statics, RequestCreateAsync))(
                        statics, name, ReplaceExisting, &operation));
                }
                finally
                {
                    WinRt.Release(statics);
                }
            }
        }
        finally
        {
            WinRt.DeleteString(name);
        }

        return SignWithRetrieved(operation, challenge, windowId, checkKey: true);
    });

    public Task<AuthenticatorResult> ReleaseAsync(string keyName, byte[] challenge, nint window) => WinRt.RunAsync(() =>
    {
        var name = WinRt.CreateString(keyName);
        var statics = WinRt.GetFactory(ManagerClass, IKeyCredentialManagerStatics);
        IntPtr operation = 0;
        try
        {
            // Opening the key doesn't prompt; signing with it does.
            WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Method(statics, OpenAsync))(statics, name, &operation));
        }
        finally
        {
            WinRt.Release(statics);
            WinRt.DeleteString(name);
        }

        return SignWithRetrieved(operation, challenge, WinRt.WindowIdFor(window), checkKey: false);
    });

    public Task DeleteAsync(string keyName) => WinRt.RunAsync(() =>
    {
        var name = WinRt.CreateString(keyName);
        var statics = WinRt.GetFactory(ManagerClass, IKeyCredentialManagerStatics);
        IntPtr action = 0;
        try
        {
            WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Method(statics, DeleteKeyAsync))(statics, name, &action));
            WinRt.Wait(action, QuickTimeout);
            WinRt.GetActionResult(action);
        }
        catch (Exception e) when (NotFoundErrors.Contains(e.HResult))
        {
            // Already gone.
        }
        finally
        {
            WinRt.Release(action);
            WinRt.Release(statics);
            WinRt.DeleteString(name);
        }

        return true;
    });

    /// <summary>Waits for a create or open, then signs the challenge with the key it found. Releases <paramref name="operation"/>.</summary>
    static AuthenticatorResult SignWithRetrieved(IntPtr operation, byte[] challenge, ulong windowId, bool checkKey)
    {
        IntPtr result = 0, credential = 0;
        try
        {
            WinRt.Wait(operation, PromptTimeout, PromptToFront(windowId));
            result = WinRt.GetObjectResult(operation);
            var status = (KeyCredentialStatus)WinRt.GetInt32(result, ResultStatus);
            if (status != KeyCredentialStatus.Success)
            {
                return Failure(status);
            }

            credential = WinRt.GetObject(result, ResultValue);
            return Sign(credential, challenge, windowId, checkKey);
        }
        catch (OperationCanceledException)
        {
            return new(AuthenticatorStatus.Cancelled);
        }
        catch (Exception e) when (e.HResult == NteUserCancelled)
        {
            return new(AuthenticatorStatus.Cancelled);
        }
        finally
        {
            WinRt.Release(credential);
            WinRt.Release(result);
            WinRt.Release(operation);
        }
    }

    static AuthenticatorResult Sign(IntPtr credential, byte[] challenge, ulong windowId, bool checkKey)
    {
        IntPtr data = WinRt.CreateBuffer(challenge), operation = 0, result = 0, signature = 0;
        try
        {
            var withWindow = windowId != 0 ? WinRt.TryQuery(credential, IKeyCredentialWithWindow) : IntPtr.Zero;
            if (withWindow != IntPtr.Zero)
            {
                try
                {
                    if (((delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, IntPtr*, int>)WinRt.Method(withWindow, RequestSignForWindowAsync))(
                            withWindow, windowId, data, &operation) < 0)
                    {
                        operation = IntPtr.Zero;
                    }
                }
                finally
                {
                    WinRt.Release(withWindow);
                }
            }

            if (operation == IntPtr.Zero)
            {
                windowId = 0;
                WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Method(credential, RequestSignAsync))(credential, data, &operation));
            }

            WinRt.Wait(operation, PromptTimeout, PromptToFront(windowId));
            result = WinRt.GetObjectResult(operation);
            var status = (KeyCredentialStatus)WinRt.GetInt32(result, ResultStatus);
            if (status != KeyCredentialStatus.Success)
            {
                return Failure(status);
            }

            signature = WinRt.GetObject(result, ResultValue);
            var bytes = WinRt.ReadBuffer(signature);
            if (checkKey && !SignsTheSameEveryTime(credential, challenge, bytes))
            {
                CryptographicOperations.ZeroMemory(bytes);
                return new(AuthenticatorStatus.Failed, Message: "Windows Hello made a kind of key Orrery can't use. Nothing was changed.");
            }

            return AuthenticatorResult.Released(bytes);
        }
        finally
        {
            WinRt.Release(signature);
            WinRt.Release(result);
            WinRt.Release(operation);
            WinRt.Release(data);
        }
    }

    /// <summary>True when the key is RSA and <paramref name="signature"/> is its PKCS#1 v1.5 signature, which is deterministic.</summary>
    static bool SignsTheSameEveryTime(IntPtr credential, byte[] challenge, byte[] signature)
    {
        IntPtr blob = 0;
        try
        {
            WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)WinRt.Method(credential, RetrievePublicKeyWithBlobType))(
                credential, X509SubjectPublicKeyInfo, &blob));
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(WinRt.ReadBuffer(blob), out _);
            return rsa.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            WinRt.Release(blob);
        }
    }

    static AuthenticatorResult Failure(KeyCredentialStatus status) => status switch
    {
        KeyCredentialStatus.UserCanceled or KeyCredentialStatus.UserPrefersPassword => new(AuthenticatorStatus.Cancelled),
        KeyCredentialStatus.NotFound => new(AuthenticatorStatus.KeyGone, Message: "Windows Hello no longer has Orrery's key, perhaps because Windows Hello was reset."),
        KeyCredentialStatus.SecurityDeviceLocked => new(AuthenticatorStatus.Failed, Message: "Windows Hello is locked after too many tries. Use the master password."),
        _ => new(AuthenticatorStatus.Failed, Message: $"Windows Hello didn't work ({status}). Use the master password."),
    };

    /// <summary>
    /// Before 24H2 the prompt can't be given an owner window and may open behind Orrery, so bring it forward once it
    /// appears. Not needed when the prompt has an owner.
    /// </summary>
    static Action? PromptToFront(ulong windowId)
    {
        if (windowId != 0)
        {
            return null;
        }

        bool done = false;
        return () =>
        {
            if (!done && FindWindowW("Credential Dialog Xaml Host", null) is var prompt and not 0)
            {
                done = SetForegroundWindow(prompt);
            }
        };
    }

    /// <summary>True when a TPM 2.0 is present and working. Windows Hello keeps keys in software without one.</summary>
    internal static bool HasTpm2()
    {
        try
        {
            return Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info) == 0 && info.TpmVersion == 2;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("tbs.dll")]
    static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(nint window);
}
