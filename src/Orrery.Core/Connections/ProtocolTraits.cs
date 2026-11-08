namespace Orrery.Core.Connections;

/// <summary>What a session of a protocol shows.</summary>
public enum SessionKind
{
    /// <summary>A remote desktop drawn in a tab (RDP, VNC).</summary>
    Desktop,

    /// <summary>A terminal (SSH, Telnet, a serial port, a local shell).</summary>
    Terminal,

    /// <summary>A web page, in a tab or the default browser.</summary>
    Web,
}

/// <summary>Where a protocol's connections are.</summary>
public enum ProtocolReach
{
    /// <summary>At a host and port on the network.</summary>
    Network,

    /// <summary>On this computer: a serial port or a shell. The connection's host names it.</summary>
    ThisComputer,
}

/// <summary>Whether a protocol can be reached through SSH jump hosts, and how.</summary>
public enum JumpHostSupport
{
    /// <summary>It can't: the jump host setting isn't shown.</summary>
    None,

    /// <summary>Through a tunnel the jump hosts make (RDP, VNC, Telnet).</summary>
    Tunnel,

    /// <summary>The protocol goes through them itself, and can be a jump host (SSH).</summary>
    Native,
}

/// <summary>How a protocol's host is checked for being up.</summary>
public enum HostProbe
{
    /// <summary>Not checked.</summary>
    None,

    /// <summary>By opening a TCP connection to its port.</summary>
    Tcp,
}

/// <summary>Whether what a protocol sends is encrypted on the way.</summary>
public enum WireEncryption
{
    /// <summary>Nothing crosses a wire: a shell on this computer.</summary>
    NotApplicable,

    Always,

    Never,
}

/// <summary>What a protocol logs in with, so which credential settings it shows and what's read before connecting.</summary>
public enum LoginKind
{
    /// <summary>Nothing: a serial port, a local shell, a web page.</summary>
    None,

    /// <summary>A username at most, which the server is told; it asks for the rest in the terminal (Telnet).</summary>
    Username,

    /// <summary>A username and password, from a saved credential or a prompt (RDP, VNC).</summary>
    Password,

    /// <summary>A password, a key file or the SSH agent's keys (SSH).</summary>
    SshKeys,
}

/// <summary>
/// What one protocol is: everything about it that isn't drawing it. Orrery.App adds the badge, the session and the
/// properties section in its own descriptor; this is what Core (the store, import, export, host checks) needs.
/// </summary>
/// <param name="Label">The badge and lists: "RDP", "TELNET".</param>
/// <param name="Name">In sentences: "RDP", "Telnet".</param>
/// <param name="DefaultPort">The port used unless one is set; 0 when it has none, which is never shown.</param>
/// <param name="Schemes">URI schemes that name it ("rdp" for rdp://host), for quick connect.</param>
/// <param name="HasDomain">Whether its login has a domain (RDP).</param>
/// <param name="AddedInSchema">The connections file schema that added it. Older builds can't read it, so a file using it is written with this schema at least.</param>
/// <param name="MRemoteNgNames">What mRemoteNG calls it; the first is written on export. Empty when mRemoteNG has no such protocol and the connection is left out.</param>
/// <param name="NotOnNetwork">For <see cref="ProtocolReach.ThisComputer"/>: why its host isn't checked.</param>
public sealed record ProtocolTraits(
    Protocol Protocol,
    string Label,
    string Name,
    int DefaultPort,
    IReadOnlyList<string> Schemes,
    SessionKind Kind,
    ProtocolReach Reach,
    JumpHostSupport JumpHosts,
    HostProbe HostProbe,
    WireEncryption Encryption,
    LoginKind Login,
    bool HasDomain,
    int AddedInSchema,
    IReadOnlyList<string> MRemoteNgNames,
    string? NotOnNetwork = null)
{
    /// <summary>Every protocol's traits, in the enum's order.</summary>
    public static IReadOnlyList<ProtocolTraits> All { get; } =
    [
        new(Protocol.Rdp, "RDP", "RDP", 3389, ["rdp"], SessionKind.Desktop, ProtocolReach.Network, JumpHostSupport.Tunnel,
            HostProbe.Tcp, WireEncryption.Always, LoginKind.Password, HasDomain: true, AddedInSchema: 1, ["RDP"]),
        new(Protocol.Ssh, "SSH", "SSH", 22, ["ssh"], SessionKind.Terminal, ProtocolReach.Network, JumpHostSupport.Native,
            HostProbe.Tcp, WireEncryption.Always, LoginKind.SshKeys, HasDomain: false, AddedInSchema: 1, ["SSH2", "SSH1"]),
        // Only VNC's classic security types are offered, so nothing is encrypted.
        new(Protocol.Vnc, "VNC", "VNC", 5900, ["vnc"], SessionKind.Desktop, ProtocolReach.Network, JumpHostSupport.Tunnel,
            HostProbe.Tcp, WireEncryption.Never, LoginKind.Password, HasDomain: false, AddedInSchema: 1, ["VNC"]),
        new(Protocol.Telnet, "TELNET", "Telnet", 23, ["telnet"], SessionKind.Terminal, ProtocolReach.Network, JumpHostSupport.Tunnel,
            HostProbe.Tcp, WireEncryption.Never, LoginKind.Username, HasDomain: false, AddedInSchema: 1, ["Telnet"]),
        // A serial line is a cable: nothing on it is encrypted, though it crosses no network.
        new(Protocol.Serial, "SERIAL", "Serial", 0, ["serial"], SessionKind.Terminal, ProtocolReach.ThisComputer, JumpHostSupport.None,
            HostProbe.None, WireEncryption.Never, LoginKind.None, HasDomain: false, AddedInSchema: 3, [],
            NotOnNetwork: "a serial port on this computer, not a host on the network"),
        new(Protocol.Http, "HTTP", "HTTP", 80, ["http"], SessionKind.Web, ProtocolReach.Network, JumpHostSupport.None,
            HostProbe.Tcp, WireEncryption.Never, LoginKind.None, HasDomain: false, AddedInSchema: 3, ["HTTP"]),
        new(Protocol.Https, "HTTPS", "HTTPS", 443, ["https"], SessionKind.Web, ProtocolReach.Network, JumpHostSupport.None,
            HostProbe.Tcp, WireEncryption.Always, LoginKind.None, HasDomain: false, AddedInSchema: 3, ["HTTPS"]),
        new(Protocol.Local, "LOCAL", "Local", 0, [], SessionKind.Terminal, ProtocolReach.ThisComputer, JumpHostSupport.None,
            HostProbe.None, WireEncryption.NotApplicable, LoginKind.None, HasDomain: false, AddedInSchema: 6, [],
            NotOnNetwork: "a shell on this computer, not a host on the network"),
    ];

    static readonly Dictionary<Protocol, ProtocolTraits> ByProtocol = All.ToDictionary(t => t.Protocol);

    public static ProtocolTraits Of(Protocol protocol) =>
        ByProtocol.TryGetValue(protocol, out var traits) ? traits : throw new ArgumentOutOfRangeException(nameof(protocol));

    /// <summary>The protocol mRemoteNG calls <paramref name="name"/> ("SSH2"), ignoring case; null when Orrery has none.</summary>
    public static Protocol? FromMRemoteNg(string name) =>
        All.FirstOrDefault(t => t.MRemoteNgNames.Contains(name, StringComparer.OrdinalIgnoreCase))?.Protocol;
}
