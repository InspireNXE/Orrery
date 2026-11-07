using System.Text.Json.Serialization;
using Orrery.Core.Sessions;

namespace Orrery.Core.Connections;

public enum Protocol
{
    Rdp,
    Ssh,
    Vnc,
    Telnet,

    /// <summary>A serial port on this computer, such as COM3 or /dev/ttyUSB0, shown in a terminal. The connection's host is the port's name.</summary>
    Serial,

    /// <summary>A web page, opened in the default browser rather than a tab.</summary>
    Http,

    /// <summary>A web page over TLS, opened in the default browser rather than a tab.</summary>
    Https,

    /// <summary>
    /// A shell on this computer in a terminal tab. The connection's host is the shell's id ("pwsh", "wsl:Ubuntu"),
    /// empty for the one chosen in Settings; <see cref="ConnectionEntry.StartFolder"/> and
    /// <see cref="ConnectionEntry.RunAsAdministrator"/> say how it starts.
    /// </summary>
    Local,
}

public static class ProtocolInfo
{
    /// <summary>The port a protocol uses unless one is set. A serial port has none: it's 0 and never shown.</summary>
    public static int DefaultPort(Protocol protocol) => protocol switch
    {
        Protocol.Rdp => 3389,
        Protocol.Ssh => 22,
        Protocol.Vnc => 5900,
        Protocol.Telnet => 23,
        Protocol.Serial => 0,
        Protocol.Http => 80,
        Protocol.Https => 443,
        Protocol.Local => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static string Label(Protocol protocol) => protocol.ToString().ToUpperInvariant();

    /// <summary>Opens in the default browser: no tab, no session, nothing to reconnect.</summary>
    public static bool IsWeb(Protocol protocol) => protocol is Protocol.Http or Protocol.Https;

    /// <summary>Reached over the network at a host and port, so it can have a jump host and a host check. Not a serial port or a local shell.</summary>
    public static bool UsesNetwork(Protocol protocol) => protocol is not (Protocol.Serial or Protocol.Local);

    /// <summary>A remote desktop drawn in a tab (RDP, VNC).</summary>
    public static bool IsDesktop(Protocol protocol) => protocol is Protocol.Rdp or Protocol.Vnc;

    /// <summary>The protocols Orrery 0.1 can't read, so a file using them is written with a newer schema.</summary>
    internal static bool AddedInSchema3(Protocol protocol) => protocol is Protocol.Serial or Protocol.Http or Protocol.Https;

    /// <summary>The protocols Orrery 0.1.2 can't read.</summary>
    internal static bool AddedInSchema6(Protocol protocol) => protocol is Protocol.Local;
}

/// <summary>RDP: where the remote computer's sound goes.</summary>
public enum RemoteAudio
{
    /// <summary>Played on this computer.</summary>
    PlayHere,

    /// <summary>Not played anywhere; the server doesn't send it.</summary>
    DoNotPlay,
}

/// <summary>How a remote desktop that doesn't follow the window's size is fitted into it (VNC).</summary>
public enum DesktopScaling
{
    /// <summary>Shrunk to fit when it's bigger than the tab, shown as is when it's smaller.</summary>
    ShrinkToFit,

    /// <summary>Shrunk or enlarged to fill the tab, keeping its shape.</summary>
    Fit,

    /// <summary>Pixel for pixel, with scroll bars when it's bigger than the tab.</summary>
    ActualSize,
}

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space,
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two,
}

public enum SerialFlowControl
{
    None,

    /// <summary>Software flow control, with XON and XOFF characters.</summary>
    XOnXOff,

    /// <summary>Hardware flow control, with the RTS and CTS lines.</summary>
    RtsCts,
}

/// <summary>
/// Settings a connection can take from its folder, mRemoteNG style. A null value means "inherit from the
/// parent"; the first non-null value walking up the tree wins, else the default applies.
/// </summary>
public sealed class InheritableSettings
{
    /// <summary>Marks "no saved credential" explicitly, overriding one set on a parent folder.</summary>
    public static readonly Guid NoCredential = Guid.Empty;

    public Protocol? Protocol { get; set; }
    public int? Port { get; set; }

    /// <summary>A saved credential by id; <see cref="NoCredential"/> for none; null to inherit.</summary>
    public Guid? CredentialId { get; set; }

    /// <summary>Used when there's no saved credential; the password is asked for at connect time.</summary>
    public string? Username { get; set; }

