using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

/// <summary>The protocol table itself: one entry per protocol, and nothing in it that contradicts the rest.</summary>
public class ProtocolTraitsTests
{
    [Fact]
    public void Every_protocol_has_traits_once_in_the_enums_order()
    {
        Assert.Equal(Enum.GetValues<Protocol>(), Protocols.All.Select(t => t.Protocol));
        Assert.All(Enum.GetValues<Protocol>(), p => Assert.Equal(p, Protocols.Get(p).Protocol));
    }

    [Fact]
    public void An_unknown_protocol_number_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Protocols.Get((Protocol)99));
    }

    [Fact]
    public void Schemes_and_mremoteng_names_belong_to_one_protocol_each()
    {
        var schemes = Protocols.All.SelectMany(t => t.Schemes).ToList();
        Assert.Equal(schemes.Count, schemes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var names = Protocols.All.SelectMany(t => t.MRemoteNgNames).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void What_isnt_on_the_network_says_why_and_has_no_port_check_or_jump_hosts()
    {
        foreach (var traits in Protocols.All)
        {
            bool local = traits.Reach == ProtocolReach.ThisComputer;
            Assert.Equal(local, traits.NotOnNetwork is not null);
            if (local)
            {
                Assert.Equal(HostProbe.None, traits.HostProbe);
                Assert.Equal(JumpHostSupport.None, traits.JumpHosts);
                Assert.Equal(0, traits.DefaultPort);
            }
        }
    }

    [Fact]
    public void A_schema_is_never_older_than_the_first()
    {
        Assert.All(Protocols.All, t => Assert.InRange(t.AddedInSchema, 1, 6));
    }

    [Theory]
    [InlineData("RDP", Protocol.Rdp)]
    [InlineData("ssh2", Protocol.Ssh)]
    [InlineData("SSH1", Protocol.Ssh)]
    [InlineData("VNC", Protocol.Vnc)]
    [InlineData("Telnet", Protocol.Telnet)]
    [InlineData("HTTP", Protocol.Http)]
    [InlineData("HTTPS", Protocol.Https)]
    [InlineData("RAW", null)]
    [InlineData("Rlogin", null)]
    public void Mremoteng_names_map_as_before(string name, Protocol? protocol)
    {
        Assert.Equal(protocol, Protocols.FromMRemoteNg(name));
    }
}
