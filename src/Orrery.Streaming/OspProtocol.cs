namespace Orrery.Streaming;

/// <summary>
/// Constants of the Orrery Stream Protocol (OSP). A draft: nothing is compatible across versions until it reaches 1.
/// See docs/design-stream.md.
/// </summary>
public static class OspProtocol
{
    /// <summary>The protocol version both sides must agree on; 0 while the design is a draft.</summary>
    public const int Version = 0;

    /// <summary>The ALPN token the QUIC handshake offers, so a host can refuse a client of another version early.</summary>
    public const string Alpn = "osp/0";

    /// <summary>The URI scheme quick connect accepts (<c>osp://host:port</c>).</summary>
    public const string UriScheme = "osp";

    /// <summary>The largest datagram payload sent, header included; QUIC leaves about this much in a 1,280-byte path.</summary>
    public const int MaxDatagramPayload = 1200;
}
