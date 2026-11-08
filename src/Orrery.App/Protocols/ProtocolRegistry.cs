using Orrery.Core.Connections;

namespace Orrery.App.Protocols;

/// <summary>Every protocol's <see cref="ProtocolDescriptor"/>. Adding a protocol means adding its descriptor here.</summary>
static class ProtocolRegistry
{
    /// <summary>In the order the protocol picker lists them: a shell on this computer first, then the rest in the order they were added.</summary>
    public static IReadOnlyList<ProtocolDescriptor> All { get; } =
    [
        new LocalProtocol(),
        new RdpProtocol(),
        new SshProtocol(),
        new VncProtocol(),
        new TelnetProtocol(),
        new SerialProtocol(),
        new WebProtocol(),
    ];

    static readonly Dictionary<Protocol, ProtocolDescriptor> ByProtocol = All
        .SelectMany(d => d.Traits.Select(t => (t.Protocol, Descriptor: d)))
        .ToDictionary(p => p.Protocol, p => p.Descriptor);

    /// <summary>Every protocol, in the picker's order.</summary>
    public static IEnumerable<ProtocolTraits> Listed => All.SelectMany(d => d.Traits);

    public static ProtocolDescriptor For(Protocol protocol) =>
        ByProtocol.TryGetValue(protocol, out var descriptor) ? descriptor : throw new ArgumentOutOfRangeException(nameof(protocol));
}
