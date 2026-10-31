using System.Net;
using System.Net.Sockets;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Telnet.Tests;

/// <summary>A session with a route (such as an SSH tunnel) dials where the route says and disposes it when it ends.</summary>
public class TelnetRouteTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Sends every connection to <see cref="Port"/> on loopback, or fails with <see cref="Failure"/>.</summary>
    sealed class FakeRoute(int port) : ISessionRoute
    {
        public int Port { get; } = port;
        public string? Failure { get; init; }
        public (string Host, int Port)? AskedFor { get; private set; }
        public bool Disposed { get; private set; }

        public Task<(string Host, int Port)> OpenAsync(string host, int port, CancellationToken token)
        {
            AskedFor = (host, port);
            return Failure is null ? Task.FromResult(("127.0.0.1", Port)) : Task.FromException<(string, int)>(new RouteException(Failure));
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Dials_the_routes_address_for_the_real_target_and_disposes_the_route_on_close()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var route = new FakeRoute(((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = listener.AcceptTcpClientAsync();
            var session = TelnetSession.Start(new ConnectionTarget("switch.internal", 23, null, null), 80, 24, route);

            using var client = await accepted.WaitAsync(Wait);
            Assert.Equal(("switch.internal", 23), route.AskedFor);
            Assert.False(route.Disposed);

            await session.DisposeAsync();
            Assert.True(route.Disposed);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_route_that_cant_open_fails_the_session_with_its_message()
    {
        var route = new FakeRoute(1) { Failure = "Jump host bastion: The username or password was rejected." };
        var failed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = TelnetSession.Start(new ConnectionTarget("switch.internal", 23, null, null), 80, 24, route);
        session.StateChanged += (_, e) =>
        {
            if (e.State == SessionState.Failed) failed.TrySetResult(e.Message);
        };
        if (session.State == SessionState.Failed) failed.TrySetResult(route.Failure);

        Assert.Equal(route.Failure, await failed.Task.WaitAsync(Wait));
        await session.DisposeAsync();
        Assert.True(route.Disposed);
    }
}
