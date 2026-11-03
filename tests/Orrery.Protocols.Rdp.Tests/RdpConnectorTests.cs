using System.Net;
using System.Net.Sockets;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Rdp.Tests;

public class RdpConnectorTests
{
    [Fact]
    public async Task A_Kerberos_reply_split_over_several_reads_is_read_whole()
    {
        var body = Enumerable.Range(0, 20_000).Select(i => (byte)i).ToArray();
        var framed = new byte[4 + body.Length];
        framed[2] = (byte)(body.Length >> 8);
        framed[3] = (byte)body.Length;
        body.CopyTo(framed, 4);

        // A second reply behind the first, which must be left unread.
        using var stream = new TrickleStream([.. framed, 0, 0, 0, 1, 9], chunk: 1000);
        var reply = await RdpConnector.ReadKdcReplyAsync(stream, CancellationToken.None);

        Assert.Equal(framed, reply);
        Assert.Equal(framed.Length, stream.Position);
    }

    [Fact]
    public async Task A_Kerberos_reply_longer_than_the_cap_is_refused()
    {
        using var stream = new TrickleStream([0x7F, 0xFF, 0xFF, 0xFF, 1, 2, 3], chunk: 1000);

        await Assert.ThrowsAsync<InvalidDataException>(() => RdpConnector.ReadKdcReplyAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_Kerberos_reply_cut_short_fails()
    {
        using var stream = new TrickleStream([0, 0, 0, 10, 1, 2, 3], chunk: 1000);

        await Assert.ThrowsAsync<EndOfStreamException>(() => RdpConnector.ReadKdcReplyAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task A_name_with_no_addresses_says_it_was_not_found()
    {
        var e = await Assert.ThrowsAsync<SessionFailureException>(() => RdpConnector.ConnectAnyAsync("nowhere.example", [], 3389, CancellationToken.None));

        Assert.Equal(FailureKind.NameNotFound, e.Kind);
        Assert.Contains("nowhere.example", e.Message);
    }

    [Fact]
    public async Task An_address_that_does_not_answer_is_passed_over_for_the_next()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Nothing listens on IPv6 loopback at that port, so it's refused, or can't be reached without IPv6.
        using var client = await RdpConnector.ConnectAnyAsync("server", [IPAddress.IPv6Loopback, IPAddress.Loopback], port, CancellationToken.None);

        Assert.True(client.Connected);
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)client.Client.RemoteEndPoint!).Address);
    }

    [Fact]
    public async Task When_no_address_answers_the_last_error_is_thrown()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await Assert.ThrowsAsync<SocketException>(() => RdpConnector.ConnectAnyAsync("server", [IPAddress.Loopback], port, CancellationToken.None));
    }

    /// <summary>A stream that hands out at most <paramref name="chunk"/> bytes per read, as a network stream may.</summary>
    sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
