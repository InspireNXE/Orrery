using System.Net;
using System.Net.Sockets;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Telnet.Tests;

/// <summary>A Telnet session counts its traffic for the Connection card, and measures the round trip only when it dials the server itself.</summary>
public class TelnetLinkTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Counts_both_ways_and_measures_the_round_trip_on_its_own_socket()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accepted = listener.AcceptTcpClientAsync();
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = TelnetSession.Start(new ConnectionTarget("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, null, null), 80, 24);
            session.DataReceived += _ => received.TrySetResult();
            using var server = await accepted.WaitAsync(Wait);
            var stream = server.GetStream();
            await stream.WriteAsync("hello\r\n"u8.ToArray());
            await received.Task.WaitAsync(Wait);

            session.Send("abc"u8);
            var buffer = new byte[3];
            await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(Wait);

            var reading = session.Link.Read();
            Assert.Equal(7, reading.Received);
            Assert.Equal(3, reading.Sent);
            Assert.NotNull(session.Link.Socket);
            Assert.NotNull(session.Link.ConnectTime);
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                Assert.NotNull(reading.RoundTrip);
            }

            await session.DisposeAsync();
            Assert.Null(session.Link.Read().RoundTrip);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Through_a_route_it_leaves_the_round_trip_out()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var accepted = listener.AcceptTcpClientAsync();
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = TelnetSession.Start(new ConnectionTarget("switch.internal", 23, null, null), 80, 24, new LoopbackRoute(((IPEndPoint)listener.LocalEndpoint).Port));
            session.StateChanged += (_, e) =>
            {
                if (e.State == SessionState.Connected) connected.TrySetResult();
            };
            if (session.State == SessionState.Connected) connected.TrySetResult();

            using var server = await accepted.WaitAsync(Wait);
            await connected.Task.WaitAsync(Wait);
            Assert.Null(session.Link.Socket);
            Assert.Null(session.Link.Read().RoundTrip);
            await session.DisposeAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    sealed class LoopbackRoute(int port) : ISessionRoute
    {
        public Task<(string Host, int Port)> OpenAsync(string host, int port1, CancellationToken token) => Task.FromResult(("127.0.0.1", port));

        public void Dispose()
        {
        }
    }
}
