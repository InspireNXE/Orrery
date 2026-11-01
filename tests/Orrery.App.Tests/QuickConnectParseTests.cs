using Orrery.App.Connections;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.App.Tests;

public class QuickConnectParseTests
{
    [Theory]
    [InlineData("https://router.lan", "router.lan", 443, Protocol.Https)]
    [InlineData("http://router.lan:8080/admin", "router.lan/admin", 8080, Protocol.Http)]
    [InlineData("serial://COM3", "COM3", 0, Protocol.Serial)]
    [InlineData("com4", "COM4", 0, Protocol.Serial)]
    [InlineData("/dev/ttyUSB0", "/dev/ttyUSB0", 0, Protocol.Serial)]
    [InlineData("serial:///dev/ttyS1", "/dev/ttyS1", 0, Protocol.Serial)]
    public void Reads_web_addresses_and_serial_ports(string text, string host, int port, Protocol protocol)
    {
        var choice = QuickConnectChoice.Parse(text)!;
        Assert.Equal((host, port, protocol), (choice.Host, choice.Port, choice.Protocol));
    }

    [Theory]
    [InlineData("https://user@router.lan")]
    [InlineData("https://")]
    [InlineData("serial://")]
    [InlineData("serial://COM 3")]
    public void Refuses_what_it_cannot_open(string text)
    {
        Assert.Null(QuickConnectChoice.Parse(text));
    }

    [Theory]
    [InlineData("https://router.lan", "Https:443")]
    [InlineData("COM3", "Serial:0")]
    [InlineData("commander", "Rdp:3389,Ssh:22")]
    [InlineData("com12345", "Rdp:3389,Ssh:22")]
    public void Web_and_serial_are_offered_alone(string text, string expected)
    {
        Assert.Equal(expected, string.Join(",", QuickConnectChoice.Candidates(text).Select(c => $"{c.Protocol}:{c.Port}")));
    }
}
