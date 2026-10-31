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
    Resolved<bool> AutoReconnect);

public static class SettingsResolver
{
    /// <summary>Resolves every inheritable setting for <paramref name="node"/>, itself first, then up the tree.</summary>
    public static ResolvedSettings Resolve(ConnectionNode node)
    {
        var protocol = Find(node, s => s.Protocol) is { } p ? new Resolved<Protocol>(p.Value!.Value, p.Source) : new Resolved<Protocol>(Protocol.Rdp, null);
        var port = Find(node, s => s.Port) is { } port0
            ? new Resolved<int>(port0.Value!.Value, port0.Source)
            : new Resolved<int>(ProtocolInfo.DefaultPort(protocol.Value), null);

        var credential = Find(node, s => s.CredentialId) is { } c
            ? new Resolved<Guid?>(c.Value == InheritableSettings.NoCredential ? null : c.Value, c.Source)
            : new Resolved<Guid?>(null, null);

        var jump = Find(node, s => s.JumpHostId) is { } j
            ? new Resolved<Guid?>(j.Value == InheritableSettings.NoJumpHost ? null : j.Value, j.Source)
            : new Resolved<Guid?>(null, null);

        return new ResolvedSettings(
            protocol,
            port,
            credential,
            FindString(node, s => s.Username),
            FindString(node, s => s.Domain),
            Find(node, s => s.ShareClipboard) is { } clip ? new Resolved<bool>(clip.Value!.Value, clip.Source) : new Resolved<bool>(true, null),
            Find(node, s => s.UseSshAgent) is { } agent ? new Resolved<bool>(agent.Value!.Value, agent.Source) : new Resolved<bool>(true, null),
            Find(node, s => s.AllowLegacySsh) is { } legacy ? new Resolved<bool>(legacy.Value!.Value, legacy.Source) : new Resolved<bool>(false, null),
            jump,
            Find(node, s => s.AutoReconnect) is { } again ? new Resolved<bool>(again.Value!.Value, again.Source) : new Resolved<bool>(true, null));
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

    /// <summary>What <paramref name="node"/> would get if it inherited everything, i.e. its parent's values.</summary>
    public static ResolvedSettings ResolveInherited(ConnectionNode node) =>
        node.Parent is { } parent ? Resolve(parent) : Resolve(new ConnectionFolder());

    static Resolved<T?>? Find<T>(ConnectionNode node, Func<InheritableSettings, T?> select) where T : struct
    {
        for (ConnectionNode? current = node; current is not null; current = current.Parent)
        {
            if (select(current.Settings) is { } value)
            {
                return new Resolved<T?>(value, current);
            }
        }

        return null;
    }

    // An empty string set on a node means "none", overriding a parent's value.
    static Resolved<string?> FindString(ConnectionNode node, Func<InheritableSettings, string?> select)
    {
        for (ConnectionNode? current = node; current is not null; current = current.Parent)
        {
            if (select(current.Settings) is { } value)
            {
                return new Resolved<string?>(value.Length == 0 ? null : value, current);
            }
        }

        return new Resolved<string?>(null, null);
    }
}
