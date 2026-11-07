using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml;
using System.Xml.Linq;

namespace Orrery.Core.Credentials;

/// <summary>
/// Windows Hello's own record of making a key: event 5225 in Microsoft-Windows-HelloForBusiness/Operational, "Creating a
/// hardware Windows Hello key with result 0x0", written by the Windows Hello container service (NgcCtnrSvc) as each key
/// is made. Its KeyProvider field is 1 when the key went into the TPM. Used only when <c>GetAttestationAsync</c> won't
/// vouch for a new key, which happens on computers with a working TPM whose attestation certificate Windows can't get
/// from Microsoft.
/// <para>
/// Programs running as the user can write to that channel, so an event only counts when Windows recorded it as written
/// by the container service's process under a service account. Windows fills in the writer's process and account
/// itself; the writer can't choose them. An event that claims to be the service's but came from anywhere else is taken
/// as tampering, and the key is refused. See docs/design-biometric-unlock.md §2.1.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HelloLog
{
    internal const string Channel = "Microsoft-Windows-HelloForBusiness/Operational";
    internal const string Provider = "Microsoft-Windows-HelloForBusiness";
    internal const string ContainerService = "NgcCtnrSvc";
    internal const int KeyCreatedEvent = 5225;

    /// <summary>KeyProvider 1 is the value Windows renders as "hardware". Nothing else is taken as hardware.</summary>
    internal const uint HardwareProvider = 1;

    /// <summary>LocalSystem, LocalService, NetworkService: the accounts a Windows service runs as.</summary>
    static readonly string[] ServiceAccounts = ["S-1-5-18", "S-1-5-19", "S-1-5-20"];

    // The event is written before the create call returns, but can take a moment to reach the log; and the clocks of
    // Orrery and the event log can differ by a little.
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);
    static readonly TimeSpan WaitForEvent = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    internal readonly record struct KeyCreation(DateTime TimeUtc, uint? KeyProvider, int? Result, uint? ProcessId, string? UserSid);

    internal enum Verdict
    {
        /// <summary>Every key the service made in the window was made in hardware, and there was at least one.</summary>
        Hardware,

        /// <summary>A key made in the window wasn't recorded as hardware, so Orrery can't tell which one is its own.</summary>
        NotHardware,

        /// <summary>No key made in the window was recorded.</summary>
        NoRecord,

        /// <summary>An event in the window wasn't written by the Windows Hello container service.</summary>
        NotFromWindows,

        /// <summary>The log or the service isn't there, or can't be read.</summary>
        Unreadable,
    }

    /// <summary>
    /// What Windows Hello recorded about the keys it made between <paramref name="startedUtc"/> and
    /// <paramref name="finishedUtc"/>: the create call's start and end. Waits a few seconds for the event to arrive.
    /// </summary>
    internal static (Verdict Verdict, string Detail) KeyMadeBetween(DateTime startedUtc, DateTime finishedUtc)
    {
        var from = startedUtc - Slack;
        var until = finishedUtc + Slack;
        var deadline = DateTime.UtcNow + WaitForEvent;
        while (true)
        {
            List<KeyCreation> creations;
            uint service;
            try
            {
                creations = Read(from, until);

                // After reading: the service that wrote them must still be the one running. If it restarted in
                // between, its new process id won't match and the key is refused, which only costs a retry.
                service = ServiceProcessId(ContainerService);
            }
            catch (Exception e) when (e is Win32Exception or XmlException or FormatException or OverflowException)
            {
                return (Verdict.Unreadable, e.Message);
            }

            if (service == 0)
            {
                return (Verdict.Unreadable, $"{ContainerService} isn't running");
            }

            var verdict = Judge(creations, service);
            if (verdict != Verdict.NoRecord || DateTime.UtcNow >= deadline)
            {
                return (verdict, Describe(creations, service));
            }

            Thread.Sleep(Poll);
        }
    }

    /// <summary>
    /// Hardware only when every event in the window was written by the container service's process
    /// (<paramref name="serviceProcessId"/>) under a service account, at least one key was made, and every key made says
    /// hardware. Another program making a key at the same moment could otherwise lend Orrery's software key its hardware
    /// record; an event from anyone else is a forgery, so nothing in the window is trusted.
    /// </summary>
    internal static Verdict Judge(IReadOnlyCollection<KeyCreation> creations, uint serviceProcessId)
    {
        if (serviceProcessId == 0)
        {
            return Verdict.Unreadable;
        }

        if (creations.Any(c => c.ProcessId != serviceProcessId || !ServiceAccounts.Contains(c.UserSid)))
        {
            return Verdict.NotFromWindows;
        }

        var made = creations.Where(c => c.Result == 0).ToList();
        if (made.Count == 0)
        {
            return Verdict.NoRecord;
        }

        return made.All(c => c.KeyProvider == HardwareProvider) ? Verdict.Hardware : Verdict.NotHardware;
    }

    static string Describe(IReadOnlyCollection<KeyCreation> creations, uint service) => creations.Count == 0
        ? "no key creation recorded"
        : string.Join(", ", creations.Select(c => FormattableString.Invariant(
            $"KeyProvider={c.KeyProvider?.ToString(CultureInfo.InvariantCulture) ?? "?"} Result=0x{c.Result ?? -1:X8} from process {c.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "?"} as {c.UserSid ?? "?"}")))
          + FormattableString.Invariant($"; {ContainerService} is process {service}");

    /// <summary>One 5225 event from its XML, or null when it isn't one.</summary>
    internal static KeyCreation? Parse(string eventXml)
    {
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var root = XDocument.Parse(eventXml).Root;
        var system = root?.Element(ns + "System");
        if (system is null
            || system.Element(ns + "Provider")?.Attribute("Name")?.Value != Provider
            || system.Element(ns + "EventID")?.Value != KeyCreatedEvent.ToString(CultureInfo.InvariantCulture)
            || system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value is not { } time)
        {
            return null;
        }

        string? Data(string name) => root!.Element(ns + "EventData")?.Elements(ns + "Data")
            .FirstOrDefault(d => d.Attribute("Name")?.Value == name)?.Value.Trim();

        uint? provider = uint.TryParse(Data("KeyProvider"), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
        int? result = Data("Result") is { } r && r.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(r.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hr) ? hr : null;
        uint? process = uint.TryParse(system.Element(ns + "Execution")?.Attribute("ProcessID")?.Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out var pid) ? pid : null;
        var user = system.Element(ns + "Security")?.Attribute("UserID")?.Value;
        return new KeyCreation(DateTime.Parse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            provider, result, process, user);
    }

    /// <summary>The 5225 events between two times. Throws <see cref="Win32Exception"/> when the log can't be read.</summary>
    internal static List<KeyCreation> Read(DateTime fromUtc, DateTime untilUtc)
    {
        string Stamp(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var query = $"*[System[Provider[@Name='{Provider}'] and (EventID={KeyCreatedEvent}) "
            + $"and TimeCreated[@SystemTime>='{Stamp(fromUtc)}' and @SystemTime<='{Stamp(untilUtc)}']]]";

        var found = new List<KeyCreation>();
        var results = EvtQuery(0, Channel, query, EvtQueryChannelPath | EvtQueryForwardDirection);
        if (results == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var events = new nint[16];
            while (true)
            {
                if (!EvtNext(results, events.Length, events, 0, 0, out int returned))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        return found;
                    }

                    throw new Win32Exception(error);
                }

                for (int i = 0; i < returned; i++)
                {
                    try
                    {
                        if (Parse(Render(events[i])) is { } creation)
                        {
                            found.Add(creation);
                        }
                    }
                    finally
                    {
                        EvtClose(events[i]);
                    }
                }
            }
        }
        finally
        {
            EvtClose(results);
        }
    }

    static string Render(nint evt)
    {
        EvtRender(0, evt, EvtRenderEventXml, 0, 0, out int used, out _);
        int error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error);
        }

        var buffer = Marshal.AllocHGlobal(used);
        try
        {
            if (!EvtRender(0, evt, EvtRenderEventXml, used, buffer, out used, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStringUni(buffer, used / sizeof(char)).TrimEnd('\0');
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The process id of a running service, or 0 when it isn't running.</summary>
    internal static uint ServiceProcessId(string name)
    {
        var manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var service = OpenServiceW(manager, name, ServiceQueryStatus);
            if (service == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return status.CurrentState == ServiceRunning ? status.ProcessId : 0;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    const int EvtQueryChannelPath = 0x1, EvtQueryForwardDirection = 0x100, EvtRenderEventXml = 1;
    const int ErrorNoMoreItems = 259, ErrorInsufficientBuffer = 122;
    const uint ScManagerConnect = 0x1, ServiceQueryStatus = 0x4, ServiceRunning = 4;
    const int ScStatusProcessInfo = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern nint EvtQuery(nint session, string path, string query, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EvtNext(nint resultSet, int count, [Out] nint[] events, int timeout, int flags, out int returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EvtRender(nint context, nint fragment, int flags, int bufferSize, nint buffer, out int bufferUsed, out int propertyCount);

    [DllImport("wevtapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool EvtClose(nint handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern nint OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern nint OpenServiceW(nint manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryServiceStatusEx(nint service, int infoLevel, out ServiceStatusProcess status, int size, out int needed);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseServiceHandle(nint handle);
}
