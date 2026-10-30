using System.Net;
using System.Net.Sockets;
using System.Text;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Telnet.Tests;

public class TelnetSessionTests
{
    const byte Se = 240, Sb = 250, Will = 251, Wont = 252, Do = 253, Dont = 254, Iac = 255;
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>A one-client Telnet server that records every byte the client sends.</summary>
    sealed class TestServer : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly List<byte> _received = [];
        TcpClient? _client;

        public TestServer() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task AcceptAsync()
        {
            _client = await _listener.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                try
                {
                    int n;
                    while ((n = await _client.GetStream().ReadAsync(buffer)) > 0)
                    {
                        lock (_received) _received.AddRange(buffer[..n]);
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                }
            });
        }

        public Task SendAsync(params byte[] bytes) => _client!.GetStream().WriteAsync(bytes).AsTask();

        public byte[] Received { get { lock (_received) return [.. _received]; } }

        public async Task<bool> ReceivedAsync(byte[] sequence)
        {
            var until = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < until)
            {
                if (Contains(Received, sequence)) return true;
                await Task.Delay(20);
            }

            return false;
        }

        public void Close() => _client?.Dispose();

        public ValueTask DisposeAsync()
        {
            _client?.Dispose();
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }

    static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        }

        return false;
    }

    static async Task<(TestServer Server, TelnetSession Session, List<byte> Output)> ConnectAsync()
    {
        var server = new TestServer();
        var accepted = server.AcceptAsync();
        var session = TelnetSession.Start(new ConnectionTarget("127.0.0.1", server.Port, null, null), 120, 40);
        var output = new List<byte>();
        session.DataReceived += data => { lock (output) output.AddRange(data); };
        await accepted.WaitAsync(Wait);
        var until = DateTime.UtcNow + Wait;
        while (session.State != SessionState.Connected && DateTime.UtcNow < until) await Task.Delay(20);
        return (server, session, output);
    }

    [Fact]
    public async Task Negotiates_like_a_terminal_and_refuses_the_rest()
    {
        var (server, session, _) = await ConnectAsync();
        await using var _ = server;
        await using var __ = session;

        await server.SendAsync(Iac, Will, 1, Iac, Will, 3, Iac, Do, 24, Iac, Do, 31, Iac, Do, 39 /* NEW-ENVIRON */, Iac, Will, 5 /* STATUS */);

        Assert.True(await server.ReceivedAsync([Iac, Do, 1]), "agrees the server echoes");
        Assert.True(await server.ReceivedAsync([Iac, Do, 3]), "agrees to suppress go-ahead");
        Assert.True(await server.ReceivedAsync([Iac, Will, 24]), "offers its terminal type");
        Assert.True(await server.ReceivedAsync([Iac, Will, 31, Iac, Sb, 31, 0, 120, 0, 40, Iac, Se]), "reports 120x40 at once");
        Assert.True(await server.ReceivedAsync([Iac, Wont, 39]), "refuses NEW-ENVIRON");
        Assert.True(await server.ReceivedAsync([Iac, Dont, 5]), "refuses STATUS");

        // Asking again changes nothing, so there's no reply: no negotiation loops.
        int before = server.Received.Length;
        await server.SendAsync(Iac, Will, 1, Iac, Do, 24);
        await Task.Delay(300);
        Assert.Equal(before, server.Received.Length);
    }

    [Fact]
    public async Task Answers_the_terminal_type_and_reports_resizes()
    {
        var (server, session, _) = await ConnectAsync();
        await using var _ = server;
        await using var __ = session;

        await server.SendAsync(Iac, Do, 24, Iac, Do, 31, Iac, Sb, 24, 1, Iac, Se);
        Assert.True(await server.ReceivedAsync([Iac, Sb, 24, 0, .. "XTERM-256COLOR"u8.ToArray(), Iac, Se]));

        session.Resize(300, 255); // 255 must be doubled inside the subnegotiation.
        Assert.True(await server.ReceivedAsync([Iac, Sb, 31, 1, 44, 0, Iac, Iac, Iac, Se]));
    }

    [Fact]
    public async Task Text_passes_through_with_255_escaped_both_ways()
    {
        var (server, session, output) = await ConnectAsync();
        await using var _ = server;
        await using var __ = session;

        await server.SendAsync([.. "login: "u8.ToArray(), Iac, Iac, (byte)'\r', 0, (byte)'x']);
        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until) { lock (output) if (output.Count >= 10) break; await Task.Delay(20); }
        lock (output) Assert.Equal([.. "login: "u8.ToArray(), 255, (byte)'\r', (byte)'x'], output);

        session.Send([(byte)'a', 255, (byte)'\r']);
        Assert.True(await server.ReceivedAsync([(byte)'a', Iac, Iac, (byte)'\r', (byte)'\n']), "0xFF doubled, Enter as CR LF");
    }

    [Fact]
    public async Task The_server_hanging_up_ends_the_session()
    {
        var (server, session, _) = await ConnectAsync();
        await using var _ = server;
        await using var __ = session;
        var ended = new TaskCompletionSource<SessionState>();
        session.StateChanged += (_, e) => ended.TrySetResult(e.State);

        server.Close();

        Assert.Equal(SessionState.Disconnected, await ended.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task Nothing_listening_fails_with_a_message()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var failed = new TaskCompletionSource<string?>();
        await using var session = TelnetSession.Start(new ConnectionTarget("127.0.0.1", port, null, null), 80, 24);
        session.StateChanged += (_, e) => { if (e.State == SessionState.Failed) failed.TrySetResult(e.Message); };
        if (session.State == SessionState.Failed) failed.TrySetResult("failed");

        Assert.False(string.IsNullOrWhiteSpace(await failed.Task.WaitAsync(Wait)));
    }

    [Fact]
    public async Task A_subnegotiation_that_never_ends_is_capped_and_dropped()
    {
        var (server, session, output) = await ConnectAsync();
        await using var _ = server;
        await using var __ = session;

        // IAC SB TTYPE SEND, then 1 MiB of filler before the IAC SE: far past any real subnegotiation.
        var filler = new byte[1024 * 1024];
        Array.Fill(filler, (byte)'z');
        await server.SendAsync([Iac, Sb, 24, 1, .. filler, Iac, Se, .. "after"u8.ToArray()]);

        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until) { lock (output) if (output.Count >= 5) break; await Task.Delay(20); }
        lock (output) Assert.Equal("after"u8.ToArray(), output);
        Assert.Equal(SessionState.Connected, session.State);
        Assert.False(Contains(server.Received, [Iac, Sb, 24, 0]), "an oversized subnegotiation is dropped, not answered");

        // The parser is back in the data state, so an ordinary subnegotiation still works.
        await server.SendAsync(Iac, Sb, 24, 1, Iac, Se);
        Assert.True(await server.ReceivedAsync([Iac, Sb, 24, 0, .. "XTERM-256COLOR"u8.ToArray(), Iac, Se]));
    }

    [Fact]
    public async Task A_handler_that_throws_fails_the_session_and_disconnecting_stays_quiet()
    {
        var (server, session, _) = await ConnectAsync();
        await using var _ = server;
        var failed = new TaskCompletionSource<string?>();
        session.StateChanged += (_, e) => { if (e.State == SessionState.Failed) failed.TrySetResult(e.Message); };
        session.DataReceived += _ => throw new InvalidOperationException("the terminal broke");

        await server.SendAsync("hello"u8.ToArray());

        Assert.False(string.IsNullOrWhiteSpace(await failed.Task.WaitAsync(Wait)));
        Assert.Equal(SessionState.Failed, session.State);
        await session.DisconnectAsync();
        await session.DisposeAsync();
    }
}
