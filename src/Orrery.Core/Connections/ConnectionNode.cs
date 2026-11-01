using System.Text.Json.Serialization;
using Orrery.Core.Sessions;

namespace Orrery.Core.Connections;

public enum Protocol
{
    Rdp,
    Ssh,
    Vnc,
    Telnet,
}

public static class ProtocolInfo
{
    public static int DefaultPort(Protocol protocol) => protocol switch
    {
        Protocol.Rdp => 3389,
        Protocol.Ssh => 22,
        Protocol.Vnc => 5900,
        Protocol.Telnet => 23,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
    };

    public static string Label(Protocol protocol) => protocol.ToString().ToUpperInvariant();
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
    /// A saved SSH connection to go through first (ssh -J); RDP, VNC and Telnet reach the machine through an SSH
    /// tunnel from it. <see cref="NoJumpHost"/> for none; null to inherit.
    /// </summary>
    public Guid? JumpHostId { get; set; }

    /// <summary>Marks "connect directly", overriding a jump host set on a parent folder.</summary>
    public static readonly Guid NoJumpHost = Guid.Empty;

    public InheritableSettings Clone() => (InheritableSettings)MemberwiseClone();

    /// <summary>
    /// Settings a guest takes from its host before its folders: the route and the session policy for the
    /// whole machine. See docs/design-host-groups.md §3.
    /// </summary>
    public static readonly IReadOnlySet<string> PassedToGuests = new HashSet<string>
    {
        nameof(JumpHostId), nameof(ShareClipboard), nameof(AutoReconnect),
    };

    /// <summary>Settings a guest never takes from its host, only from its folders: its login, protocol and display.</summary>
    public static readonly IReadOnlySet<string> KeptByGuests = new HashSet<string>
    {
        nameof(Protocol), nameof(Port), nameof(CredentialId), nameof(Username), nameof(Domain),
        nameof(KeyFile), nameof(UseSshAgent), nameof(AllowLegacySsh), nameof(DesktopSize), nameof(EnlargeRemoteDesktop), nameof(DisplayQuality), nameof(ColorCorrection),
    };

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
                property.SetValue(this, property.GetValue(parent));
            }
        }
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
