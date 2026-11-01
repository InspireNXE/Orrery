using System.Net;
using System.Net.Sockets;
using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Orrery.Core.Network;
using Xunit;

namespace Orrery.Core.Tests.Network;

public class WakeOnLanTests
{
    [Theory]
    [InlineData("00:1A:2B:3C:4D:5E")]
    [InlineData("00-1a-2b-3c-4d-5e")]
    [InlineData("001A.2B3C.4D5E")]
    [InlineData("001a2b3c4d5e")]
    [InlineData("  00:1a:2b:3c:4d:5e  ")]
    [InlineData("0:1a:2b:3c:4d:5e")]
    public void Common_mac_formats_are_read_and_normalized(string text)
    {
        Assert.True(MacAddress.TryNormalize(text, out var normalized));
        Assert.Equal("00:1A:2B:3C:4D:5E", normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("00:1A:2B:3C:4D")]
    [InlineData("00:1A:2B:3C:4D:5E:6F")]
    [InlineData("00:1A:2B:3C:4D:5G")]
    [InlineData("00:1A-2B:3C-4D:5E")]
    [InlineData("001A.2B3C")]
    [InlineData("001A2B3C4D5")]
    [InlineData("000:1A:2B:3C:4D:5E")]
    [InlineData("00:00:00:00:00:00")]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    [InlineData("not a mac")]
    public void Things_that_are_not_mac_addresses_are_refused(string? text) =>
        Assert.False(MacAddress.TryNormalize(text, out _));

    [Fact]
    public void The_magic_packet_is_six_ff_bytes_then_the_mac_sixteen_times()
    {
        MacAddress.TryParse("00:1A:2B:3C:4D:5E", out var mac);
        var packet = WakeOnLan.MagicPacket(mac);

        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(mac, packet.Skip(6 + 6 * i).Take(6).ToArray());
        }
    }

    [Theory]
    [InlineData(null, "255.255.255.255")]
    [InlineData("", "255.255.255.255")]
    [InlineData(" 192.168.1.255 ", "192.168.1.255")]
    public void The_broadcast_address_defaults_to_the_whole_network(string? text, string expected)
    {
        Assert.True(WakeOnLan.TryParseBroadcast(text, out var address));
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData("192.168.1")]
    [InlineData("1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("::1")]
    [InlineData("256.1.1.1")]
    [InlineData("0.0.0.0")]
    [InlineData("printer.local")]
    public void Anything_but_a_dotted_ipv4_address_is_refused_as_a_broadcast_address(string text) =>
        Assert.False(WakeOnLan.TryParseBroadcast(text, out _));

    [Fact]
    public void A_plan_uses_udp_port_9_and_says_why_it_cannot_be_made()
    {
        var entry = new ConnectionEntry { Name = "nas", MacAddress = "00-1a-2b-3c-4d-5e" };
        var plan = WakeOnLan.PlanFor(entry, out var why);
        Assert.NotNull(plan);
        Assert.Null(why);
        Assert.Equal(new IPEndPoint(IPAddress.Broadcast, 9), plan.Destination);

        entry.WakeBroadcast = "10.0.0.255";
        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.0.0.255"), 9), WakeOnLan.PlanFor(entry, out _)!.Destination);

        entry.WakeBroadcast = "nonsense";
        Assert.Null(WakeOnLan.PlanFor(entry, out why));
        Assert.Contains("broadcast", why);

        Assert.Null(WakeOnLan.PlanFor(new ConnectionEntry(), out why));
        Assert.Equal("No MAC address is set.", why);
    }

    [Fact]
    public async Task The_packet_arrives_over_udp()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        MacAddress.TryParse("00:1A:2B:3C:4D:5E", out var mac);

        await WakeOnLan.SendAsync(mac, (IPEndPoint)listener.Client.LocalEndPoint!);
        var received = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WakeOnLan.MagicPacket(mac), received.Buffer);
    }

    // ---- Import and export ----

    static ConnectionTree Sample()
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "nas", Host = "nas.local", MacAddress = "00:1A:2B:3C:4D:5E", WakeBroadcast = "192.168.1.255" }, tree.Root);
        return tree;
    }

    [Fact]
    public void The_mac_and_broadcast_address_survive_an_orrery_export_and_import()
    {
        var tree = Sample();
        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, null, DateTimeOffset.Now);
        var imported = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "x.json");

        var nas = imported.Root.Descendants().OfType<ConnectionEntry>().Single();
        Assert.Equal("00:1A:2B:3C:4D:5E", nas.MacAddress);
        Assert.Equal("192.168.1.255", nas.WakeBroadcast);
    }

    [Fact]
    public void An_orrery_file_with_a_bad_mac_or_broadcast_address_loses_them_with_a_warning()
    {
        var export = ConnectionExporter.Export(Sample().Root, ExportFormat.Orrery, null, DateTimeOffset.Now);
        var edited = export.Content.Replace("00:1A:2B:3C:4D:5E", "zz").Replace("192.168.1.255", "1.2.3");
        var imported = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(edited)), "x.json");

        var nas = imported.Root.Descendants().OfType<ConnectionEntry>().Single();
        Assert.Null(nas.MacAddress);
        Assert.Null(nas.WakeBroadcast);
        Assert.Equal(2, imported.Report.Warnings.Count);
    }

    [Fact]
    public void A_duplicate_keeps_the_mac_address()
    {
        var tree = Sample();
        var copy = (ConnectionEntry)tree.Duplicate(tree.AllConnections().Single());
        Assert.Equal("00:1A:2B:3C:4D:5E", copy.MacAddress);
        Assert.Equal("192.168.1.255", copy.WakeBroadcast);
    }

    [Fact]
    public void The_mac_address_goes_to_and_from_mremoteng_and_the_broadcast_address_is_reported_as_lost()
    {
        var export = ConnectionExporter.Export(Sample().Root, ExportFormat.MRemoteNg, null, DateTimeOffset.Now);
        Assert.Contains("MacAddress=\"00:1A:2B:3C:4D:5E\"", export.Content);
        Assert.Contains(export.Warnings, w => w.Contains("broadcast"));

        var imported = MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "confCons.xml");
        Assert.Equal("00:1A:2B:3C:4D:5E", imported.Root.Descendants().OfType<ConnectionEntry>().Single().MacAddress);
    }

    [Fact]
    public void An_mremoteng_mac_address_in_another_format_is_normalized_and_a_bad_one_warned_about()
    {
        var export = ConnectionExporter.Export(Sample().Root, ExportFormat.MRemoteNg, null, DateTimeOffset.Now);

        var dashed = MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(
            export.Content.Replace("00:1A:2B:3C:4D:5E", "00-1a-2b-3c-4d-5e"))), "confCons.xml");
        Assert.Equal("00:1A:2B:3C:4D:5E", dashed.Root.Descendants().OfType<ConnectionEntry>().Single().MacAddress);

        var bad = MRemoteNgImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(
            export.Content.Replace("00:1A:2B:3C:4D:5E", "nope"))), "confCons.xml");
        Assert.Null(bad.Root.Descendants().OfType<ConnectionEntry>().Single().MacAddress);
        Assert.Contains(bad.Report.Warnings, w => w.Contains("MAC address"));
    }
}
