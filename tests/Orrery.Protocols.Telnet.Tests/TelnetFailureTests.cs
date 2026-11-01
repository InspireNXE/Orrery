using System.Net;
using System.Net.Sockets;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Telnet.Tests;

/// <summary>Telnet's failures, as the connection-lost screen tells them.</summary>
public class TelnetFailureTests
{
    static readonly ConnectionTarget Switch = new("switch.internal", 23, null, null);

    sealed class FailingRoute(Exception failure) : ISessionRoute
    {
        public Task<(string Host, int Port)> OpenAsync(string host, int port, CancellationToken token) => Task.FromException<(string, int)>(failure);
        public void Dispose() { }
    }

    [Fact]
    public async Task Nothing_listening_is_a_refusal()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var error = await FailureOf(() => TelnetSession.Start(new ConnectionTarget("127.0.0.1", port, null, null), 80, 24));

        Assert.Equal(FailureKind.Refused, ConnectionFailure.Classify(error, null, Switch).Kind);
    }

    [Fact]
    public async Task A_jump_host_refusing_the_login_is_named_and_never_retried()
    {
        var hop = new ConnectionTarget("bastion", 22, "me", null);
        var failure = new RouteException("Jump host bastion: The username or password was rejected.",
            new SessionFailureException(FailureKind.LoginFailed, "The username or password was rejected."), hop);

        var error = await FailureOf(() => TelnetSession.Start(Switch, 80, 24, new FailingRoute(failure)));
        var classified = ConnectionFailure.Classify(error, null, Switch);

        Assert.Equal(FailureKind.JumpHost, classified.Kind);
        Assert.Equal("bastion", classified.Hop);
        Assert.False(classified.CanRetryByItself(dropped: true));
    }

    /// <summary>The error a session fails with. Started again if it failed before the handler was on, which loses the error.</summary>
    static async Task<Exception?> FailureOf(Func<TelnetSession> start)
    {
        for (int attempt = 0; ; attempt++)
        {
            var session = start();
            var failed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.StateChanged += (_, e) =>
            {
                if (e.State == SessionState.Failed) failed.TrySetResult(e.Error);
            };
            try
            {
                if (session.State != SessionState.Failed || attempt == 5)
                {
                    return await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
    }
}
