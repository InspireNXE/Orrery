using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Orrery.Core.Sessions;

namespace Orrery.Protocols.Telnet;

/// <summary>
/// A Telnet terminal session (RFC 854). Negotiates echo, suppress-go-ahead and binary, reports the terminal
/// type (RFC 1091) and window size (RFC 1073), and refuses every other option. Telnet has no login of its
/// own: the server prompts in the terminal. Nothing is encrypted, so passwords cross the network as typed.
/// </summary>
public sealed class TelnetSession : ITerminalSession
{
    const byte Se = 240, Sb = 250, Will = 251, Wont = 252, Do = 253, Dont = 254, Iac = 255;
    const byte OptBinary = 0, OptEcho = 1, OptSuppressGoAhead = 3, OptTerminalType = 24, OptWindowSize = 31;
    const byte TerminalTypeIs = 0, TerminalTypeSend = 1;
    const string TerminalType = "XTERM-256COLOR";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    // We only answer TTYPE and NAWS, which are a few bytes; a longer subnegotiation is dropped.
    const int MaxSubnegotiation = 4 * 1024;

    // Options we'll do on our side when asked, and ones we'll let the server do.
    static readonly HashSet<byte> OursAllowed = [OptBinary, OptSuppressGoAhead, OptTerminalType, OptWindowSize];
    static readonly HashSet<byte> TheirsAllowed = [OptBinary, OptEcho, OptSuppressGoAhead];

    readonly object _lock = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    readonly HashSet<byte> _ours = [], _theirs = [];
    TcpClient? _client;
    NetworkStream? _stream;
    int _columns, _rows;
    Task? _run;

    TelnetSession(ConnectionTarget target, int columns, int rows)
    {
        Target = target;
        (_columns, _rows) = (columns, rows);
    }

    public ConnectionTarget Target { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event Action<byte[]>? DataReceived;

    /// <summary>Starts connecting in the background and returns at once; watch <see cref="StateChanged"/>.</summary>
    public static TelnetSession Start(ConnectionTarget target, int columns, int rows)
    {
        var session = new TelnetSession(target, columns, rows);
        session._run = Task.Run(session.RunAsync);
        return session;
    }

    async Task RunAsync()
    {
        var token = _cts.Token;
        try
        {
            var client = new TcpClient { NoDelay = true };
            lock (_lock) _client = client;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(Target.Host, Target.Port, timeout.Token);
            }

            var stream = client.GetStream();
            lock (_lock) _stream = stream;
            SetState(SessionState.Connected);

            var buffer = new byte[16 * 1024];
            var parser = new Parser(this);
            while (!token.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, token);
                if (read == 0)
                {
                    break;
                }

                var (data, replies) = parser.Feed(buffer.AsSpan(0, read));
                if (replies.Length > 0)
                {
                    await WriteRawAsync(replies);
                }

                if (data.Length > 0)
                {
                    DataReceived?.Invoke(data);
                }
            }

            SetState(SessionState.Disconnected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetState(SessionState.Disconnected);
        }
        catch (OperationCanceledException)
        {
            SetState(SessionState.Failed, "The connection timed out.");
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            Trace.TraceWarning($"Telnet session to {Target.Host} ended: {e.Message}");
            SetState(State == SessionState.Connected ? SessionState.Disconnected : SessionState.Failed, e.Message);
        }
        catch (Exception e)
        {
            // From a DataReceived handler or the parser. Report it here so it doesn't sit unobserved in _run.
            Trace.TraceError($"Telnet session to {Target.Host} failed: {e}");
            SetState(SessionState.Failed, "The Telnet session stopped because of an internal error.");
        }
    }

    /// <summary>
    /// Keystrokes to the server. 0xFF is doubled, and outside binary mode a carriage return goes as CR LF,
    /// Telnet's newline, since a lone CR isn't allowed there.
    /// </summary>
    public void Send(ReadOnlySpan<byte> data)
    {
        if (State != SessionState.Connected)
        {
            return;
        }

        bool binary;
        lock (_lock) binary = _ours.Contains(OptBinary);
        var bytes = new List<byte>(data.Length + 8);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            bytes.Add(b);
            if (b == Iac)
            {
                bytes.Add(Iac);
            }
            else if (b == '\r' && !binary && (i + 1 >= data.Length || data[i + 1] != '\n'))
            {
                bytes.Add((byte)'\n');
            }
        }

