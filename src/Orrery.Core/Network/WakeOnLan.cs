using System.Net;
using System.Net.Sockets;
using Orrery.Core.Connections;

namespace Orrery.Core.Network;

/// <summary>Wakes a sleeping computer with a magic packet: six 0xFF bytes, then its MAC address sixteen times, sent over UDP.</summary>
public static class WakeOnLan
{
    public const int Port = 9;
    public const int PacketLength = 6 + 16 * MacAddress.Length;

    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != MacAddress.Length)
        {
            throw new ArgumentException("A MAC address has six bytes.", nameof(mac));
        }

        var packet = new byte[PacketLength];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (int i = 0; i < 16; i++)
        {
            mac.CopyTo(packet, 6 + i * MacAddress.Length);
        }

        return packet;
    }

    /// <summary>
    /// Reads the broadcast address a connection stores. Empty is the whole-network default, 255.255.255.255. Only an
    /// IPv4 address in dotted form is accepted: IPv6 has no broadcast, and "1" or "0x7f.1" would be read as something else.
    /// </summary>
    public static bool TryParseBroadcast(string? text, out IPAddress address)
    {
        address = IPAddress.Broadcast;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (value.Count(c => c == '.') != 3 || !value.All(c => char.IsAsciiDigit(c) || c == '.')
            || !IPAddress.TryParse(value, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork
            || parsed.Equals(IPAddress.Any))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    /// <summary>A packet to send: whose MAC address, and where to.</summary>
    public sealed record Plan(byte[] Mac, IPEndPoint Destination);

    /// <summary>What to send to wake <paramref name="entry"/>, or null with <paramref name="whyNot"/> when its MAC address or broadcast address is missing or unreadable.</summary>
    public static Plan? PlanFor(ConnectionEntry entry, out string? whyNot)
    {
        whyNot = null;
        if (!MacAddress.TryParse(entry.MacAddress, out var mac))
        {
            whyNot = string.IsNullOrWhiteSpace(entry.MacAddress) ? "No MAC address is set." : "The MAC address isn't valid.";
            return null;
        }

        if (!TryParseBroadcast(entry.WakeBroadcast, out var broadcast))
        {
            whyNot = "The broadcast address isn't valid.";
            return null;
        }

        return new Plan(mac, new IPEndPoint(broadcast, Port));
    }

    /// <summary>Sends the magic packet for <paramref name="mac"/> to <paramref name="destination"/>, usually a broadcast address and <see cref="Port"/>.</summary>
    /// <exception cref="SocketException">The network refused it, for example when there is none.</exception>
    public static async Task SendAsync(byte[] mac, IPEndPoint destination, CancellationToken cancellationToken = default)
    {
        using var client = new UdpClient(destination.AddressFamily) { EnableBroadcast = true };
        await client.SendAsync(MagicPacket(mac), destination, cancellationToken);
    }
}