    public string? Domain { get; set; }

    /// <summary>Copy and paste between this computer and the remote one (RDP, VNC). Null inherits; on by default.</summary>
    public bool? ShareClipboard { get; set; }

    /// <summary>SSH: log in with the running agent's keys (Pageant, OpenSSH agent) when there's no saved credential. On by default.</summary>
    public bool? UseSshAgent { get; set; }

    /// <summary>
    /// SSH: the path to a private key file on this computer, tried after a saved key credential and before the agent
    /// and the password. A leading ~ is the home folder. Only the path is kept; the file is read when connecting and
    /// never copied. Empty means none, overriding a folder's; null inherits.
    /// </summary>
    public string? KeyFile { get; set; }

    /// <summary>SSH: also accept SHA-1, CBC and 3DES algorithms, for old devices that offer nothing newer. Off by default.</summary>
    public bool? AllowLegacySsh { get; set; }

    /// <summary>RDP: a fixed desktop size like "1920x1080"; empty fits the window; null inherits. See <see cref="DesktopResolution"/>.</summary>
    public string? DesktopSize { get; set; }

    /// <summary>RDP, VNC: enlarge the remote desktop to match this display's scaling. Null inherits; with nothing set, the app setting applies.</summary>
    public bool? EnlargeRemoteDesktop { get; set; }

    /// <summary>
    /// Web connections and management pages: open in a tab in Orrery (true) or in the default browser (false). Null
    /// inherits; with nothing set, the app setting applies.
    /// </summary>
    public bool? WebPagesInOrrery { get; set; }

    /// <summary>
    /// Whether the tree checks this connection's host is up, when Settings has host checks on. Null inherits; on by
    /// default. An import from someone else's file can turn it off for what it brought in.
    /// </summary>
    public bool? CheckHostStatus { get; set; }

    /// <summary>RDP, VNC: how the remote desktop trades looks for speed. Null inherits; balanced by default.</summary>
    public DisplayQuality? DisplayQuality { get; set; }

    /// <summary>
    /// RDP, VNC: correct the remote desktop's colors for the color vision chosen in Settings (true), or show them
    /// unchanged (false), for design or photo work. Null inherits; with nothing set, the app setting applies.
    /// </summary>
    public bool? ColorCorrection { get; set; }

    // The key builds before the switch to American spelling wrote, in connections.json and in exports. Read into
    // ColorCorrection, which wins when a file has both, and never written. Private, so it isn't a setting of its own.
    [JsonInclude, JsonPropertyName("colourCorrection")]
    bool? BritishColorCorrection { get => null; set => ColorCorrection ??= value; }

    /// <summary>Try again by itself when an open session drops (not when it's logged off or closed). Null inherits; on by default.</summary>
    public bool? AutoReconnect { get; set; }

    /// <summary>
    /// Record each session of this connection, from when it first connects (docs/design-session-recording.md).
    /// Null inherits; off by default.
    /// </summary>
    public bool? RecordSessions { get; set; }

    /// <summary>
    /// A terminal recording of this connection keeps what's typed too, keys and pastes, not only what the session sent
    /// back (docs/design-session-recording.md §2.4). Passwords Orrery types, and typing at a password prompt it
    /// recognizes, are left out; one typed at a prompt it doesn't recognize isn't. Null inherits; off by default.
    /// An older build drops it on save, which turns it off.
    /// </summary>
    public bool? RecordTyping { get; set; }

    /// <summary>
    /// Ask before sending any saved command to a session of this connection, whatever the command says, as for a
    /// folder of production machines. Null inherits; off by default.
    /// </summary>
    public bool? ConfirmCommands { get; set; }

    /// <summary>
    /// A saved SSH connection to go through first (ssh -J); RDP, VNC and Telnet reach the machine through an SSH
    /// tunnel from it. <see cref="NoJumpHost"/> for none; null to inherit.
    /// </summary>
    public Guid? JumpHostId { get; set; }

    /// <summary>Marks "connect directly", overriding a jump host set on a parent folder.</summary>
    public static readonly Guid NoJumpHost = Guid.Empty;

    /// <summary>RDP: where the remote computer's sound goes. Null inherits; played here by default.</summary>
    public RemoteAudio? RemoteAudio { get; set; }

    /// <summary>RDP: share every drive on this computer with the remote one. Null inherits; off by default.</summary>
    public bool? ShareAllDrives { get; set; }

