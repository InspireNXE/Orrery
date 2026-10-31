using System.Diagnostics;
using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Renci.SshNet;
using Orrery.Protocols.Ssh.Agent;
using Renci.SshNet.Common;

namespace Orrery.Protocols.Ssh;

/// <summary>
/// How to log in: a password (also answers keyboard-interactive prompts) or a private key, after the SSH
/// agent's keys when <see cref="UseAgent"/> is set. A null password means don't try one.
/// </summary>
public sealed record SshLogin(string Username, string? Password, string? PrivateKey = null, string? Passphrase = null, bool UseAgent = false)
{
    // The generated one would print the password, key and passphrase.
    public override string ToString() => $"SshLogin {{ Username = {Username}, UseAgent = {UseAgent} }}";
}

/// <summary>A jump host to go through on the way (ssh -J): where it is, how to log in, and whether old algorithms are allowed.</summary>
public sealed record SshHop(ConnectionTarget Target, SshLogin Login, bool AllowLegacy = false);

/// <summary>
/// An interactive shell over SSH.NET with an xterm-256color pty. The host key is checked during key
/// exchange, before any credentials are sent. Jump hosts are gone through with an <see cref="SshTunnel"/>.
/// </summary>
public sealed class SshSession : ITerminalSession
{
    const string TerminalType = "xterm-256color";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    readonly HostKeyValidator _hostKeys;
    readonly bool _allowLegacy;
    readonly IReadOnlyList<SshHop> _jumps;
    readonly IReadOnlyList<PortForward> _forwards;
    readonly object _lock = new();
    SshTunnel? _tunnel;
    SshClient? _client;
    ShellStream? _shell;
    ShellPump? _pump;
    int _columns, _rows;
    bool _closing;

    SshSession(ConnectionTarget target, HostKeyValidator hostKeys, int columns, int rows, bool allowLegacy,
        IReadOnlyList<SshHop> jumps, IReadOnlyList<PortForward> forwards)
    {
        Target = target;
        _hostKeys = hostKeys;
        _allowLegacy = allowLegacy;
        _jumps = jumps;
        _forwards = forwards;
        (_columns, _rows) = (columns, rows);
    }

