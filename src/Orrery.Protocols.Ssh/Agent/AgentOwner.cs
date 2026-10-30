using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Orrery.Protocols.Ssh.Agent;

/// <summary>
/// Who runs the agent at the other end of a pipe or behind a Pageant window. Anyone can create the OpenSSH
/// agent's pipe while that service is stopped, and any window can be called "Pageant", including one from a
/// lower-integrity process of this same user, so the server must be this user at Medium integrity or above,
/// or (for pipes) SYSTEM.
/// </summary>
[SupportedOSPlatform("windows")]
static class AgentOwner
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    const int TokenUser = 1, TokenIntegrityLevel = 25;
    const int MediumIntegrity = 0x2000;
    const int ERROR_SEM_TIMEOUT = 121;
    const uint SC_MANAGER_CONNECT = 0x0001, SERVICE_QUERY_STATUS = 0x0004, SERVICE_RUNNING = 4;
    const string SystemSid = "S-1-5-18";
    const string OpenSshAgentService = "ssh-agent";

    /// <summary>Throws unless the pipe's server is a process <see cref="IsTrusted"/> accepts. Call before sending anything.</summary>
    public static void CheckPipe(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var processId) || !IsTrusted(processId, allowSystem: true))
        {
            throw new IOException("The SSH agent pipe belongs to another account.");
        }
    }

    /// <summary>
    /// True when the process runs as this user at Medium integrity or above, or, with <paramref name="allowSystem"/>,
    /// as SYSTEM. Anything that can't be checked is untrusted.
    /// </summary>
    public static bool IsTrusted(uint processId, bool allowSystem)
    {
        if (TokenOf(processId) is not { } token)
        {
            // A standard user can't open a SYSTEM service's process, so ask the service manager instead.
            return allowSystem && processId != 0 && processId == RunningServiceProcessId(OpenSshAgentService);
        }

        using var me = WindowsIdentity.GetCurrent();
        return (token.User == me.User!.Value && token.Integrity >= MediumIntegrity) || (allowSystem && token.User == SystemSid);
    }

    /// <summary>Whether the pipe <c>\\.\pipe\name</c> exists. Unlike <see cref="File.Exists"/>, this doesn't connect to it.</summary>
    public static bool PipeExists(string name) =>
        WaitNamedPipe($@"\\.\pipe\{name}", 1) || Marshal.GetLastWin32Error() == ERROR_SEM_TIMEOUT;

    /// <summary>The process's token user and integrity level, or null when they can't be read.</summary>
    static (string User, int Integrity)? TokenOf(uint processId)
    {
        using var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsInvalid || !OpenProcessToken(process, TOKEN_QUERY, out var token))
        {
            return null;
        }

        using (token)
        {
            // TOKEN_USER and TOKEN_MANDATORY_LABEL both start with a pointer to the SID.
            var user = TokenSid(token, TokenUser);
            var label = TokenSid(token, TokenIntegrityLevel);
            if (user is null || label is null)
            {
                return null;
            }

            // The integrity level is the label's last sub-authority: S-1-16-8192 is Medium.
            return (user.Value, int.Parse(label.Value[(label.Value.LastIndexOf('-') + 1)..]));
        }
    }

    static SecurityIdentifier? TokenSid(SafeAccessTokenHandle token, int informationClass)
    {
        GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var size);
        if (size <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return GetTokenInformation(token, informationClass, buffer, size, out _)
                ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer))
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The process id of a running service, or 0.</summary>
    static uint RunningServiceProcessId(string service)
    {
        var manager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            var handle = OpenService(manager, service, SERVICE_QUERY_STATUS);
            if (handle == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                // SERVICE_STATUS_PROCESS: dwCurrentState at offset 4, dwProcessId at 28.
                var status = new byte[36];
                if (!QueryServiceStatusEx(handle, 0 /* SC_STATUS_PROCESS_INFO */, status, status.Length, out _)
                    || BitConverter.ToUInt32(status, 4) != SERVICE_RUNNING)
                {
                    return 0;
                }

                return BitConverter.ToUInt32(status, 28);
            }
            finally
            {
                CloseServiceHandle(handle);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WaitNamedPipeW")]
    static extern bool WaitNamedPipe(string name, uint timeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr buffer, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    static extern IntPtr OpenService(IntPtr manager, string service, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, byte[] buffer, int size, out int needed);

    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr handle);
}