    /// <summary>
    /// RDP: folders on this computer to share with the remote one, each shown there as a drive. A leading ~ is the home
    /// folder. Empty means none, overriding a folder's; null inherits. Replaced whole when edited, never changed in place.
    /// </summary>
    public List<string>? SharedFolders { get; set; }

    /// <summary>
    /// RDP and VNC: in full screen, true fills every display of this computer, false the one the window is on. Null
    /// inherits; the one display by default. <see cref="ChosenDisplays"/>, when set on the same node, wins over it.
    /// </summary>
    public bool? UseAllMonitors { get; set; }

    /// <summary>
    /// RDP and VNC: the displays full screen fills, one or several; set, it wins over <see cref="UseAllMonitors"/> on
    /// the same node. Null inherits. Replaced whole when edited, never changed in place.
    /// </summary>
    public List<ChosenDisplay>? ChosenDisplays { get; set; }

    /// <summary>
    /// RDP and VNC on Windows: where Alt+Tab, Alt+Esc and Ctrl+Esc go while the session has the keyboard. Null inherits;
    /// with nothing set, the app setting applies (full screen only, as mstsc does). An older build drops it on save,
    /// which puts it back to the app setting.
    /// </summary>
    public Orrery.Core.Settings.SystemShortcuts? SystemShortcuts { get; set; }

    /// <summary>RDP: connect to the server's console (administrative) session, as mstsc /admin does. Null inherits; off by default.</summary>
    public bool? AdminSession { get; set; }

    /// <summary>VNC: watch without sending keys, clicks or the clipboard. Null inherits; off by default.</summary>
    public bool? ViewOnly { get; set; }

    /// <summary>VNC: how the remote desktop is fitted into the tab. Null inherits; fitted to the tab by default.</summary>
    public DesktopScaling? Scaling { get; set; }

    /// <summary>SSH: seconds between keep-alive messages, which stop idle sessions being dropped; 0 sends none. Null inherits; 30 by default.</summary>
    public int? KeepAliveSeconds { get; set; }

    /// <summary>
    /// SSH: a SOCKS proxy every SSH connection under this opens, like its own <c>D</c> port forward: a port, or an
    /// address and a port ("1080", "127.0.0.1:1080"). Empty means none, overriding a folder's; null inherits.
    /// </summary>
    public string? SocksProxy { get; set; }

    /// <summary>Serial: bits per second. Null inherits; 9600 by default.</summary>
    public int? BaudRate { get; set; }

    /// <summary>Serial: bits in each character, 5 to 8. Null inherits; 8 by default.</summary>
    public int? DataBits { get; set; }

    /// <summary>Serial: null inherits; none by default.</summary>
    public SerialParity? Parity { get; set; }

    /// <summary>Serial: null inherits; one by default.</summary>
    public SerialStopBits? StopBits { get; set; }

    /// <summary>Serial: null inherits; none by default.</summary>
    public SerialFlowControl? FlowControl { get; set; }

    public InheritableSettings Clone()
    {
        var copy = (InheritableSettings)MemberwiseClone();
        copy.SharedFolders = SharedFolders?.ToList();
        copy.ChosenDisplays = ChosenDisplays?.ToList();
        return copy;
    }

    /// <summary>
    /// Settings a guest takes from its host before its folders: the route and the session policy for the
    /// whole machine. See docs/design-host-groups.md §3.
    /// </summary>
    public static readonly IReadOnlySet<string> PassedToGuests = new HashSet<string>
    {
        nameof(JumpHostId), nameof(ShareClipboard), nameof(AutoReconnect), nameof(RecordSessions), nameof(RecordTyping),
    };

    /// <summary>Settings a guest never takes from its host, only from its folders: its login, protocol and display.</summary>
    public static readonly IReadOnlySet<string> KeptByGuests = new HashSet<string>
    {
        nameof(Protocol), nameof(Port), nameof(CredentialId), nameof(Username), nameof(Domain),
        nameof(KeyFile), nameof(UseSshAgent), nameof(AllowLegacySsh), nameof(DesktopSize), nameof(EnlargeRemoteDesktop), nameof(DisplayQuality), nameof(ColorCorrection),
        nameof(RemoteAudio), nameof(ShareAllDrives), nameof(SharedFolders), nameof(UseAllMonitors), nameof(ChosenDisplays), nameof(AdminSession),
        nameof(ViewOnly), nameof(Scaling), nameof(KeepAliveSeconds), nameof(SocksProxy),
        nameof(BaudRate), nameof(DataBits), nameof(Parity), nameof(StopBits), nameof(FlowControl), nameof(WebPagesInOrrery),
        nameof(CheckHostStatus), nameof(ConfirmCommands), nameof(SystemShortcuts),
    };