    public ConnectionTarget Target { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;

    /// <summary>True when the server turned down every way of logging in that was tried.</summary>
    public bool AuthenticationRejected { get; private set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// Starts connecting in the background and returns at once; watch <see cref="StateChanged"/>. SHA-1, CBC and 3DES
    /// are only negotiated when <paramref name="allowLegacy"/> is set. <paramref name="jumps"/> are gone through
    /// in order, outermost first; <paramref name="forwards"/> open once logged in, and any that can't are reported
    /// in the terminal without ending the session.
    /// </summary>
    public static SshSession Start(ConnectionTarget target, SshLogin login, HostKeyValidator hostKeys, int columns, int rows, bool allowLegacy = false,
        IReadOnlyList<SshHop>? jumps = null, IReadOnlyList<PortForward>? forwards = null)
    {
        var session = new SshSession(target, hostKeys, columns, rows, allowLegacy, jumps ?? [], forwards ?? []);
        _ = Task.Run(() => session.Run(login));
        return session;
    }

    void Run(SshLogin login)
    {
        try
        {
            string host = Target.Host;
            int port = Target.Port;
            if (_jumps.Count > 0)
            {
                var tunnel = new SshTunnel(_jumps, _hostKeys);
                lock (_lock)
                {
                    if (_closing)
                    {
                        return;
                    }

                    _tunnel = tunnel;
                }

                try
                {
                    (host, port) = tunnel.Open(Target.Host, Target.Port);
                }
                catch (RouteException e)
                {
                    Fail(e.Message);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            if (ConnectClient(host, port, login) is not { } client)
            {
                return;
            }

            OpenForwards(client);

            int columns, rows;
            lock (_lock) (columns, rows) = (_columns, _rows);
            var shell = client.CreateShellStream(TerminalType, (uint)columns, (uint)rows, 0, 0, 64 * 1024);
            shell.ErrorOccurred += (_, e) => Fail(e.Exception.Message);

            // Output is read off the stream, not taken from its DataReceived event: the stream keeps everything
            // until it's read. The reader reports the end once the last output is out, which the stream's
            // Closed event (raised on another thread) can't promise.
            var pump = new ShellPump(shell, data => DataReceived?.Invoke(data), () => SetState(SessionState.Disconnected), Fail);
            lock (_lock) (_shell, _pump) = (shell, pump);
            SetState(SessionState.Connected);
            pump.Start();
        }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException or InvalidOperationException or ArgumentException)
        {
            Trace.TraceError($"SSH session to {Target.Host} failed: {e}");
            Fail(Describe(e, _allowLegacy));
        }
    }

    /// <summary>
    /// Connects and logs in to the server, dialling <paramref name="host"/>:<paramref name="port"/> (a loopback
    /// forward when it's behind a jump host). Null, with the session failed, when it couldn't.
    /// </summary>
    SshClient? ConnectClient(string host, int port, SshLogin login)
    {
        var target = Target;
        bool allowLegacy = _allowLegacy;
        bool hostKeyRejected = false;
        try
        {
            var info = new ConnectionInfo(host, port, login.Username, AuthenticationMethods(login))
            {
                Timeout = ConnectTimeout,
            };
            SshAlgorithms.Apply(info, allowLegacy);

            var client = new SshClient(info) { KeepAliveInterval = KeepAlive };
            client.HostKeyReceived += (_, e) =>
            {
                // SSH.NET asks synchronously on its own thread; the prompt runs on the UI thread meanwhile. The key
                // is checked against the real name, not the loopback address it's reached through.
                e.CanTrust = _hostKeys.ValidateAsync(target.Host, target.Port, e.HostKeyName, e.FingerPrintSHA256)
                    .GetAwaiter().GetResult();
                hostKeyRejected = !e.CanTrust;
            };
            client.ErrorOccurred += (_, e) => Fail(Describe(e.Exception, allowLegacy));

            lock (_lock)
            {
                if (_closing)
                {
                    client.Dispose();
                    return null;
                }

                _client = client;
            }

            client.Connect();
            return client;
        }
        catch (SshConnectionException) when (hostKeyRejected)
        {
            Fail("The server's host key was not trusted.");
        }
        catch (SshAuthenticationException e)
        {
            // The server's own refusal sends the caller back to ask for a login; a jump host's is only reported.
            AuthenticationRejected = true;
            Fail(Refusal(e, login));
        }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException or InvalidOperationException or ArgumentException)
        {
            Trace.TraceError($"SSH connection to {target.Host} failed: {e}");
            Fail(Describe(e, allowLegacy));
        }

        return null;
    }

    /// <summary>Why a login was turned down, by what was tried.</summary>
    internal static string Refusal(SshAuthenticationException e, SshLogin login) => e is NoLoginMethodException ? e.Message : login switch
    {
        { UseAgent: true, Password: null, PrivateKey: null } => "The server didn't accept any of the SSH agent's keys.",
        { PrivateKey: not null } => "The server didn't accept the key.",
        _ => "The username or password was rejected.",
    };

    /// <summary>Opens the connection's port forwards, saying in the terminal which opened and which couldn't.</summary>
    void OpenForwards(SshClient client)
    {
        foreach (var forward in _forwards)
        {
            try
            {
                ForwardedPort port = forward.Kind switch
                {
                    ForwardKind.Local => new ForwardedPortLocal(forward.EffectiveListenAddress, (uint)forward.ListenPort, forward.DestinationHost!, (uint)forward.DestinationPort),
                    ForwardKind.Remote => new ForwardedPortRemote(forward.EffectiveListenAddress, (uint)forward.ListenPort, forward.DestinationHost!, (uint)forward.DestinationPort),
                    _ => new ForwardedPortDynamic(forward.EffectiveListenAddress, (uint)forward.ListenPort),
                };
                port.Exception += (_, e) => Notice($"Forward {forward}: {e.Exception.Message}", warning: true);
                client.AddForwardedPort(port);
                port.Start();
                Notice($"Forwarding {forward.Describe()}");
            }
            catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)
            {
                Notice($"Couldn't open forward {forward}: {e.Message}", warning: true);
            }
        }
    }

    /// <summary>A line of Orrery's own in the terminal, set apart from the server's output by colour.</summary>
    void Notice(string text, bool warning = false) =>
        DataReceived?.Invoke(Encoding.UTF8.GetBytes($"\x1b[{(warning ? "33" : "90")}m» {text}\x1b[0m\r\n"));
    // SSH.NET may report the same failure through ErrorOccurred and by throwing; whichever lands first is shown.
    internal static string Describe(Exception e, bool allowLegacy) => e is SshConnectionException connection ? SshAlgorithms.ExplainFailure(connection, allowLegacy) : e.Message;

    /// <summary>Checks a private key (OpenSSH, PEM or PuTTY format) and its passphrase. Null when it's usable.</summary>
    public static string? ValidatePrivateKey(string key, string? passphrase)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(key));
            using var file = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
            return null;
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return "This key is protected; enter its passphrase.";
        }
        catch (Exception e) when (e is SshException or InvalidOperationException or ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return $"This doesn't look like a usable private key: {e.Message}";
        }
    }

    internal static AuthenticationMethod[] AuthenticationMethods(SshLogin login)
    {
        var methods = new List<AuthenticationMethod>();
        if (login.UseAgent && AgentKeys() is { Length: > 0 } agentKeys)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(login.Username, agentKeys));
        }

        if (login.PrivateKey is { Length: > 0 } key)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(key));
            var keyFile = string.IsNullOrEmpty(login.Passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, login.Passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(login.Username, keyFile));
        }
        else if (login.Password is { } password)
        {
            var interactive = new KeyboardInteractiveAuthenticationMethod(login.Username);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = password;
                }
            };
            methods.Add(new PasswordAuthenticationMethod(login.Username, password));
            methods.Add(interactive);
        }

        if (methods.Count == 0)
        {
            throw new NoLoginMethodException();
        }

        return [.. methods];
    }

    /// <summary>The running agent's keys, or none when there's no agent or it can't be reached.</summary>
    static IPrivateKeySource[] AgentKeys()
    {
        try
        {
            if (SshAgentClient.Find() is not { } agent)
            {
                return [];
            }

            return [.. agent.ListIdentities().Select(identity => new AgentKeySource(agent, identity))];
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
        {
            Trace.TraceWarning($"Couldn't use the SSH agent: {e.Message}");
            return [];
        }
    }

    /// <summary>Queues the bytes for the background writer and returns at once, so a stalled server can't block the caller.</summary>
    public void Send(ReadOnlySpan<byte> data)
    {
        ShellPump? pump;
        lock (_lock) pump = _pump;
        if (pump is null || State != SessionState.Connected)
        {
            return;
        }

        pump.Send(data);
    }

    public void Resize(int columns, int rows)
    {
        ShellStream? shell;
        lock (_lock)
        {
            (_columns, _rows) = (columns, rows);
            shell = _shell;
        }

        try
        {
            shell?.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        }
        catch (Exception e) when (e is ObjectDisposedException or SshException)
        {
            // Closing anyway.
        }
    }

    public Task DisconnectAsync()
    {
        SshClient? client;
        ShellStream? shell;
        ShellPump? pump;
        SshTunnel? tunnel;
        lock (_lock)
        {
            _closing = true;
            (client, shell, pump, tunnel) = (_client, _shell, _pump, _tunnel);
            (_client, _shell, _pump, _tunnel) = (null, null, null, null);
        }

        pump?.Stop();
        return Task.Run(async () =>
        {
            try
            {
                // Disposing the stream wakes the reader; closing the client frees a writer stuck sending.
                shell?.Dispose();
                if (client?.IsConnected == true) client.Disconnect();
            }
            catch (Exception e) when (e is SshException or ObjectDisposedException or IOException)
            {
                // Already gone.
            }
            finally
            {
                client?.Dispose();
                tunnel?.Dispose();

                await WaitForAsync(pump);
                SetState(SessionState.Disconnected);
            }
        });
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    async Task WaitForAsync(ShellPump? pump)
    {
        // Bounded, and never throws, so closing always ends in Disconnected.
        if (pump is not null && await Task.WhenAny(pump.Completion, Task.Delay(StopTimeout)) != pump.Completion)
        {
            Trace.TraceWarning($"SSH session to {Target.Host} left its reader or writer running after closing.");
        }
    }

    void Fail(string message)
    {
        if (State is SessionState.Disconnected or SessionState.Failed) return;
        SetState(SessionState.Failed, message);
    }

    void SetState(SessionState state, string? message = null)
    {
        lock (_lock)
        {
            if (State == state || State is SessionState.Disconnected or SessionState.Failed && state != SessionState.Connecting) return;
            State = state;
        }

        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
    }
}

/// <summary>Nothing to log in with: the agent had no keys (or went away) and there's no password or key.</summary>
sealed class NoLoginMethodException() : SshAuthenticationException("The SSH agent has no keys, and there's no password to try.");
