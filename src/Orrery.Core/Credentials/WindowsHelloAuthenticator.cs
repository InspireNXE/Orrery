using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;

namespace Orrery.Core.Credentials;

/// <summary>
/// Windows Hello through <c>Windows.Security.Credentials.KeyCredentialManager</c>: a per-user RSA key that signs only
/// after a Hello gesture (face, fingerprint or PIN). The secret is the key's signature over the challenge.
/// RSASSA-PKCS1-v1_5 is deterministic, so the same key and challenge always sign the same; turning it on checks that
/// the key is RSA and the signature verifies, so a key type that signs differently each time is refused. A new key is
/// only accepted when Windows attests that it's held in the TPM. See docs/design-biometric-unlock.md §2.1.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsHelloAuthenticator : IPlatformAuthenticator
{
    static readonly ILogger Logger = Log.For("Vault");

    const string ManagerClass = "Windows.Security.Credentials.KeyCredentialManager";

    // Interface IDs and vtable slots from windows.security.credentials.h, Windows SDK 10.0.26100.
    static readonly Guid IKeyCredentialManagerStatics = new("6AAC468B-0EF1-4CE0-8290-4106DA6A63B5");
    const int IsSupportedAsync = 6, RequestCreateAsync = 8, OpenAsync = 9, DeleteKeyAsync = 10;

    // Windows 11 24H2 (UniversalApiContract 19): the same calls with an owner window for the prompt.
    static readonly Guid IKeyCredentialManagerCreateWithWindowStatics = new("30B1B9C9-61EF-43E8-88AC-CC433B38D1A6");
    const int RequestCreateForWindowAsync = 6;
    static readonly Guid IKeyCredentialWithWindow = new("050DEB3B-B19C-4635-9DF6-5650D66C62B1");
    const int RequestSignForWindowAsync = 6;

    // IKeyCredential (the default interface of KeyCredential): Name, RetrievePublicKey (default blob type), the same
    // with a blob type, RequestSignAsync, GetAttestationAsync.
    const int RetrievePublicKeyWithBlobType = 8, RequestSignAsync = 9, GetAttestationAsync = 10;
    const int X509SubjectPublicKeyInfo = 0;

    // IKeyCredentialRetrievalResult and IKeyCredentialOperationResult both have the value at 6 and the status at 7.
    const int ResultValue = 6, ResultStatus = 7;

    // IKeyCredentialAttestationResult: CertificateChainBuffer, AttestationBuffer, Status.
    const int AttestationResultStatus = 8;

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

    /// <summary>KeyCredentialAttestationStatus.</summary>
    internal enum AttestationStatus
    {
        Success = 0,
        UnknownError = 1,
        NotSupported = 2,
        TemporaryFailure = 3,
    }

    // A person may take a while over the prompt; after this long Orrery stops waiting and cancels it.
    static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);
    static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(30);

    // Attestation may need Windows to fetch the TPM's certificate first.
    static readonly TimeSpan AttestationTimeout = TimeSpan.FromSeconds(60);

    const int NteUserCancelled = unchecked((int)0x80090036);
    const int ErrorCancelled = unchecked((int)0x800704C7);

    // What a 24H2 "ForWindow" call fails with when Windows refuses it or the window, so asking without one may work.
    internal const int ENotImpl = unchecked((int)0x80004001);
    internal const int ENoInterface = unchecked((int)0x80004002);
    internal const int RegdbClassNotReg = unchecked((int)0x80040154);
    internal const int EInvalidArg = unchecked((int)0x80070057);
    internal const int ErrorInvalidWindowHandle = unchecked((int)0x80070578);
    const int EPointer = unchecked((int)0x80004003);
    static readonly int[] NotFoundErrors =
    [
        unchecked((int)0x80090011), // NTE_NOT_FOUND
        unchecked((int)0x8009000D), // NTE_NO_KEY
        unchecked((int)0x80090016), // NTE_BAD_KEYSET
        unchecked((int)0x80070002), // ERROR_FILE_NOT_FOUND
        unchecked((int)0x80070490), // ERROR_NOT_FOUND
    ];

    const string PromptClass = "Credential Dialog Xaml Host";

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
            WinRt.Wait(operation, QuickTimeout, CancellationToken.None);
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

    public Task<AuthenticatorResult> CreateAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel) => WinRt.RunAsync(() =>
    {
        ulong windowId = WinRt.WindowIdFor(window);
        var name = WinRt.CreateString(keyName);
        IntPtr operation = 0, result = 0, credential = 0;
        bool made = false, keep = false;
        try
        {
            operation = WaitPreferringWindow(
                windowId == 0 ? null : () =>
                {
                    var withWindow = WinRt.TryGetFactory(ManagerClass, IKeyCredentialManagerCreateWithWindowStatics);
                    if (withWindow == IntPtr.Zero)
                    {
                        return (ENoInterface, IntPtr.Zero);
                    }

                    try
                    {
                        IntPtr started = 0;
                        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, int, IntPtr*, int>)WinRt.Method(withWindow, RequestCreateForWindowAsync))(
                            withWindow, windowId, name, ReplaceExisting, &started);
                        return (hr, started);
                    }
                    finally
                    {
                        WinRt.Release(withWindow);
                    }
                },
                () =>
                {
                    var statics = WinRt.GetFactory(ManagerClass, IKeyCredentialManagerStatics);
                    try
                    {
                        IntPtr started = 0;
                        WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, IntPtr*, int>)WinRt.Method(statics, RequestCreateAsync))(
                            statics, name, ReplaceExisting, &started));
                        return started;
                    }
                    finally
                    {
                        WinRt.Release(statics);
                    }
                },
                ref windowId, cancel);

            result = WinRt.GetObjectResult(operation);
            var status = (KeyCredentialStatus)WinRt.GetInt32(result, ResultStatus);
            if (status != KeyCredentialStatus.Success)
            {
                return Failure(status);
            }

            made = true;
            credential = WinRt.GetObject(result, ResultValue);

            // Before the second prompt: is the new key really in the TPM? What Windows said goes in the log, since
            // the user only sees what to do about it.
            var attestation = Attestation(credential, cancel);
            if (HardwareProblem(attestation) is { } problem)
            {
                Logger.LogWarning("Windows Hello didn't attest the new key as held in the TPM: {Status} (TPM 2.0 present: {Tpm}, virtual machine: {Vm})",
                    attestation, HasTpm2(), LooksVirtual());
                return new AuthenticatorResult(AuthenticatorStatus.Failed, Message: problem);
            }

            var signed = Sign(credential, challenge, windowId, checkKey: true, cancel);
            keep = signed.Status == AuthenticatorStatus.Success;
            return signed with { HardwareBacked = keep };
        }
        catch (OperationCanceledException)
        {
            return new AuthenticatorResult(AuthenticatorStatus.Canceled);
        }
        catch (Exception e) when (IsCancellation(e.HResult))
        {
            return new AuthenticatorResult(AuthenticatorStatus.Canceled);
        }
        finally
        {
            WinRt.Release(credential);
            WinRt.Release(result);
            WinRt.Release(operation);
            WinRt.DeleteString(name);

            // A key made here but not used opens nothing; don't leave it behind.
            if (made && !keep)
            {
                DeleteKey(keyName);
            }
        }
    });

    public Task<AuthenticatorResult> ReleaseAsync(string keyName, byte[] challenge, nint window, CancellationToken cancel) => WinRt.RunAsync(() =>
    {
        var name = WinRt.CreateString(keyName);
        IntPtr statics = 0, operation = 0, result = 0, credential = 0;
        try
        {
            // Opening the key doesn't prompt; signing with it does.
            statics = WinRt.GetFactory(ManagerClass, IKeyCredentialManagerStatics);
            WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Method(statics, OpenAsync))(statics, name, &operation));
            WinRt.Wait(operation, QuickTimeout, cancel);
            result = WinRt.GetObjectResult(operation);
            var status = (KeyCredentialStatus)WinRt.GetInt32(result, ResultStatus);
            if (status != KeyCredentialStatus.Success)
            {
                return Failure(status);
            }

            credential = WinRt.GetObject(result, ResultValue);
            return Sign(credential, challenge, WinRt.WindowIdFor(window), checkKey: false, cancel);
        }
        catch (OperationCanceledException)
        {
            return new AuthenticatorResult(AuthenticatorStatus.Canceled);
        }
        catch (Exception e) when (IsCancellation(e.HResult))
        {
            return new AuthenticatorResult(AuthenticatorStatus.Canceled);
        }
        finally
        {
            WinRt.Release(credential);
            WinRt.Release(result);
            WinRt.Release(operation);
            WinRt.Release(statics);
            WinRt.DeleteString(name);
        }
    });

    public Task DeleteAsync(string keyName) => WinRt.RunAsync(() =>
    {
        DeleteKey(keyName, throwOnError: true);
        return true;
    });

    static void DeleteKey(string keyName, bool throwOnError = false)
    {
        var name = WinRt.CreateString(keyName);
        IntPtr statics = 0, action = 0;
        try
        {
            statics = WinRt.GetFactory(ManagerClass, IKeyCredentialManagerStatics);
            WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Method(statics, DeleteKeyAsync))(statics, name, &action));
            WinRt.Wait(action, QuickTimeout, CancellationToken.None);
            WinRt.GetActionResult(action);
        }
        catch (Exception e) when (NotFoundErrors.Contains(e.HResult))
        {
            // Already gone.
        }
        catch (Exception) when (!throwOnError)
        {
            // Cleaning up after a failed turn-on: the key opens nothing without a record.
        }
        finally
        {
            WinRt.Release(action);
            WinRt.Release(statics);
            WinRt.DeleteString(name);
        }
    }

    /// <summary>
    /// Starts a 24H2 "ForWindow" call and waits for it. Only when Windows refuses that call or the window
    /// (<see cref="RetryWithoutWindow"/>) does it start the plain call instead and clear <paramref name="windowId"/>;
    /// any other error is the answer, so the user isn't asked twice. Returns the finished operation, which the caller
    /// releases.
    /// </summary>
    /// <param name="withWindow">Starts the call: its HRESULT and the operation. <see cref="ENoInterface"/> when this Windows doesn't have it.</param>
    static IntPtr WaitPreferringWindow(Func<(int Result, IntPtr Operation)>? withWindow, Func<IntPtr> plain, ref ulong windowId, CancellationToken cancel)
    {
        if (withWindow is not null)
        {
            var (hr, operation) = withWindow();
            if (hr >= 0)
            {
                if (operation == IntPtr.Zero)
                {
                    throw new COMException("Windows returned no object.", EPointer);
                }

                try
                {
                    WinRt.Wait(operation, PromptTimeout, cancel);
                    return operation;
                }
                catch (Exception e) when (e is not OperationCanceledException and not TimeoutException && RetryWithoutWindow(e.HResult, whenStarting: false))
                {
                    WinRt.Release(operation);
                }
                catch
                {
                    WinRt.Release(operation);
                    throw;
                }
            }
            else if (!RetryWithoutWindow(hr, whenStarting: true))
            {
                WinRt.Check(hr);
            }
        }

        windowId = 0;
        var fallback = plain();
        if (fallback == IntPtr.Zero)
        {
            throw new COMException("Windows returned no object.", EPointer);
        }

        try
        {
            WinRt.Wait(fallback, PromptTimeout, cancel, PromptToFront());
            return fallback;
        }
        catch
        {
            WinRt.Release(fallback);
            throw;
        }
    }

    /// <summary>Asks Windows whether the key is held in the TPM. Never prompts.</summary>
    static AttestationStatus Attestation(IntPtr credential, CancellationToken cancel)
    {
        IntPtr operation = 0, result = 0;
        try
        {
            operation = WinRt.GetObject(credential, GetAttestationAsync);
            WinRt.Wait(operation, AttestationTimeout, cancel);
            result = WinRt.GetObjectResult(operation);
            return (AttestationStatus)WinRt.GetInt32(result, AttestationResultStatus);
        }
        catch (TimeoutException)
        {
            return AttestationStatus.TemporaryFailure;
        }
        catch (Exception e) when (e is not OperationCanceledException && !IsCancellation(e.HResult))
        {
            Logger.LogWarning("Asking Windows Hello for the key's attestation failed: 0x{HResult:X8}", e.HResult);
            return AttestationStatus.UnknownError;
        }
        finally
        {
            WinRt.Release(result);
            WinRt.Release(operation);
        }
    }

    /// <summary>
    /// Null when the attestation shows the key is in the TPM; otherwise why Orrery won't use it. Only Success is
    /// accepted. NotSupported means Windows won't vouch for the key (it may be in software, or Windows may just not offer
    /// attestation on this computer), and a temporary failure proves nothing either way.
    /// </summary>
    internal static string? HardwareProblem(AttestationStatus status) => status switch
    {
        AttestationStatus.Success => null,
        AttestationStatus.NotSupported => NotAttested,
        AttestationStatus.TemporaryFailure => "Windows couldn't confirm just now that the Windows Hello key is held in the TPM security chip, "
            + "so nothing was turned on. Try again later; Windows may need to be online for this the first time.",
        _ => "Windows couldn't confirm that the Windows Hello key is held in the TPM security chip, so nothing was turned on. Try again later.",
    };

    /// <summary>
    /// Why a key Windows won't vouch for is refused. It must never suggest resetting or removing the Windows PIN: that
    /// deletes every Windows Hello key on the account, passkeys and other apps' keys included, and Windows can answer
    /// NotSupported on a computer whose keys are in the TPM, so it may not help either.
    /// </summary>
    internal const string NotAttested = "Windows wouldn't confirm that the Windows Hello key is held in the TPM security chip, "
        + "so Orrery won't use Windows Hello on this computer. Nothing was turned on, and your master password still unlocks "
        + "the vault. Don't remove or reset your Windows PIN to try to fix this: that deletes every Windows Hello key on your "
        + "account, including passkeys and other apps' sign-ins, and may not help. In a virtual machine, Windows Hello also "
        + "needs a virtual TPM.";

    /// <summary>Whether Windows says it's running in a virtual machine, for the log only.</summary>
    static bool LooksVirtual()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var maker = $"{key?.GetValue("SystemManufacturer")} {key?.GetValue("SystemProductName")}";
            return maker.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || maker.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || maker.Contains("QEMU", StringComparison.OrdinalIgnoreCase) || maker.Contains("KVM", StringComparison.Ordinal)
                || maker.Contains("Parallels", StringComparison.OrdinalIgnoreCase) || maker.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    static AuthenticatorResult Sign(IntPtr credential, byte[] challenge, ulong windowId, bool checkKey, CancellationToken cancel)
    {
        IntPtr data = WinRt.CreateBuffer(challenge), operation = 0, result = 0, signature = 0;
        try
        {
            operation = WaitPreferringWindow(
                windowId == 0 ? null : () =>
                {
                    var withWindow = WinRt.TryQuery(credential, IKeyCredentialWithWindow);
                    if (withWindow == IntPtr.Zero)
                    {
                        return (ENoInterface, IntPtr.Zero);
                    }

                    try
                    {
                        IntPtr started = 0;
                        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, IntPtr*, int>)WinRt.Method(withWindow, RequestSignForWindowAsync))(
                            withWindow, windowId, data, &started);
                        return (hr, started);
                    }
                    finally
                    {
                        WinRt.Release(withWindow);
                    }
                },
                () =>
                {
                    IntPtr started = 0;
                    WinRt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)WinRt.Method(credential, RequestSignAsync))(credential, data, &started));
                    return started;
                },
                ref windowId, cancel);

            result = WinRt.GetObjectResult(operation);
            var status = (KeyCredentialStatus)WinRt.GetInt32(result, ResultStatus);
            if (status != KeyCredentialStatus.Success)
            {
                return Failure(status);
            }

            signature = WinRt.GetObject(result, ResultValue);
            var bytes = WinRt.ReadAndClearBuffer(signature);
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
            return IsDeterministicRsaSignature(WinRt.ReadBuffer(blob), challenge, signature);
        }
        finally
        {
            WinRt.Release(blob);
        }
    }

    /// <summary>
    /// True when <paramref name="subjectPublicKeyInfo"/> is an RSA key and <paramref name="signature"/> is its
    /// RSASSA-PKCS1-v1_5 SHA-256 signature over <paramref name="challenge"/>: the one scheme here that signs the same
    /// every time. Anything else (another key type, PSS, a bad signature) is false.
    /// </summary>
    internal static bool IsDeterministicRsaSignature(byte[] subjectPublicKeyInfo, byte[] challenge, byte[] signature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            return rsa.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a 24H2 "ForWindow" call that failed with <paramref name="hresult"/> should be asked again without the
    /// window. Only for a refusal of the call itself (this Windows doesn't have it), which can only come as it starts,
    /// or of the window. Anything else, canceling included, may mean the user already saw a prompt, so it stands.
    /// </summary>
    /// <param name="whenStarting">True when the call failed as it was made, false when it finished with the error.</param>
    internal static bool RetryWithoutWindow(int hresult, bool whenStarting) =>
        hresult is EInvalidArg or ErrorInvalidWindowHandle
        || (whenStarting && hresult is ENotImpl or ENoInterface or RegdbClassNotReg);

    static bool IsCancellation(int hresult) => hresult is NteUserCancelled or ErrorCancelled;

    /// <summary>
    /// What went wrong, for turning unlock on as well as for unlocking, so it says nothing about what to do next; that's
    /// for the caller. Windows's own name for the answer goes in the log.
    /// </summary>
    static AuthenticatorResult Failure(KeyCredentialStatus status)
    {
        if (status is not (KeyCredentialStatus.UserCanceled or KeyCredentialStatus.UserPrefersPassword))
        {
            Logger.LogWarning("Windows Hello answered {Status}", status);
        }

        return status switch
        {
            KeyCredentialStatus.UserCanceled or KeyCredentialStatus.UserPrefersPassword => new(AuthenticatorStatus.Canceled),
            KeyCredentialStatus.NotFound => new(AuthenticatorStatus.KeyGone, Message: "Windows Hello couldn't find Orrery's key."),
            KeyCredentialStatus.SecurityDeviceLocked => new(AuthenticatorStatus.Failed, Message: "Windows Hello is locked after too many tries."),
            _ => new(AuthenticatorStatus.Failed, Message: "Windows Hello didn't work."),
        };
    }

    /// <summary>
    /// Before 24H2 the prompt can't be given an owner window and may open behind Orrery, so bring it forward once it
    /// appears. Only a visible window of Windows' own credential prompt process is brought forward; anything else with
    /// the same window class is left alone, and it keeps looking.
    /// </summary>
    static Action PromptToFront()
    {
        bool done = false;
        var broker = PromptProcessPath();
        return () =>
        {
            for (nint candidate = FindWindowExW(0, 0, PromptClass, null); !done && candidate != 0; candidate = FindWindowExW(0, candidate, PromptClass, null))
            {
                if (IsHelloPrompt(IsWindowVisible(candidate), ImagePathOf(candidate), broker))
                {
                    done = SetForegroundWindow(candidate);
                }
            }
        };
    }

    /// <summary>Where Windows' credential prompt runs from: %SystemRoot%\System32\CredentialUIBroker.exe.</summary>
    internal static string PromptProcessPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "CredentialUIBroker.exe");

    /// <summary>Whether a window is the Hello prompt to bring forward: visible, and owned by <paramref name="brokerPath"/>.</summary>
    internal static bool IsHelloPrompt(bool visible, string? imagePath, string brokerPath)
    {
        if (!visible || string.IsNullOrEmpty(imagePath) || !Path.IsPathFullyQualified(imagePath))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(imagePath), Path.GetFullPath(brokerPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The full path of the program that owns <paramref name="window"/>, or null.</summary>
    static string? ImagePathOf(nint window)
    {
        if (GetWindowThreadProcessId(window, out uint processId) == 0 || processId == 0)
        {
            return null;
        }

        nint process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageNameW(process, 0, path, ref size) ? path.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>True when a TPM 2.0 is present and working. Windows Hello keeps keys in software without one.</summary>
    internal static bool HasTpm2()
    {
        try
        {
            return Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info) == 0 && info.TpmVersion == 2;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
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

    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("tbs.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetForegroundWindow(nint window);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageNameW(nint process, int flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint handle);
}
