using System.Text;
using Devolutions.IronRdp;
using Orrery.Core.Sessions;

namespace Orrery.Protocols.Rdp;

/// <summary>
/// What a Server Redirection PDU said ([MS-RDPBCGR] 2.2.13.1): an RD Connection Broker sending the client on to the
/// session host that holds, or will hold, the user's session. Read out of IronRDP's object so the next hop can be
/// worked out, logged and tested without it.
/// </summary>
/// <remarks>
/// The next connection carries back what the broker sent (<see cref="RdpConnector"/>): the routing token
/// (<see cref="LoadBalanceInfo"/>), the session to rejoin and the password cookie, as IronRDP's own client does. The
/// login still goes through CredSSP with the password Orrery already has; the cookie only goes in the client info
/// that follows, for the broker's own use, and is never logged.
/// </remarks>
internal sealed record RdpRedirection(
    uint Flags,
    string? TargetFqdn,
    string? TargetNetAddress,
    string? TargetNetbiosName,
    string? Username,
    string? Domain,
    byte[] LoadBalanceInfo,
    uint SessionId,
    byte[]? PasswordCookie = null)
{
    /// <summary>How many redirections in a row a connection follows before giving up.</summary>
    public const int MaxHops = 5;

    // [MS-RDPBCGR] 2.2.13.1 RDP_SERVER_REDIRECTION_PACKET, RedirFlags.
    internal const uint TargetNetAddressFlag = 0x1, LoadBalanceInfoFlag = 0x2, UsernameFlag = 0x4, DomainFlag = 0x8,
        PasswordFlag = 0x10, NoRedirectFlag = 0x80, TargetFqdnFlag = 0x100, TargetNetbiosNameFlag = 0x200;

    /// <summary>The broker wants the client to connect to it again, sending the routing token, rather than move.</summary>
    public bool StayOnServer => (Flags & NoRedirectFlag) != 0;

    /// <summary>The broker sent a password cookie, passed back in the next connection (see the remarks).</summary>
    public bool HasPassword => (Flags & PasswordFlag) != 0;

    /// <summary>The routing token to send back: what the broker sent, when its flag says there is one.</summary>
    public byte[] RoutingToken => (Flags & LoadBalanceInfoFlag) != 0 ? LoadBalanceInfo : [];

    /// <summary>The password cookie to send back, when the broker sent one.</summary>
    public byte[] Cookie => HasPassword && PasswordCookie is { } cookie ? cookie : [];

    /// <summary>
    /// The host to connect to next: the server's full name, else its address, else its NetBIOS name, the order FreeRDP
    /// prefers them in. Null when the redirection names none (or says to stay, <see cref="StayOnServer"/>).
    /// </summary>
    public string? NextHost =>
        StayOnServer ? null
        : (Flags & TargetFqdnFlag) != 0 && !string.IsNullOrWhiteSpace(TargetFqdn) ? TargetFqdn.Trim()
        : (Flags & TargetNetAddressFlag) != 0 && !string.IsNullOrWhiteSpace(TargetNetAddress) ? TargetNetAddress.Trim()
        : (Flags & TargetNetbiosNameFlag) != 0 && !string.IsNullOrWhiteSpace(TargetNetbiosName) ? TargetNetbiosName.Trim()
        : null;

    /// <summary>
    /// Where to connect next, on the same port: the host the redirection names, or the same server again when it asks
    /// to be called back with its routing token. The login is as the redirection says when it names the user and
    /// domain, else as before. Null when there's nowhere to go.
    /// </summary>
    public ConnectionTarget? Next(ConnectionTarget current) => (StayOnServer ? current.Host : NextHost) is { } host
        ? new ConnectionTarget(host, current.Port,
            (Flags & UsernameFlag) != 0 && !string.IsNullOrWhiteSpace(Username) ? Username : current.Username,
            (Flags & DomainFlag) != 0 && Domain is not null ? Domain : current.Domain)
        : null;

    /// <summary>The routing token as text, for the log: brokers send "Cookie: msts=..." or "tsv://...".</summary>
    public string RoutingTokenText => LoadBalanceInfo.Length == 0 ? "none" : Encoding.ASCII.GetString(LoadBalanceInfo).TrimEnd('\r', '\n', '\0');

    /// <summary>Reads IronRDP's redirection. A field the PDU doesn't carry reads as null, or empty.</summary>
    public static RdpRedirection From(ServerRedirection pdu) =>
        new(pdu.Flags, Read(pdu.GetTargetFqdn), Read(pdu.GetTargetNetAddress), Read(pdu.GetTargetNetbiosName),
            Read(pdu.GetUsername), Read(pdu.GetDomain), Bytes(() => pdu.LoadBalanceInfo), pdu.SessionId, Bytes(() => pdu.Password));

    static byte[] Bytes(Func<VecU8> get)
    {
        try
        {
            using var vec = get();
            var bytes = new byte[vec.Size];
            vec.Fill(bytes);
            return bytes;
        }
        catch (IronRdpException)
        {
            return [];
        }
    }

    static string? Read(Func<string> get)
    {
        try
        {
            return get() is { Length: > 0 } value ? value.TrimEnd('\0') : null;
        }
        catch (IronRdpException)
        {
            return null;
        }
    }
}

/// <summary>The server sent the connection elsewhere instead of letting it in; <see cref="RdpSession"/> follows it.</summary>
internal sealed class ServerRedirectedException(RdpRedirection redirection)
    : Exception($"The server redirected the connection to {redirection.NextHost ?? "itself"}.")
{
    public RdpRedirection Redirection { get; } = redirection;
}
