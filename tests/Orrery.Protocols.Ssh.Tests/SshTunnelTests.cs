using System.Net;
using System.Net.Sockets;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

public class SshTunnelTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"orrery-known-hosts-{Guid.NewGuid():N}.json");

    HostKeyValidator Validator() => new(new JsonCertificateTrustStore(_path), _ => Task.FromResult(CertificateTrustDecision.Reject));

    [Fact]
    public void Needs_at_least_one_jump_host() =>
        Assert.Throws<ArgumentException>(() => new SshTunnel([], Validator()));

    [Fact]
    public async Task A_jump_host_that_cant_be_reached_fails_with_a_message_naming_it()
    {
        var hop = new SshHop(new ConnectionTarget("127.0.0.1", ClosedPort(), "me", null), new SshLogin("me", "pw"));
        using var tunnel = new SshTunnel([hop], Validator());

        var e = await Assert.ThrowsAsync<RouteException>(() => tunnel.OpenAsync("desktop.internal", 3389, CancellationToken.None));
        Assert.StartsWith("Jump host 127.0.0.1: ", e.Message);
    }

    [Fact]
    public async Task Cancelling_while_connecting_ends_with_cancellation_not_an_error()
    {
        // Accepts and then says nothing, so the SSH handshake hangs.
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var hop = new SshHop(new ConnectionTarget("127.0.0.1", ((IPEndPoint)silent.LocalEndpoint).Port, "me", null), new SshLogin("me", "pw"));
            using var tunnel = new SshTunnel([hop], Validator());
            using var cancel = new CancellationTokenSource();

            var opening = tunnel.OpenAsync("desktop.internal", 3389, cancel.Token);
            using var accepted = await silent.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();

            // At once, not after the 20-second connect timeout.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            silent.Stop();
        }
    }

    static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose() => File.Delete(_path);
}
