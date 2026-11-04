using System.Net;
using System.Net.Sockets;

namespace Orrery.Streaming;

/// <summary>
/// Sends frames as plain UDP datagrams to one address: phase 1's transport, before QUIC (docs/design-stream.md §4,
/// §11). No encryption, pacing, FEC or retransmission.
/// </summary>
public sealed class UdpMediaSender : IDisposable
{
    readonly Socket _socket;
    readonly byte[] _buffer = new byte[OspProtocol.MaxDatagramPayload];

    public UdpMediaSender(IPEndPoint destination)
    {
        _socket = new Socket(destination.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { SendBufferSize = 4 * 1024 * 1024 };
        _socket.Connect(destination);
    }

    public long PacketsSent { get; private set; }

    public long BytesSent { get; private set; }

    /// <summary>Sends <paramref name="frame"/> with <paramref name="header"/>'s fields, the packet index and count filled in.</summary>
    public void SendFrame(ReadOnlySpan<byte> frame, MediaPacketHeader header) =>
        FramePacketizer.Split(frame, header, _buffer, datagram =>
        {
            _socket.Send(datagram);
            PacketsSent++;
            BytesSent += datagram.Length;
        });

    public void Dispose() => _socket.Dispose();
}

/// <summary>
/// Receives datagrams from <see cref="UdpMediaSender"/> and puts frames back together. Accepts datagrams from anyone:
/// fine on a test network, and replaced by QUIC with mutual TLS in phase 3.
/// </summary>
public sealed class UdpMediaReceiver : IDisposable
{
    readonly Socket _socket;

    public UdpMediaReceiver(IPEndPoint bind)
    {
        _socket = new Socket(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 4 * 1024 * 1024 };
        _socket.Bind(bind);
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_socket.LocalEndPoint!;

    /// <summary>Only touched by <see cref="Run"/>'s thread while it runs; read its counts after.</summary>
    public FrameAssembler Assembler { get; } = new();

    /// <summary>Receives until disposed, handing each completed frame to <paramref name="onFrame"/> on this thread.</summary>
    public void Run(Action<AssembledFrame> onFrame)
    {
        var buffer = new byte[65536];
        while (true)
        {
            int length;
            try
            {
                length = _socket.Receive(buffer);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports an earlier ICMP port-unreachable this way; nothing to do with this socket's input.
                continue;
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException { SocketErrorCode: SocketError.Interrupted or SocketError.OperationAborted })
            {
                return;
            }

            if (Assembler.Add(buffer.AsSpan(0, length)) is { } frame)
            {
                onFrame(frame);
            }
        }
    }

    public void Dispose() => _socket.Dispose();
}
