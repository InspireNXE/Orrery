using Orrery.Core.Sessions;

namespace Orrery.Core.Connections;

/// <summary>An effective setting and the node it came from; Source is null when the default applies.</summary>
public sealed record Resolved<T>(T Value, ConnectionNode? Source)
{
    public bool IsDefault => Source is null;
}

public sealed record ResolvedSettings(
    Resolved<Protocol> Protocol,
    Resolved<int> Port,
    Resolved<Guid?> CredentialId,
    Resolved<string?> Username,
    Resolved<string?> Domain,
    Resolved<bool> ShareClipboard,
    Resolved<string?> KeyFile,
    Resolved<bool> UseSshAgent,
    Resolved<bool> AllowLegacySsh,
    Resolved<Guid?> JumpHostId,
    Resolved<bool> AutoReconnect,
    Resolved<string?> DesktopSize,
    Resolved<bool?> EnlargeRemoteDesktop,
    Resolved<DisplayQuality> DisplayQuality,
    Resolved<bool> ColorCorrection)
{
    /// <summary>Whether to enlarge the remote desktop: the tree's value, else <paramref name="appSetting"/>.</summary>
    public bool Enlarge(bool appSetting) => EnlargeRemoteDesktop.Value ?? appSetting;

    public required Resolved<RemoteAudio> RemoteAudio { get; init; }
    public required Resolved<bool> ShareAllDrives { get; init; }

    /// <summary>The folders to share, as set (a leading ~ not yet expanded); empty for none.</summary>
    public required Resolved<IReadOnlyList<string>> SharedFolders { get; init; }

    /// <summary>The displays full screen fills: <see cref="InheritableSettings.ChosenDisplays"/> or <see cref="InheritableSettings.UseAllMonitors"/>, whichever node sets one first.</summary>
    public required Resolved<FullScreenChoice> FullScreen { get; init; }
    public required Resolved<bool> AdminSession { get; init; }
    public required Resolved<bool> ViewOnly { get; init; }
    public required Resolved<DesktopScaling> Scaling { get; init; }
    public required Resolved<int> KeepAliveSeconds { get; init; }

    /// <summary>The SOCKS proxy SSH connections open, as set ("1080", "127.0.0.1:1080"); null for none.</summary>
    public required Resolved<string?> SocksProxy { get; init; }

    public required Resolved<int> BaudRate { get; init; }
    public required Resolved<int> DataBits { get; init; }
    public required Resolved<SerialParity> Parity { get; init; }
    public required Resolved<SerialStopBits> StopBits { get; init; }
    public required Resolved<SerialFlowControl> FlowControl { get; init; }
    public required Resolved<bool> RecordSessions { get; init; }

    /// <summary>Where web pages open, as the tree sets it; null leaves it to the app setting, see <see cref="InOrrery"/>.</summary>
    public required Resolved<bool?> WebPagesInOrrery { get; init; }

    /// <summary>Whether the tree checks the host is up, when Settings has host checks on.</summary>
    public required Resolved<bool> CheckHostStatus { get; init; }

    /// <summary>Whether web pages open in an Orrery tab: the tree's value, else <paramref name="appSetting"/>.</summary>
    public bool InOrrery(bool appSetting) => WebPagesInOrrery.Value ?? appSetting;
}

public static class SettingsResolver
{
    /// <summary>
    /// Resolves every inheritable setting for <paramref name="node"/>: itself first, then, for a guest, its host
    /// (only for <see cref="InheritableSettings.PassedToGuests"/>), then up the folders.
    /// </summary>
    public static ResolvedSettings Resolve(ConnectionNode node) => Resolve(node, includeSelf: true);

