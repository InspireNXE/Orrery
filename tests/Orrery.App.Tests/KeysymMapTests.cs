using Avalonia.Input;
using Orrery.App.Connections;
using Orrery.App.Input;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.App.Tests;

public class KeysymMapTests
{
    [Theory]
    [InlineData(Key.A, PhysicalKey.A, "a", 0x61u)]
    [InlineData(Key.A, PhysicalKey.A, "A", 0x41u)]           // Shifted: what it types.
    [InlineData(Key.A, PhysicalKey.A, "\u0001", 0x61u)]      // Ctrl+A types a control character.
    [InlineData(Key.D2, PhysicalKey.Digit2, "@", 0x40u)]
    [InlineData(Key.OemQuestion, PhysicalKey.Minus, "ß", 0xDFu)] // German layout: Latin-1 keysym.
    [InlineData(Key.E, PhysicalKey.E, "€", 0x010020ACu)]      // Beyond Latin-1: Unicode keysym.
    [InlineData(Key.Enter, PhysicalKey.Enter, "\r", 0xFF0Du)]
    [InlineData(Key.Enter, PhysicalKey.NumPadEnter, "\r", 0xFF8Du)]
    [InlineData(Key.LeftShift, PhysicalKey.ShiftLeft, null, 0xFFE1u)]
    [InlineData(Key.F5, PhysicalKey.F5, null, 0xFFC2u)]
    [InlineData(Key.NumPad7, PhysicalKey.NumPad7, "7", 0xFFB7u)]
    [InlineData(Key.Space, PhysicalKey.Space, " ", 0x20u)]
    public void Maps_keys_to_keysyms(Key key, PhysicalKey physical, string? text, uint expected)
    {
        Assert.Equal(expected, KeysymMap.Get(key, physical, text));
    }

    [Fact]
    public void A_key_that_types_nothing_known_is_skipped()
    {
        Assert.Null(KeysymMap.Get(Key.None, PhysicalKey.None, null));
    }

    [Theory]
    [InlineData("vnc://box", "box", 5900, Protocol.Vnc)]
    [InlineData("vnc://box:5901", "box", 5901, Protocol.Vnc)]
    [InlineData("telnet://switch", "switch", 23, Protocol.Telnet)]
    [InlineData("telnet://switch:2323", "switch", 2323, Protocol.Telnet)]
    public void Quick_connect_reads_vnc_addresses(string text, string host, int port, Protocol protocol)
    {
        var choice = QuickConnectChoice.Parse(text)!;
        Assert.Equal((host, port, protocol), (choice.Host, choice.Port, choice.Protocol));
    }

    [Theory]
    [InlineData("box", "Rdp:3389,Ssh:22")]
    [InlineData("box:2222", "Rdp:2222,Ssh:2222")]
    [InlineData("admin@box", "Ssh:22,Rdp:3389")]
    [InlineData("vnc://box", "Vnc:5900")]
    [InlineData("", "")]
    public void Quick_connect_offers_rdp_and_ssh_unless_a_scheme_picks_one(string text, string expected)
    {
        var offered = QuickConnectChoice.Candidates(text).Select(c => $"{c.Protocol}:{c.Port}");
        Assert.Equal(expected, string.Join(",", offered));
    }
}
