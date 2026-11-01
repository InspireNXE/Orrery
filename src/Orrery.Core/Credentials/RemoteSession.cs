using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Core.Credentials;

/// <summary>
/// Whether Orrery is running in a remote session, such as Remote Desktop. Biometric unlock is for the person at the
/// computer itself, so it isn't offered or prompted for in one.
/// </summary>
public interface IRemoteSession
{
    bool IsRemote { get; }
}

/// <summary>
/// Remote Desktop on Windows: <c>GetSystemMetrics(SM_REMOTESESSION)</c>, and also any session that isn't the one
/// attached to the physical console, which catches RemoteFX sessions that SM_REMOTESESSION misses. When the console
/// session is changing hands Windows reports no console session, and that counts as remote too.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteSession : IRemoteSession
{
    const int SM_REMOTESESSION = 0x1000;
    const uint NoConsoleSession = 0xFFFFFFFF;

    public bool IsRemote
    {
        get
        {
            if (GetSystemMetrics(SM_REMOTESESSION) != 0)
            {
                return true;
            }

            uint console = WTSGetActiveConsoleSessionId();
            return console == NoConsoleSession
                || !ProcessIdToSessionId(GetCurrentProcessId(), out uint session)
                || session != console;
        }
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
