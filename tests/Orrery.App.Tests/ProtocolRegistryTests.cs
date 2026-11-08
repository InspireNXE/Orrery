using Orrery.App.Protocols;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>The app's protocol descriptors: one for every protocol, with nothing left out or listed twice.</summary>
public class ProtocolRegistryTests
{
    [Fact]
    public void Every_protocol_has_exactly_one_descriptor()
    {
        var listed = ProtocolRegistry.Listed.Select(t => t.Protocol).ToList();
        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.Equal(Enum.GetValues<Protocol>().Order(), listed.Order());
        Assert.All(Enum.GetValues<Protocol>(), p => Assert.Contains(ProtocolRegistry.For(p).Traits, t => t.Protocol == p));
    }

    [Fact]
    public void A_descriptors_traits_are_cores()
    {
        Assert.All(ProtocolRegistry.Listed, t => Assert.Same(ProtocolTraits.Of(t.Protocol), t));
    }
}
