using System.Net;
using System.Net.Sockets;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Sessions;

public class LinkStatsTests
{
    [Fact]
    public async Task A_counting_stream_counts_what_it_reads_and_writes_every_way()
    {
        var stats = new LinkStats();
        var inner = new MemoryStream();
        await using var stream = new CountingStream(inner, stats);

        stream.Write([1, 2, 3], 0, 3);
        stream.Write(new byte[] { 4, 5 }.AsSpan());
        await stream.WriteAsync(new byte[] { 6 });
        await stream.WriteAsync(new byte[4], 0, 4);
        inner.Position = 0;

        var buffer = new byte[4];
        Assert.Equal(4, stream.Read(buffer, 0, 4));
        Assert.Equal(4, await stream.ReadAsync(buffer));
        Assert.Equal(2, stream.Read(buffer.AsSpan()));
        Assert.Equal(0, await stream.ReadAsync(buffer, 0, 4));

        var reading = stats.Read();
        Assert.Equal(10, reading.Sent);
        Assert.Equal(10, reading.Received);
        Assert.Null(reading.RoundTrip);
    }

    [Fact]
    public void Busy_time_and_the_timings_add_up_and_can_be_cleared()
    {
        var stats = new LinkStats();
        stats.Busy(TimeSpan.FromMilliseconds(30));
        stats.Busy(TimeSpan.FromMilliseconds(20));
        Assert.Equal(TimeSpan.FromMilliseconds(50), stats.Read().Busy);

        Assert.Null(stats.ConnectTime);
        stats.ConnectTime = TimeSpan.FromMilliseconds(320);
        stats.LastResize = TimeSpan.FromMilliseconds(45);
        Assert.Equal(TimeSpan.FromMilliseconds(320), stats.ConnectTime);
        Assert.Equal(TimeSpan.FromMilliseconds(45), stats.LastResize);
        stats.LastResize = null;
        Assert.Null(stats.LastResize);

        // The same time again is still a new resize; clearing it isn't one.
        Assert.Equal(1, stats.Resizes);
        stats.LastResize = TimeSpan.FromMilliseconds(45);
        Assert.Equal(2, stats.Resizes);

        Assert.Empty(stats.ConnectSteps);
        stats.ConnectSteps = [new ConnectStep("Network", TimeSpan.FromMilliseconds(4))];
        Assert.Equal("Network", Assert.Single(stats.ConnectSteps).Name);
    }

    [Fact]
    public async Task The_system_gives_a_connected_sockets_round_trip_and_none_once_its_closed()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();

        // A round trip is only measured once something has gone across and been acknowledged.
        await client.GetStream().WriteAsync(new byte[] { 1 });
        await server.GetStream().ReadExactlyAsync(new byte[1]);
        await server.GetStream().WriteAsync(new byte[] { 2 });
        await client.GetStream().ReadExactlyAsync(new byte[1]);

        var socket = client.Client;
        var rtt = TcpRoundTrip.Of(socket);
        Assert.NotNull(rtt);
        Assert.InRange(rtt!.Value, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(1));

        client.Close();
        Assert.Null(TcpRoundTrip.Of(socket));
    }
}