        _ = WriteRawAsync([.. bytes]);
    }

    public void Resize(int columns, int rows)
    {
        bool send;
        lock (_lock)
        {
            (_columns, _rows) = (columns, rows);
            send = _ours.Contains(OptWindowSize);
        }

        if (send && State == SessionState.Connected)
        {
            _ = WriteRawAsync(WindowSize());
        }
    }

    /// <summary>The NAWS subnegotiation for the current size, 255s doubled.</summary>
    byte[] WindowSize()
    {
        int columns, rows;
        lock (_lock) (columns, rows) = (_columns, _rows);
        var message = new List<byte> { Iac, Sb, OptWindowSize };
        foreach (var value in new[] { columns, rows })
        {
            foreach (var b in new[] { (byte)(value >> 8), (byte)value })
            {
                message.Add(b);
                if (b == Iac) message.Add(Iac);
            }
        }

        message.AddRange([Iac, Se]);
        return [.. message];
    }

    async Task WriteRawAsync(byte[] bytes)
    {
        NetworkStream? stream;
        lock (_lock) stream = _stream;
        if (stream is null)
        {
            return;
        }

        try
        {
            await _writeLock.WaitAsync(_cts.Token);
            try
            {
                await stream.WriteAsync(bytes, _cts.Token);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // The read loop reports the end of the connection.
        }
    }

    public async Task DisconnectAsync()
    {
        _cts.Cancel();
        TcpClient? client;
        lock (_lock) (client, _client, _stream) = (_client, null, null);
        client?.Dispose();
        if (_run is not null)
        {
            try
            {
                await _run;
            }
            catch (Exception e)
            {
                // The read loop has already reported how it ended.
                Trace.TraceWarning($"Telnet read loop for {Target.Host} ended with {e.GetType().Name}: {e.Message}");
            }
        }

        SetState(SessionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts.Dispose();
    }

    void SetState(SessionState state, string? message = null)
    {
        lock (_lock)
        {
            if (State == state || State is SessionState.Disconnected or SessionState.Failed) return;
            State = state;
        }

        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
    }

    /// <summary>
    /// Splits the server's bytes into terminal data and Telnet commands, and works out the replies. It answers
    /// only when an option actually changes state, so two sides can't loop agreeing with each other.
    /// </summary>
    sealed class Parser(TelnetSession session)
    {
        enum Mode { Data, Command, Option, Sub, SubCommand }

        Mode _mode;
        byte _verb;
        bool _lastWasCr, _subTooLong;
        readonly List<byte> _sub = [];

        public (byte[] Data, byte[] Replies) Feed(ReadOnlySpan<byte> input)
        {
            var data = new List<byte>(input.Length);
            var replies = new List<byte>();
            foreach (byte b in input)
            {
                switch (_mode)
                {
                    case Mode.Data when b == Iac:
                        _mode = Mode.Command;
                        break;
                    case Mode.Data:
                        // Outside binary mode the server sends CR NUL for a bare carriage return.
                        if (!(b == 0 && _lastWasCr && !session.TheirBinary))
                        {
                            data.Add(b);
                        }
                        _lastWasCr = b == '\r';
                        break;

                    case Mode.Command:
                        switch (b)
                        {
                            case Iac: data.Add(Iac); _mode = Mode.Data; break;
                            case Will or Wont or Do or Dont: _verb = b; _mode = Mode.Option; break;
                            case Sb: _sub.Clear(); _subTooLong = false; _mode = Mode.Sub; break;
                            default: _mode = Mode.Data; break; // NOP, GA, AYT and friends need nothing.
                        }
                        break;

                    case Mode.Option:
                        replies.AddRange(session.Negotiate(_verb, b));
                        _mode = Mode.Data;
                        break;

                    case Mode.Sub when b == Iac:
                        _mode = Mode.SubCommand;
                        break;
                    case Mode.Sub:
                        AddSub(b);
                        break;

                    case Mode.SubCommand when b == Se:
                        // One that ran past the cap is dropped unanswered, and parsing carries on from here.
                        if (!_subTooLong)
                        {
                            replies.AddRange(session.SubNegotiation(_sub));
                        }

                        _sub.Clear();
                        _mode = Mode.Data;
                        break;
                    case Mode.SubCommand:
                        AddSub(b); // IAC IAC inside a subnegotiation is a literal 255.
                        _mode = Mode.Sub;
                        break;
                }
            }

            return ([.. data], [.. replies]);
        }

        void AddSub(byte b)
        {
            if (_sub.Count < MaxSubnegotiation)
            {
                _sub.Add(b);
            }
            else
            {
                _subTooLong = true;
            }
        }
    }

    bool TheirBinary { get { lock (_lock) return _theirs.Contains(OptBinary); } }

    byte[] Negotiate(byte verb, byte option)
    {
        lock (_lock)
        {
            switch (verb)
            {
                case Do when OursAllowed.Contains(option):
                    if (!_ours.Add(option)) return [];
                    // Say yes, and send the size straight away rather than waiting for a resize.
                    return option == OptWindowSize ? [Iac, Will, option, .. WindowSize()] : [Iac, Will, option];
                case Do:
                    return [Iac, Wont, option];
                case Dont:
                    return _ours.Remove(option) ? [Iac, Wont, option] : [];
                case Will when TheirsAllowed.Contains(option):
                    return _theirs.Add(option) ? [Iac, Do, option] : [];
                case Will:
                    return [Iac, Dont, option];
                case Wont:
                    return _theirs.Remove(option) ? [Iac, Dont, option] : [];
                default:
                    return [];
            }
        }
    }

    byte[] SubNegotiation(List<byte> sub)
    {
        if (sub is [OptTerminalType, TerminalTypeSend, ..])
        {
            return [Iac, Sb, OptTerminalType, TerminalTypeIs, .. Encoding.ASCII.GetBytes(TerminalType), Iac, Se];
        }

        return [];
    }
}
