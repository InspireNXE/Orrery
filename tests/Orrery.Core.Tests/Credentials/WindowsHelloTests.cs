using System.Runtime.Versioning;
using System.Security.Cryptography;
using Orrery.Core.Credentials;
using Xunit;

namespace Orrery.Core.Tests.Credentials;

/// <summary>Runs on Windows only: shows as skipped elsewhere.</summary>
sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}

/// <summary>Runs on Windows only: shows as skipped elsewhere.</summary>
sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}

/// <summary>
/// The real WinRT plumbing behind Windows Hello unlock, through calls that never show a prompt. The prompt itself, and
/// making or using a Hello key, need a person (docs/design-biometric-unlock.md §6.3).
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsHelloTests
{
    [WindowsFact]
    public async Task A_buffer_round_trips_through_the_windows_runtime()
    {
        var bytes = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        var copy = await WinRt.RunAsync(() =>
        {
            var buffer = WinRt.CreateBuffer(bytes);
            try
            {
                return WinRt.ReadBuffer(buffer);
            }
            finally
            {
                WinRt.Release(buffer);
            }
        });

        Assert.Equal(bytes, copy);
    }

    [WindowsFact]
    public async Task A_missing_runtime_class_or_interface_is_reported_not_crashed_on()
    {
        await WinRt.RunAsync(() =>
        {
            Assert.Equal(IntPtr.Zero, WinRt.TryGetFactory("Windows.Security.Credentials.NoSuchClass", Guid.NewGuid()));
            Assert.ThrowsAny<Exception>(() => WinRt.GetFactory("Windows.Security.Credentials.NoSuchClass", Guid.NewGuid()));

            // KeyCredentialManager exists, but not with a made-up interface.
            Assert.Equal(IntPtr.Zero, WinRt.TryGetFactory("Windows.Security.Credentials.KeyCredentialManager", Guid.NewGuid()));
            return true;
        });
    }

    [WindowsFact]
    public async Task Checking_for_windows_hello_waits_for_its_answer_without_a_prompt()
    {
        // IsSupportedAsync is a real async WinRT call, so this covers activation, strings and waiting on IAsyncInfo.
        var reason = await new WindowsHelloAuthenticator().CheckAvailableAsync().WaitAsync(TimeSpan.FromSeconds(60));

        if (!WindowsHelloAuthenticator.HasTpm2())
        {
            Assert.Contains("TPM", reason);
        }
        else if (reason is not null)
        {
            Assert.Contains("Sign-in options", reason);
        }
    }

    [WindowsFact]
    public void A_window_id_needs_a_window()
    {
        Assert.Equal(0UL, WinRt.WindowIdFor(0));
    }

    [WindowsFact]
    public async Task A_secret_buffer_is_wiped_once_read()
    {
        var bytes = RandomNumberGenerator.GetBytes(256);

        var (copy, after) = await WinRt.RunAsync(() =>
        {
            var buffer = WinRt.CreateBuffer(bytes);
            try
            {
                var read = WinRt.ReadAndClearBuffer(buffer);
                return (read, WinRt.ReadBuffer(buffer));
            }
            finally
            {
                WinRt.Release(buffer);
            }
        });

        Assert.Equal(bytes, copy);
        Assert.Equal(new byte[bytes.Length], after);
    }

    [WindowsFact]
    public void Listing_records_reads_without_writing()
    {
        // Only reads Credential Manager: no test makes a real entry.
        Assert.Empty(new WindowsCredentialBackend().List("orrery:test-nothing-here-" + Guid.NewGuid().ToString("N") + ":"));
    }

    // ---- Hardware attestation: only a key Windows attests is in the TPM is used ----

    [Fact]
    public void Only_an_attested_key_is_accepted()
    {
        Assert.Null(WindowsHelloAuthenticator.HardwareProblem(WindowsHelloAuthenticator.AttestationStatus.Success));
    }

    [Fact]
    public void A_software_key_is_refused()
    {
        var problem = WindowsHelloAuthenticator.HardwareProblem(WindowsHelloAuthenticator.AttestationStatus.NotSupported);
        Assert.Contains("software", problem);

        // And what usually fixes it: setting the PIN up again, or a virtual TPM in a virtual machine.
        Assert.Contains("remove the PIN in Windows Settings › Accounts › Sign-in options, add it again", problem);
        Assert.Contains("virtual TPM", problem);
    }

    [Theory]
    [InlineData(3)] // TemporaryFailure
    [InlineData(1)] // UnknownError
    [InlineData(99)]
    public void A_key_that_cant_be_shown_to_be_in_the_tpm_is_refused_with_try_again_later(int status)
    {
        var problem = WindowsHelloAuthenticator.HardwareProblem((WindowsHelloAuthenticator.AttestationStatus)status);
        Assert.NotNull(problem);
        Assert.Contains("Try again later", problem);
    }

    // ---- When a 24H2 "ForWindow" call is asked again without the window ----

    [Theory]
    [InlineData(WindowsHelloAuthenticator.ENotImpl)]
    [InlineData(WindowsHelloAuthenticator.ENoInterface)]
    [InlineData(WindowsHelloAuthenticator.RegdbClassNotReg)]
    [InlineData(WindowsHelloAuthenticator.EInvalidArg)]
    [InlineData(WindowsHelloAuthenticator.ErrorInvalidWindowHandle)]
    public void A_call_or_window_refused_as_it_starts_is_asked_again_without_the_window(int hresult)
    {
        Assert.True(WindowsHelloAuthenticator.RetryWithoutWindow(hresult, whenStarting: true));
    }

    [Theory]
    [InlineData(WindowsHelloAuthenticator.EInvalidArg)]
    [InlineData(WindowsHelloAuthenticator.ErrorInvalidWindowHandle)]
    public void A_window_refused_when_the_call_finishes_is_asked_again_without_it(int hresult)
    {
        Assert.True(WindowsHelloAuthenticator.RetryWithoutWindow(hresult, whenStarting: false));
    }

    [Theory]
    [InlineData(WindowsHelloAuthenticator.ENotImpl)]
    [InlineData(WindowsHelloAuthenticator.ENoInterface)]
    [InlineData(WindowsHelloAuthenticator.RegdbClassNotReg)]
    [InlineData(unchecked((int)0x80004005))] // E_FAIL
    [InlineData(unchecked((int)0x80090036))] // NTE_USER_CANCELLED
    [InlineData(unchecked((int)0x800704C7))] // ERROR_CANCELLED
    [InlineData(unchecked((int)0x80090011))] // NTE_NOT_FOUND
    [InlineData(unchecked((int)0x80090035))] // NTE_DEVICE_NOT_READY
    public void Any_other_error_when_the_call_finishes_stands_so_there_is_no_second_prompt(int hresult)
    {
        Assert.False(WindowsHelloAuthenticator.RetryWithoutWindow(hresult, whenStarting: false));
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005))] // E_FAIL
    [InlineData(unchecked((int)0x80090036))] // NTE_USER_CANCELLED
    [InlineData(unchecked((int)0x8007000E))] // E_OUTOFMEMORY
    public void Any_other_error_as_the_call_starts_stands(int hresult)
    {
        Assert.False(WindowsHelloAuthenticator.RetryWithoutWindow(hresult, whenStarting: true));
    }

    // ---- Which window is brought forward before 24H2 ----

    static string Broker => WindowsHelloAuthenticator.PromptProcessPath();

    [WindowsFact]
    public void The_credential_prompt_process_window_is_brought_forward()
    {
        Assert.True(WindowsHelloAuthenticator.IsHelloPrompt(true, Broker, Broker));
        Assert.True(WindowsHelloAuthenticator.IsHelloPrompt(true, Broker.ToUpperInvariant(), Broker));
    }

    [WindowsTheory]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\CredentialUIBroker.exe")]
    [InlineData(@"C:\Program Files\Something\CredentialUIBroker.exe")]
    [InlineData("CredentialUIBroker.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void A_window_of_any_other_program_is_left_alone(string? image)
    {
        Assert.False(WindowsHelloAuthenticator.IsHelloPrompt(true, image, Broker));
    }

    [WindowsFact]
    public void A_hidden_prompt_window_is_left_alone()
    {
        Assert.False(WindowsHelloAuthenticator.IsHelloPrompt(false, Broker, Broker));
    }

    [WindowsFact]
    public void A_path_that_walks_back_into_system32_still_has_to_be_the_broker()
    {
        var sneaky = Path.Combine(Path.GetDirectoryName(Broker)!, "..", "System32", "CredentialUIBroker.exe");
        Assert.True(WindowsHelloAuthenticator.IsHelloPrompt(true, sneaky, Broker));
        Assert.False(WindowsHelloAuthenticator.IsHelloPrompt(true, Path.Combine(Path.GetDirectoryName(Broker)!, "..", "Temp", "CredentialUIBroker.exe"), Broker));
    }
}

/// <summary>
/// The check that a new Windows Hello key signs the same every time, run over public-key blobs made here: no Hello key
/// is created. Plain .NET crypto, so it runs everywhere.
/// </summary>
[SupportedOSPlatform("windows")]
public class DeterministicSignatureTests
{
    static readonly byte[] Challenge = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void An_rsa_pkcs1_signature_is_accepted()
    {
        using var rsa = RSA.Create(2048);
        var signature = rsa.SignData(Challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.True(WindowsHelloAuthenticator.IsDeterministicRsaSignature(rsa.ExportSubjectPublicKeyInfo(), Challenge, signature));
        Assert.Equal(signature, rsa.SignData(Challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void A_pss_signature_which_differs_every_time_is_refused()
    {
        using var rsa = RSA.Create(2048);
        var signature = rsa.SignData(Challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        Assert.False(WindowsHelloAuthenticator.IsDeterministicRsaSignature(rsa.ExportSubjectPublicKeyInfo(), Challenge, signature));
    }

    [Fact]
    public void A_signature_over_something_else_is_refused()
    {
        using var rsa = RSA.Create(2048);
        var signature = rsa.SignData(RandomNumberGenerator.GetBytes(32), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.False(WindowsHelloAuthenticator.IsDeterministicRsaSignature(rsa.ExportSubjectPublicKeyInfo(), Challenge, signature));
    }

    [Fact]
    public void An_elliptic_curve_key_is_refused()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = ecdsa.SignData(Challenge, HashAlgorithmName.SHA256);

        Assert.False(WindowsHelloAuthenticator.IsDeterministicRsaSignature(ecdsa.ExportSubjectPublicKeyInfo(), Challenge, signature));
    }

    [Fact]
    public void A_damaged_blob_is_refused()
    {
        Assert.False(WindowsHelloAuthenticator.IsDeterministicRsaSignature([1, 2, 3], Challenge, new byte[256]));
    }
}
