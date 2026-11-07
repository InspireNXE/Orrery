using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Orrery.Core.Sessions;

/// <summary>
/// How a session's connection is doing, for the session pane's Connection card and the tab's mark: the data each way,
/// the time spent decoding it, the round trip, and how long connecting and resizing took. (A remote desktop's
/// screen updates are counted by its view.) The session counts as it goes, from any
/// thread; the view reads it about once a second and works out the rates.
/// </summary>
public sealed class LinkStats
{
    long _received, _sent, _busyTicks;
    Socket? _socket;
    long _connectTicks = -1, _resizeTicks = -1;

    /// <summary>The session's data as bytes received, after decryption where the session can't see the wire (SSH).</summary>
    public void Received(long bytes) => Interlocked.Add(ref _received, bytes);

    public void Sent(long bytes) => Interlocked.Add(ref _sent, bytes);

    /// <summary>Time spent turning what arrived into pictures, to tell a slow link from a busy computer.</summary>
    public void Busy(TimeSpan time) => Interlocked.Add(ref _busyTicks, time.Ticks);

    /// <summary>
    /// The socket straight to the server, for the round trip; leave unset when the connection goes through a tunnel,
    /// whose socket only reaches this computer.
    /// </summary>
    public Socket? Socket
    {
        get => Volatile.Read(ref _socket);
        set => Volatile.Write(ref _socket, value);
    }

    /// <summary>How long connecting took, from opening the connection to the session starting.</summary>
    public TimeSpan? ConnectTime
    {
        get => Ticks(Interlocked.Read(ref _connectTicks));
        set => Interlocked.Exchange(ref _connectTicks, value?.Ticks ?? -1);
    }

    /// <summary>How long the server took to change the desktop size the last time it was asked to.</summary>
    public TimeSpan? LastResize
    {
        get => Ticks(Interlocked.Read(ref _resizeTicks));
        set => Interlocked.Exchange(ref _resizeTicks, value?.Ticks ?? -1);
    }

    static TimeSpan? Ticks(long ticks) => ticks < 0 ? null : TimeSpan.FromTicks(ticks);

    public LinkReading Read() => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _sent),
        TimeSpan.FromTicks(Interlocked.Read(ref _busyTicks)),
        Socket is { } socket ? TcpRoundTrip.Of(socket) : null);
}

/// <summary>The totals so far, and the round trip right now (null where it can't be measured).</summary>
public readonly record struct LinkReading(long Received, long Sent, TimeSpan Busy, TimeSpan? RoundTrip);

/// <summary>A session that keeps <see cref="LinkStats"/>.</summary>
public interface ILinkStatsSession
{
    LinkStats Link { get; }
}

/// <summary>
/// The round trip the operating system measures for a TCP connection (its smoothed RTT), so nothing extra is sent to
/// the server. Null where the system doesn't say, or the socket is closed.
/// </summary>
public static class TcpRoundTrip
{
    // Windows: SIO_TCP_INFO with TCP_INFO_v0, whose RttUs is at offset 20 (Windows 10 1703 and later).
    const int SioTcpInfo = unchecked((int)0xD8000027);
    const int WindowsRttOffset = 20;

    // Linux: TCP_INFO's tcpi_rtt, in microseconds. macOS: TCP_CONNECTION_INFO's tcpi_srtt, in milliseconds.
    const int IpProtoTcp = 6;
    const int LinuxTcpInfo = 11, LinuxRttOffset = 68;
    const int MacTcpConnectionInfo = 0x106, MacSrttOffset = 44;

    public static TimeSpan? Of(Socket? socket)
    {
        try
        {
            if (socket is not { Connected: true })
            {
                return null;
            }

            if (OperatingSystem.IsWindows())
            {
                var output = new byte[128];
                int length = socket.IOControl(SioTcpInfo, BitConverter.GetBytes(0), output);
                return length >= WindowsRttOffset + 4 ? Positive(TimeSpan.FromMicroseconds(BitConverter.ToUInt32(output, WindowsRttOffset))) : null;
            }

            Span<byte> info = stackalloc byte[256];
            if (OperatingSystem.IsLinux())
            {
                int length = socket.GetRawSocketOption(IpProtoTcp, LinuxTcpInfo, info);
                return length >= LinuxRttOffset + 4 ? Positive(TimeSpan.FromMicroseconds(MemoryMarshal.Read<uint>(info[LinuxRttOffset..]))) : null;
            }

            if (OperatingSystem.IsMacOS())
            {
                int length = socket.GetRawSocketOption(IpProtoTcp, MacTcpConnectionInfo, info);
                return length >= MacSrttOffset + 4 ? Positive(TimeSpan.FromMilliseconds(MemoryMarshal.Read<uint>(info[MacSrttOffset..]))) : null;
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or PlatformNotSupportedException or NotSupportedException)
        {
        }

        return null;
    }

    // A connection that hasn't measured one yet says 0.
    static TimeSpan? Positive(TimeSpan rtt) => rtt > TimeSpan.Zero ? rtt : null;
}

/// <summary>A stream that counts what goes through it into <see cref="LinkStats"/>.</summary>
public sealed class CountingStream(Stream inner, LinkStats stats) : Stream
{
    public Stream Inner => inner;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override bool CanTimeout => inner.CanTimeout;
    public override int ReadTimeout { get => inner.ReadTimeout; set => inner.ReadTimeout = value; }
    public override int WriteTimeout { get => inner.WriteTimeout; set => inner.WriteTimeout = value; }

    public override int Read(byte[] buffer, int offset, int count) => Counted(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Counted(inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Counted(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Counted(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        stats.Sent(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        stats.Sent(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        stats.Sent(buffer.Length);
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    int Counted(int read)
    {
        stats.Received(read);
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
