using System.Net;
using System.Net.Sockets;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

/// <summary>SSH.NET's own exceptions, as the connection-lost screen tells them.</summary>
public class SshFailureTests
{
    static readonly ConnectionTarget Box = new("box", 22, "me", null);

    static FailureKind KindOf(Exception e) => ConnectionFailure.Classify(SshSession.Recognize(e), e.Message, Box).Kind;

    [Fact]
    public void A_refused_login_is_a_login_failure()
    {
        Assert.Equal(FailureKind.LoginFailed, KindOf(new SshAuthenticationException("Permission denied (password).")));
        Assert.Equal(FailureKind.LoginFailed, KindOf(new NoLoginMethodException()));
    }

    [Fact]
    public void Its_connect_timeout_is_a_timeout()
    {
        Assert.Equal(FailureKind.TimedOut, KindOf(new SshOperationTimeoutException("Connection failed to establish within 20000 milliseconds.")));
    }

    [Fact]
    public void The_server_dropping_or_ending_the_connection_is_the_server_closing_it()
    {
        Assert.Equal(FailureKind.ServerClosed, KindOf(new SshConnectionException("An established connection was aborted by the server.", DisconnectReason.ConnectionLost)));
        Assert.Equal(FailureKind.ServerClosed, KindOf(new SshConnectionException("The connection was closed by the server: idle (ByApplication).", DisconnectReason.ByApplication)));
    }

    [Fact]
    public void Socket_errors_pass_through_and_anything_else_keeps_its_message()
    {
        Assert.Equal(FailureKind.NameNotFound, KindOf(new SocketException((int)SocketError.HostNotFound)));
        Assert.Equal(FailureKind.Other, KindOf(new SshConnectionException("Key exchange negotiation failed.", DisconnectReason.KeyExchangeFailed)));
    }

    [Fact]
    public async Task Nothing_listening_fails_with_the_refusal_attached()
    {
        var error = await FailureOf(() => SshSession.Start(new ConnectionTarget("127.0.0.1", ClosedPort(), "me", null), new SshLogin("me", "pw"), NoPrompt(), 80, 24));

        Assert.Equal(FailureKind.Refused, ConnectionFailure.Classify(error, null, Box).Kind);
    }

    [Fact]
    public async Task A_jump_host_that_refuses_is_named_as_the_hop()
    {
        int closed = ClosedPort();
        var hop = new SshHop(new ConnectionTarget("127.0.0.1", closed, "me", null), new SshLogin("me", "pw"));
        var error = await FailureOf(() => SshSession.Start(Box, new SshLogin("me", "pw"), NoPrompt(), 80, 24, jumps: [hop]));

        var failure = ConnectionFailure.Classify(error, null, Box);
        Assert.Equal(FailureKind.JumpHost, failure.Kind);
        Assert.Equal(FailureKind.Refused, failure.Cause);
        Assert.Equal("127.0.0.1", failure.Hop);
    }

    static HostKeyValidator NoPrompt() =>
        new(new MemoryTrustStore(), _ => Task.FromResult(CertificateTrustDecision.Reject));

    /// <summary>The error a session fails with. Started again if it failed before the handler was on, which loses the error.</summary>
    static async Task<Exception?> FailureOf(Func<SshSession> start)
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
                    return await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
            finally
            {
                await session.DisposeAsync();
            }
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

    sealed class MemoryTrustStore : ICertificateTrustStore
    {
        readonly Dictionary<(string, int), PinnedCertificate> _pins = [];
        public PinnedCertificate? Get(string host, int port) => _pins.GetValueOrDefault((host, port));
        public void Pin(string host, int port, PinnedCertificate certificate) => _pins[(host, port)] = certificate;
    }
}
