using System.Net;
using System.Net.Sockets;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

public class SshJumpTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"orrery-known-hosts-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task A_jump_host_that_cant_be_reached_fails_the_session_naming_it_and_doesnt_ask_for_a_login()
    {
        int closedPort = ClosedPort();
        var jump = new SshHop(new ConnectionTarget("127.0.0.1", closedPort, "me", null), new SshLogin("me", "pw"));
        var validator = new HostKeyValidator(new JsonCertificateTrustStore(_path), _ => Task.FromResult(CertificateTrustDecision.Reject));
        var failed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = SshSession.Start(new ConnectionTarget("db.internal", 22, "me", null), new SshLogin("me", "pw"), validator, 80, 24, jumps: [jump]);
        session.StateChanged += (_, e) =>
        {
            if (e.State == SessionState.Failed) failed.TrySetResult(e.Message);
        };
        if (session.State == SessionState.Failed) failed.TrySetResult("(failed before subscribing)");

        var message = await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.StartsWith("Jump host 127.0.0.1", message);
        Assert.False(session.AuthenticationRejected);
        await session.DisposeAsync();
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