    static ResolvedSettings Resolve(ConnectionNode node, bool includeSelf)
    {
        var protocol = Find(node, includeSelf, nameof(InheritableSettings.Protocol), s => s.Protocol) is { } p
            ? new Resolved<Protocol>(p.Value!.Value, p.Source)
            : new Resolved<Protocol>(Protocol.Rdp, null);
        var port = Find(node, includeSelf, nameof(InheritableSettings.Port), s => s.Port) is { } port0
            ? new Resolved<int>(port0.Value!.Value, port0.Source)
            : new Resolved<int>(ProtocolInfo.DefaultPort(protocol.Value), null);

        var credential = Find(node, includeSelf, nameof(InheritableSettings.CredentialId), s => s.CredentialId) is { } c
            ? new Resolved<Guid?>(c.Value == InheritableSettings.NoCredential ? null : c.Value, c.Source)
            : new Resolved<Guid?>(null, null);

        var jump = Find(node, includeSelf, nameof(InheritableSettings.JumpHostId), s => s.JumpHostId) is { } j
            ? new Resolved<Guid?>(j.Value == InheritableSettings.NoJumpHost ? null : j.Value, j.Source)
            : new Resolved<Guid?>(null, null);

        Resolved<bool> Flag(string setting, Func<InheritableSettings, bool?> select, bool fallback) => Value(setting, select, fallback);

        Resolved<T> Value<T>(string setting, Func<InheritableSettings, T?> select, T fallback) where T : struct =>
            Find(node, includeSelf, setting, select) is { } found ? new Resolved<T>(found.Value!.Value, found.Source) : new Resolved<T>(fallback, null);

        // An empty list set on a node means "none", overriding a parent's.
        var shared = Chain(node, includeSelf, nameof(InheritableSettings.SharedFolders)).FirstOrDefault(n => n.Settings.SharedFolders is not null) is { } sharedSource
            ? new Resolved<IReadOnlyList<string>>(sharedSource.Settings.SharedFolders!.ToList(), sharedSource)
            : new Resolved<IReadOnlyList<string>>([], null);

        // The two settings are one choice: the nearest node with either decides, so a folder's chosen displays don't
        // outlast a connection's "every display".
        var fullScreen = Chain(node, includeSelf, nameof(InheritableSettings.UseAllMonitors))
            .FirstOrDefault(n => n.Settings.ChosenDisplays is not null || n.Settings.UseAllMonitors is not null) is { } screenSource
            ? new Resolved<FullScreenChoice>(screenSource.Settings.ChosenDisplays is { } chosen
                ? new FullScreenChoice(FullScreenOn.ChosenDisplays, chosen.ToList())
                : screenSource.Settings.UseAllMonitors == true ? FullScreenChoice.EveryDisplay : FullScreenChoice.ThisDisplay, screenSource)
            : new Resolved<FullScreenChoice>(FullScreenChoice.ThisDisplay, null);

        return new ResolvedSettings(
            protocol,
            port,
            credential,
            FindString(node, includeSelf, nameof(InheritableSettings.Username), s => s.Username),
            FindString(node, includeSelf, nameof(InheritableSettings.Domain), s => s.Domain),
            Flag(nameof(InheritableSettings.ShareClipboard), s => s.ShareClipboard, true),
            FindString(node, includeSelf, nameof(InheritableSettings.KeyFile), s => s.KeyFile),
            Flag(nameof(InheritableSettings.UseSshAgent), s => s.UseSshAgent, true),
            Flag(nameof(InheritableSettings.AllowLegacySsh), s => s.AllowLegacySsh, false),
            jump,
            Flag(nameof(InheritableSettings.AutoReconnect), s => s.AutoReconnect, true),
            FindString(node, includeSelf, nameof(InheritableSettings.DesktopSize), s => s.DesktopSize),
            // No Orrery default here: the app's "Enlarge remote desktops" setting fills in, see ResolvedSettings.Enlarge.
            Find(node, includeSelf, nameof(InheritableSettings.EnlargeRemoteDesktop), s => s.EnlargeRemoteDesktop) is { } enlarge
                ? new Resolved<bool?>(enlarge.Value, enlarge.Source)
                : new Resolved<bool?>(null, null),
            Find(node, includeSelf, nameof(InheritableSettings.DisplayQuality), s => s.DisplayQuality) is { } quality
                ? new Resolved<DisplayQuality>(quality.Value!.Value, quality.Source)
                : new Resolved<DisplayQuality>(Sessions.DisplayQuality.Balanced, null),
            // On means "as Settings says", so with no color vision chosen it changes nothing.
            Flag(nameof(InheritableSettings.ColorCorrection), s => s.ColorCorrection, true))
        {
            RemoteAudio = Value(nameof(InheritableSettings.RemoteAudio), s => s.RemoteAudio, Connections.RemoteAudio.PlayHere),
            // No Orrery default here either: the app's "Open web pages in" setting fills in, see ResolvedSettings.InOrrery.
            WebPagesInOrrery = Find(node, includeSelf, nameof(InheritableSettings.WebPagesInOrrery), s => s.WebPagesInOrrery) is { } web
                ? new Resolved<bool?>(web.Value, web.Source)
                : new Resolved<bool?>(null, null),
            ShareAllDrives = Flag(nameof(InheritableSettings.ShareAllDrives), s => s.ShareAllDrives, false),
            SharedFolders = shared,
            FullScreen = fullScreen,
            AdminSession = Flag(nameof(InheritableSettings.AdminSession), s => s.AdminSession, false),
            ViewOnly = Flag(nameof(InheritableSettings.ViewOnly), s => s.ViewOnly, false),
            Scaling = Value(nameof(InheritableSettings.Scaling), s => s.Scaling, DesktopScaling.Fit),
            KeepAliveSeconds = Value(nameof(InheritableSettings.KeepAliveSeconds), s => s.KeepAliveSeconds, DefaultKeepAliveSeconds),
            SocksProxy = FindString(node, includeSelf, nameof(InheritableSettings.SocksProxy), s => s.SocksProxy),
            BaudRate = Value(nameof(InheritableSettings.BaudRate), s => s.BaudRate, DefaultBaudRate),
            DataBits = Value(nameof(InheritableSettings.DataBits), s => s.DataBits, DefaultDataBits),
            Parity = Value(nameof(InheritableSettings.Parity), s => s.Parity, SerialParity.None),
            StopBits = Value(nameof(InheritableSettings.StopBits), s => s.StopBits, SerialStopBits.One),
            FlowControl = Value(nameof(InheritableSettings.FlowControl), s => s.FlowControl, SerialFlowControl.None),
            RecordSessions = Flag(nameof(InheritableSettings.RecordSessions), s => s.RecordSessions, false),
            CheckHostStatus = Flag(nameof(InheritableSettings.CheckHostStatus), s => s.CheckHostStatus, true),
        };
    }