    /// <summary>
    /// Settings Orrery 0.1 doesn't know. It would load a file that has them and drop them on its next save, so a file
    /// using any of them is written with schema 3, which it refuses instead.
    /// </summary>
    internal static readonly IReadOnlySet<string> AddedInSchema3 = new HashSet<string>
    {
        nameof(RemoteAudio), nameof(ShareAllDrives), nameof(SharedFolders), nameof(UseAllMonitors), nameof(ChosenDisplays), nameof(AdminSession),
        nameof(ViewOnly), nameof(Scaling), nameof(KeepAliveSeconds), nameof(SocksProxy),
        nameof(BaudRate), nameof(DataBits), nameof(Parity), nameof(StopBits), nameof(FlowControl), nameof(RecordSessions), nameof(WebPagesInOrrery),
        nameof(CheckHostStatus),
    };

    /// <summary>Settings Orrery 0.1.1 doesn't know, written with schema 4 for the same reason.</summary>
    internal static readonly IReadOnlySet<string> AddedInSchema4 = new HashSet<string> { nameof(ConfirmCommands) };

    /// <summary>Settings Orrery 0.1.1 doesn't know and added with schema 6, written with it for the same reason.</summary>
    internal static readonly IReadOnlySet<string> AddedInSchema6 = new HashSet<string> { nameof(SystemShortcuts) };

    /// <summary>Whether this uses a protocol, or sets anything, that Orrery 0.1.1 can't read or keep.</summary>
    internal bool NeedsSchema6() =>
        (Protocol is { } protocol && ProtocolInfo.AddedInSchema6(protocol))
        || SettingProperties.Any(p => AddedInSchema6.Contains(p.Name) && p.GetValue(this) is not null);

    /// <summary>Whether this sets anything Orrery 0.1.1 can't keep.</summary>
    internal bool NeedsSchema4() => SettingProperties.Any(p => AddedInSchema4.Contains(p.Name) && p.GetValue(this) is not null);

    /// <summary>Whether this sets anything Orrery 0.1 can't keep: a setting added since, or a protocol added since.</summary>
    internal bool NeedsSchema3() =>
        (Protocol is { } protocol && ProtocolInfo.AddedInSchema3(protocol))
        || SettingProperties.Any(p => AddedInSchema3.Contains(p.Name) && p.GetValue(this) is not null);

    /// <summary>
    /// Takes every value this one inherits (null) from <paramref name="parent"/>, so a node lifted out of its
    /// folder keeps what it had. Every setting is nullable, so new ones are covered without listing them.
    /// <paramref name="only"/> limits it to those settings, such as <see cref="PassedToGuests"/> for a host.
    /// </summary>
    public void FillFrom(InheritableSettings parent, IReadOnlySet<string>? only = null)
    {
        foreach (var property in SettingProperties)
        {
            if (only?.Contains(property.Name) != false && property.GetValue(this) is null)
            {
                // A list is copied, so the two never share one.
                property.SetValue(this, property.GetValue(parent) is List<string> list ? list.ToList() : property.GetValue(parent));
            }
        }
    }

    /// <summary>
    /// Settings set to a number that isn't one of their choices, such as <c>"protocol": 99</c>. JSON takes numbers for
    /// them, so only a file edited by hand or damaged has one.
    /// </summary>
    internal IReadOnlyList<string> UndefinedChoices() =>
        SettingProperties.Where(p => p.GetValue(this) is Enum value && !Enum.IsDefined(value.GetType(), value)).Select(p => p.Name).ToList();

    /// <summary>Sets each of <see cref="UndefinedChoices"/> back to inherit, and returns their names.</summary>
    internal IReadOnlyList<string> ForgetUndefinedChoices()
    {
        var names = UndefinedChoices();
        foreach (var name in names)
        {
            typeof(InheritableSettings).GetProperty(name)!.SetValue(this, null);
        }

        return names;
    }

