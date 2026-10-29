using System.Text.Json.Serialization;

namespace Orrery.Core.Connections;

public enum Protocol
{
    Rdp,
    Ssh,
    Vnc,
}

public static class ProtocolInfo
{
    public static int DefaultPort(Protocol protocol) => protocol switch
    {
        Protocol.Rdp => 3389,
        Protocol.Ssh => 22,
        Protocol.Vnc => 5900,
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

    public InheritableSettings Clone() => (InheritableSettings)MemberwiseClone();
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

    [JsonIgnore]
    public ConnectionFolder? Parent { get; internal set; }

    /// <summary>Folder names from the root down to this node's parent, e.g. "Production / Web".</summary>
    [JsonIgnore]
    public string Path => string.Join(" / ", Ancestors().Reverse().Where(a => a.Parent is not null).Select(a => a.Name));

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
            if (child is ConnectionFolder folder)
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
}
