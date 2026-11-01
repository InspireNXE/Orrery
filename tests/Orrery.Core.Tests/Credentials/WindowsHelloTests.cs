using System.Runtime.Versioning;
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
}
