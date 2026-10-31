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
    Resolved<bool> UseSshAgent,
    Resolved<bool> AllowLegacySsh,
    Resolved<Guid?> JumpHostId,
    Resolved<bool> AutoReconnect,
    Resolved<string?> DesktopSize,
    Resolved<bool?> EnlargeRemoteDesktop)
{
    /// <summary>Whether to enlarge the remote desktop: the tree's value, else <paramref name="appSetting"/>.</summary>
    public bool Enlarge(bool appSetting) => EnlargeRemoteDesktop.Value ?? appSetting;
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

        Resolved<bool> Flag(string setting, Func<InheritableSettings, bool?> select, bool fallback) =>
            Find(node, includeSelf, setting, select) is { } found ? new Resolved<bool>(found.Value!.Value, found.Source) : new Resolved<bool>(fallback, null);

        return new ResolvedSettings(
            protocol,
            port,
            credential,
            FindString(node, includeSelf, nameof(InheritableSettings.Username), s => s.Username),
            FindString(node, includeSelf, nameof(InheritableSettings.Domain), s => s.Domain),
            Flag(nameof(InheritableSettings.ShareClipboard), s => s.ShareClipboard, true),
            Flag(nameof(InheritableSettings.UseSshAgent), s => s.UseSshAgent, true),
            Flag(nameof(InheritableSettings.AllowLegacySsh), s => s.AllowLegacySsh, false),
            jump,
            Flag(nameof(InheritableSettings.AutoReconnect), s => s.AutoReconnect, true),
            FindString(node, includeSelf, nameof(InheritableSettings.DesktopSize), s => s.DesktopSize),
            // No Orrery default here: the app's "Enlarge remote desktops" setting fills in, see ResolvedSettings.Enlarge.
            Find(node, includeSelf, nameof(InheritableSettings.EnlargeRemoteDesktop), s => s.EnlargeRemoteDesktop) is { } enlarge
                ? new Resolved<bool?>(enlarge.Value, enlarge.Source)
                : new Resolved<bool?>(null, null));
    }

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