    static readonly System.Reflection.PropertyInfo[] SettingProperties =
        typeof(InheritableSettings).GetProperties().Where(p => p.CanRead && p.CanWrite).ToArray();
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConnectionFolder), "folder")]
[JsonDerivedType(typeof(ConnectionEntry), "connection")]
public abstract class ConnectionNode
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public InheritableSettings Settings { get; set; } = new();

    /// <summary>
    /// Commands to send to a terminal of this connection, or of every connection in this folder, from the Ctrl+K
    /// palette or the session's menu. A folder's add to its connections' own. Null when there are none.
    /// </summary>
    public List<SavedCommand>? Commands { get; set; }

    /// <summary>Where this came from when imported (e.g. "mremoteng:&lt;id&gt;"), to spot re-imports.</summary>
    public string? ImportSourceId { get; set; }

    [JsonIgnore]
    public ConnectionFolder? Parent { get; internal set; }

    /// <summary>
    /// Folder names from the root down to this node's parent, e.g. "Production / Web"; for a guest, its host's
    /// name comes last ("Production / hv-01").
    /// </summary>
    [JsonIgnore]
    public string Path => string.Join(" / ", Ancestors().Reverse().Where(a => a.Parent is not null).Select(a => a.Name)
        .Concat((this as ConnectionEntry)?.GuestOf is { } host ? [host.Name] : []));

    public IEnumerable<ConnectionFolder> Ancestors()
    {
        for (var folder = Parent; folder is not null; folder = folder.Parent)
        {
            yield return folder;
        }
    }
}

public sealed class ConnectionFolder : ConnectionNode
{
    public bool Expanded { get; set; } = true;

    [JsonIgnore]
    public IReadOnlyList<ConnectionNode> Children => ChildList;

    // Changed only through ConnectionTree, which keeps Parent links right.
    [JsonInclude]
    [JsonPropertyName("children")]
    internal List<ConnectionNode> ChildList { get; set; } = [];

    public IEnumerable<ConnectionNode> Descendants()
    {
        foreach (var child in ChildList)
        {
            yield return child;
            if (child is ConnectionEntry { GuestList: { } guests })
            {
                foreach (var guest in guests)
                {
                    yield return guest;
                }
            }
            else if (child is ConnectionFolder folder)
            {
                foreach (var nested in folder.Descendants())
                {
                    yield return nested;
                }
            }
        }
    }
}

public sealed class ConnectionEntry : ConnectionNode
{
    public string Host { get; set; } = string.Empty;

    /// <summary>The machine's MAC address as <c>00:1A:2B:3C:4D:5E</c>, for Wake-on-LAN. Null when not set. Written normalized by the properties pane and the importers.</summary>
    public string? MacAddress { get; set; }

    /// <summary>Where the Wake-on-LAN packet goes, as an IPv4 address such as 192.168.1.255. Null is 255.255.255.255.</summary>
    public string? WakeBroadcast { get; set; }

    /// <summary>
    /// The machine's management page, such as <c>https://10.20.0.1:8443</c>, opened from the right-click menu. Set on
    /// the connection itself, never inherited. Null when not set. Written normalized by the properties pane and the importers.
    /// </summary>
    public string? ManagementUrl { get; set; }

    /// <summary>Local: the folder the shell starts in; null for the home folder. A leading ~ is the home folder. Not inherited.</summary>
    public string? StartFolder { get; set; }

    /// <summary>Local, on Windows: run the shell as administrator, after a UAC prompt. Written only when true. Not inherited.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RunAsAdministrator { get; set; }

    /// <summary>SSH: ports to forward while the session is open. Null when there are none.</summary>
    public List<PortForward>? PortForwards { get; set; }

    /// <summary>
    /// Connections that run on this one, such as a hypervisor's VMs. Having any makes this a host. One level
    /// only: a guest has no guests. A guest's <see cref="ConnectionNode.Parent"/> is its host's folder.
    /// See docs/design-host-groups.md.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ConnectionEntry> Guests => (IReadOnlyList<ConnectionEntry>?)GuestList ?? [];

    // Changed only through ConnectionTree, which keeps the links right. Null when there are none, so it's left out of the file.
    [JsonInclude]
    [JsonPropertyName("guests")]
    internal List<ConnectionEntry>? GuestList { get; set; }

    /// <summary>The host this is a guest of; null for a connection that isn't a guest.</summary>
    [JsonIgnore]
    public ConnectionEntry? GuestOf { get; internal set; }

    [JsonIgnore]
    public bool IsHost => GuestList is { Count: > 0 };

    /// <summary>The host's row is collapsed. Written only when true, so ordinary connections don't carry it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool GuestsCollapsed { get; set; }
}