    /// <summary>SSH keep-alive interval when nothing sets one, as before it could be set.</summary>
    public const int DefaultKeepAliveSeconds = 30;

    /// <summary>The serial line settings most consoles use: 9600 8N1, no flow control.</summary>
    public const int DefaultBaudRate = 9600, DefaultDataBits = 8;

    /// <summary>
    /// The jump hosts to go through to reach <paramref name="entry"/>, outermost first: its jump host's own
    /// jump host comes before it. Null, with <paramref name="error"/> set, when one is missing, isn't SSH,
    /// has no host, loops back, or the chain is longer than <see cref="MaxJumpHosts"/>.
    /// </summary>
    public static List<ConnectionEntry>? JumpChain(ConnectionEntry entry, ConnectionTree tree, out string? error)
    {
        error = null;
        var chain = new List<ConnectionEntry>();
        var seen = new HashSet<Guid> { entry.Id };
        for (var current = entry; Resolve(current).JumpHostId.Value is { } id; )
        {
            if (tree.Find(id) is not ConnectionEntry jump)
            {
                error = $"The jump host for \"{current.Name}\" no longer exists. Choose another in its settings.";
                return null;
            }

            if (!seen.Add(jump.Id))
            {
                error = $"\"{jump.Name}\" is reached through itself. Change the jump host of one of the connections in the loop.";
                return null;
            }

            if (Resolve(jump).Protocol.Value != Protocol.Ssh || string.IsNullOrWhiteSpace(jump.Host))
            {
                error = $"The jump host \"{jump.Name}\" must be an SSH connection with a host.";
                return null;
            }

            if (chain.Count == MaxJumpHosts)
            {
                error = $"\"{entry.Name}\" goes through more than {MaxJumpHosts} jump hosts.";
                return null;
            }

            chain.Insert(0, jump);
            current = jump;
        }

        return chain;
    }

    public const int MaxJumpHosts = 5;

    /// <summary>What <paramref name="node"/> would get if it inherited everything: its host's and folders' values.</summary>
    public static ResolvedSettings ResolveInherited(ConnectionNode node) => Resolve(node, includeSelf: false);

    /// <summary>The nodes a setting is looked up on, nearest first.</summary>
    static IEnumerable<ConnectionNode> Chain(ConnectionNode node, bool includeSelf, string setting)
    {
        if (includeSelf)
        {
            yield return node;
        }

        if (node is ConnectionEntry { GuestOf: { } host } && InheritableSettings.PassedToGuests.Contains(setting))
        {
            yield return host;
        }

        for (var folder = node.Parent; folder is not null; folder = folder.Parent)
        {
            yield return folder;
        }
    }

    static Resolved<T?>? Find<T>(ConnectionNode node, bool includeSelf, string setting, Func<InheritableSettings, T?> select) where T : struct
    {
        foreach (var current in Chain(node, includeSelf, setting))
        {
            if (select(current.Settings) is { } value)
            {
                return new Resolved<T?>(value, current);
            }
        }

        return null;
    }

    // An empty string set on a node means "none", overriding a parent's value.
    static Resolved<string?> FindString(ConnectionNode node, bool includeSelf, string setting, Func<InheritableSettings, string?> select)
    {
        foreach (var current in Chain(node, includeSelf, setting))
        {
            if (select(current.Settings) is { } value)
            {
                return new Resolved<string?>(value.Length == 0 ? null : value, current);
            }
        }

        return new Resolved<string?>(null, null);
    }
}
