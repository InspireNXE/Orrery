using System.Net;
using System.Net.Sockets;
using MarcusW.VncClient.Protocol;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Vnc.Tests;

/// <summary>The VNC library's own exceptions, as the connection-lost screen tells them.</summary>
public class VncFailureTests
{
    static readonly ConnectionTarget Box = new("box", 5900, null, null);

    static FailureKind KindOf(Exception e) => ConnectionFailure.Classify(VncSession.Recognize(e), e.Message, Box).Kind;

    [Fact]
    public void The_stream_ending_is_the_server_closing_it()
    {
        Assert.Equal(FailureKind.ServerClosed, KindOf(new UnexpectedEndOfStreamException("Unexpected end of stream.")));
    }

    [Fact]
    public void Other_handshake_failures_keep_their_message()
    {
        Assert.Equal(FailureKind.Other, KindOf(new HandshakeFailedException("The server asked for a login method Orrery doesn't support.")));
        Assert.Equal(FailureKind.Other, KindOf(new UnsupportedProtocolFeatureException("No supported encoding.")));
    }

    [Fact]
    public async Task A_wrong_password_is_a_refused_login()
    {
        await using var server = new TestRfbServer(password: "dummypw1");
        var error = await FailureOf(() => VncSession.Start(new ConnectionTarget("127.0.0.1", server.Port, null, null), "wrongpw!"));

        Assert.Equal(FailureKind.LoginFailed, ConnectionFailure.Classify(error, null, Box).Kind);
    }

    [Fact]
    public async Task Nothing_listening_is_a_refusal()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var error = await FailureOf(() => VncSession.Start(new ConnectionTarget("127.0.0.1", port, null, null), "pw"));

        Assert.Equal(FailureKind.Refused, ConnectionFailure.Classify(error, null, Box).Kind);
    }

    /// <summary>The error a session fails with. Started again if it failed before the handler was on, which loses the error.</summary>
    static async Task<Exception?> FailureOf(Func<VncSession> start)
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
