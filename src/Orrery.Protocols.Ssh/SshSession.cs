using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using Renci.SshNet;
using Orrery.Protocols.Ssh.Agent;
using Renci.SshNet.Common;

namespace Orrery.Protocols.Ssh;

/// <summary>
/// How to log in, tried in this order: <see cref="PrivateKey"/> (the text of a saved key), the private key file at
/// <see cref="KeyFile"/>, the SSH agent's keys when <see cref="UseAgent"/> is set, then <see cref="Password"/>
/// (which also answers keyboard-interactive prompts). Whatever is null or off isn't tried; a null password means
/// don't try one. The key file is read at connect time, each time, not when this is made.
/// </summary>
/// <param name="KeyFile">A path to a private key on this computer; a leading ~ is this user's home folder.</param>
/// <param name="KeyFilePassphrase">For an encrypted <paramref name="KeyFile"/>; the caller asks for it.</param>
public sealed record SshLogin(string Username, string? Password, string? PrivateKey = null, string? Passphrase = null, bool UseAgent = false,
    string? KeyFile = null, string? KeyFilePassphrase = null)
{
    // The generated one would print the password, key and passphrases, and the key file's path.
    public override string ToString() => $"SshLogin {{ Username = {Username}, UseAgent = {UseAgent} }}";
}

/// <summary>A jump host to go through on the way (ssh -J): where it is, how to log in, and whether old algorithms are allowed.</summary>
/// <param name="KeepAlive">How often to send keep-alive messages through this hop; <see cref="SshSession.KeepAlive"/> when null.</param>
public sealed record SshHop(ConnectionTarget Target, SshLogin Login, bool AllowLegacy = false, TimeSpan? KeepAlive = null);

/// <summary>
/// An interactive shell over SSH.NET with an xterm-256color pty. The host key is checked during key
/// exchange, before any credentials are sent. Jump hosts are gone through with an <see cref="SshTunnel"/>.
/// </summary>
public sealed class SshSession : ITerminalSession, ILinkStatsSession
{
    const string TerminalType = "xterm-256color";
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(Orrery.Core.Connections.SettingsResolver.DefaultKeepAliveSeconds);

    /// <summary>The keep-alive interval for a connection's setting in seconds; 0 or less sends none.</summary>
    public static TimeSpan KeepAliveFor(int seconds) => seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan;
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    readonly HostKeyValidator _hostKeys;
    readonly bool _allowLegacy;
    readonly IReadOnlyList<SshHop> _jumps;
    readonly IReadOnlyList<PortForward> _forwards;
    readonly TimeSpan _keepAlive;
    readonly object _lock = new();
    SshTunnel? _tunnel;
    SshClient? _client;
    ShellStream? _shell;
    ShellPump? _pump;
    int _columns, _rows;
    bool _closing;

    SshSession(ConnectionTarget target, HostKeyValidator hostKeys, int columns, int rows, bool allowLegacy,
        IReadOnlyList<SshHop> jumps, IReadOnlyList<PortForward> forwards, TimeSpan keepAlive)
    {
        Target = target;
        _keepAlive = keepAlive;
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

    public SessionStateChangedEventArgs? LastChange { get; private set; }
    public event Action<byte[]>? DataReceived;

    /// <summary>
    /// Starts connecting in the background and returns at once; watch <see cref="StateChanged"/>. SHA-1, CBC and 3DES
    /// are only negotiated when <paramref name="allowLegacy"/> is set. <paramref name="jumps"/> are gone through
    /// in order, outermost first; <paramref name="forwards"/> open once logged in, and any that can't are reported
    /// in the terminal without ending the session.
    /// </summary>
    /// <param name="keepAlive">How often to send a keep-alive message; <see cref="KeepAlive"/> when null, none when infinite.</param>
    public static SshSession Start(ConnectionTarget target, SshLogin login, HostKeyValidator hostKeys, int columns, int rows, bool allowLegacy = false,
        IReadOnlyList<SshHop>? jumps = null, IReadOnlyList<PortForward>? forwards = null, TimeSpan? keepAlive = null)
    {
        var session = new SshSession(target, hostKeys, columns, rows, allowLegacy, jumps ?? [], forwards ?? [], keepAlive ?? KeepAlive);
        _ = Task.Run(() => session.Run(login));
        return session;
    }

    /// <summary>The terminal's traffic, after decryption, and the round trip, for the session pane.</summary>
    public LinkStats Link { get; } = new();

    void Run(SshLogin login)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
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
                    Fail(e.Message, e);
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

            WarnAboutKeyFile(login);
            OpenForwards(client);
            // Behind a jump host the socket only reaches the local forward, whose round trip says nothing.
            if (_jumps.Count == 0)
            {
                Link.Socket = SshSocket.Of(client);
            }

            int columns, rows;
            lock (_lock) (columns, rows) = (_columns, _rows);
            var shell = client.CreateShellStream(TerminalType, (uint)columns, (uint)rows, 0, 0, 64 * 1024);
            shell.ErrorOccurred += (_, e) => Fail(e.Exception.Message, Recognize(e.Exception));

            // Output is read off the stream, not taken from its DataReceived event: the stream keeps everything
            // until it's read. The reader reports the end once the last output is out, which the stream's
            // Closed event (raised on another thread) can't promise.
            var pump = new ShellPump(new CountingStream(shell, Link), data => DataReceived?.Invoke(data), () => SetState(SessionState.Disconnected),
                (message, error) => Fail(message, error is null ? null : Recognize(error)));
            lock (_lock)
            {
                (_shell, _pump) = (shell, pump);

                // A resize that came while the shell was being opened only changed the size wanted. Sent under the
                // lock, as Resize sends, so an older size can't land after a newer one.
                if ((_columns, _rows) != (columns, rows))
                {
                    shell.ChangeWindowSize((uint)_columns, (uint)_rows, 0, 0);
                }
            }

            Link.ConnectTime = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            SetState(SessionState.Connected);
            pump.Start();
        }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException or InvalidOperationException or ArgumentException)
        {
            Log.For("Ssh").LogError(e, "SSH session to {Host} failed", Target.Host);
            Fail(Describe(e, _allowLegacy), Recognize(e));
        }
    }

    /// <summary>
    /// Connects and logs in to the server, dialing <paramref name="host"/>:<paramref name="port"/> (a loopback
    /// forward when it's behind a jump host). Null, with the session failed, when it couldn't.
    /// </summary>
    SshClient? ConnectClient(string host, int port, SshLogin login)
    {
        var target = Target;
        bool allowLegacy = _allowLegacy;
        HostKeyCheck? hostKey = null;
        try
        {
            var client = new SshClient(ConnectionInfoFor(host, port, login, allowLegacy)) { KeepAliveInterval = _keepAlive };
            hostKey = HostKeyCheck.Attach(client, _hostKeys, target);
            client.ErrorOccurred += (_, e) => Fail(Describe(e.Exception, allowLegacy), Recognize(e.Exception));

            lock (_lock)
            {
                if (_closing)
                {
                    client.Dispose();
                    return null;
                }

                _client = client;
            }

            hostKey.Connect(client, () =>
            {
                SshTunnel? tunnel;
                lock (_lock) tunnel = _tunnel;
                tunnel?.AllowRetry(port);
            });
            return client;
        }
        catch (SshConnectionException e) when (hostKey?.Rejected == true)
        {
            Fail("The server's host key was not trusted.", hostKey.Failure("The server's host key was not trusted.", e));
        }
        catch (SshAuthenticationException e)
        {
            // The server's own refusal sends the caller back to ask for a login; a jump host's is only reported.
            Log.For("Ssh").LogWarning("{Host}: login as {User} refused: {Reason}", target.Host, login.Username, e.Message);
            AuthenticationRejected = true;
            Fail(Refusal(e, login), Recognize(e));
        }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException or InvalidOperationException or ArgumentException)
        {
            Log.For("Ssh").LogError(e, "SSH connection to {Host} failed", target.Host);
            Fail(Describe(e, allowLegacy), Recognize(e));
        }

        return null;
    }

    /// <summary>
    /// Says, in the terminal and the log, that the login's key file can be read by other users (Linux and macOS).
    /// The login goes ahead; OpenSSH would refuse such a file, Orrery only warns.
    /// </summary>
    void WarnAboutKeyFile(SshLogin login)
    {
        if (login.KeyFile is { Length: > 0 } file && SshKeyFile.PermissionWarning(file) is { } warning)
        {
            Log.For("Ssh").LogWarning("{Host}: {Warning}", Target.Host, warning);
            Notice(warning, warning: true);
        }
    }

    /// <summary>
    /// How to reach and log in to a server at <paramref name="host"/>:<paramref name="port"/>, which is a loopback
    /// forward when it's behind a jump host. SHA-1, CBC and 3DES only when <paramref name="allowLegacy"/> is set.
    /// Reads the login's key file, so it throws <see cref="KeyFileException"/> when that can't be used.
    /// </summary>
    internal static ConnectionInfo ConnectionInfoFor(string host, int port, SshLogin login, bool allowLegacy)
    {
        var info = new ConnectionInfo(host, port, login.Username, AuthenticationMethods(login)) { Timeout = ConnectTimeout };
        SshAlgorithms.Apply(info, allowLegacy);
        return info;
    }

    /// <summary>Why a login was turned down, by what was tried.</summary>
    internal static string Refusal(SshAuthenticationException e, SshLogin login)
    {
        if (e is NoLoginMethodException)
        {
            return e.Message;
        }

        bool key = login.PrivateKey is { Length: > 0 }, file = login.KeyFile is { Length: > 0 }, agent = login.UseAgent, password = login.Password is not null;
        return (key, file, agent, password) switch
        {
            (false, false, false, _) => "The username or password was rejected.",
            (false, false, true, false) => "The server didn't accept any of the SSH agent's keys.",
            (true, false, false, false) => "The server didn't accept the key.",
            (false, true, false, false) => "The server didn't accept the key file.",
            (_, _, _, false) => "The server didn't accept any of the keys that were tried.",
            _ => "The server didn't accept the key or the password.",
        };
    }

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
                port.Exception += (_, e) =>
                {
                    Log.For("Ssh").LogWarning("{Host}: forward {Forward}: {Message}", Target.Host, forward.ToString(), e.Exception.Message);
                    Notice($"Forward {forward}: {e.Exception.Message}", warning: true);
                };
                client.AddForwardedPort(port);
                port.Start();
                Log.For("Ssh").LogInformation("{Host}: forwarding {Forward}", Target.Host, forward.Describe());
                Notice($"Forwarding {forward.Describe()}");
            }
            catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)
            {
                Log.For("Ssh").LogWarning("{Host}: couldn't open forward {Forward}: {Message}", Target.Host, forward.ToString(), e.Message);
                Notice($"Couldn't open forward {forward}: {e.Message}", warning: true);
            }
        }
    }

    /// <summary>A line of Orrery's own in the terminal, set apart from the server's output by color.</summary>
    void Notice(string text, bool warning = false) =>
        DataReceived?.Invoke(Encoding.UTF8.GetBytes($"\x1b[{(warning ? "33" : "90")}m» {text}\x1b[0m\r\n"));
    // SSH.NET may report the same failure through ErrorOccurred and by throwing; whichever lands first is shown.
    internal static string Describe(Exception e, bool allowLegacy) => e is SshConnectionException connection ? SshAlgorithms.ExplainFailure(connection, allowLegacy) : e.Message;

    /// <summary>
    /// SSH.NET's failures in terms <see cref="ConnectionFailure.Classify"/> knows, with the original inside: a refused
    /// login, its own timeout, and the server dropping or ending the connection. Socket errors already say what happened.
    /// </summary>
    internal static Exception Recognize(Exception e) => e switch
    {
        KeyFileException => new SessionFailureException(FailureKind.KeyFile, e.Message, e),
        SshAuthenticationException => new SessionFailureException(FailureKind.LoginFailed, e.Message, e),
        SshOperationTimeoutException => new TimeoutException(e.Message, e),
        SshConnectionException { DisconnectReason: Renci.SshNet.Messages.Transport.DisconnectReason.ConnectionLost or Renci.SshNet.Messages.Transport.DisconnectReason.ByApplication }
            => new SessionFailureException(FailureKind.ServerClosed, e.Message, e),
        _ => e,
    };

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

    /// <summary>
    /// The login methods in the order they're tried: one public-key method holding the saved key, then the key file,
    /// then the agent's keys, and after it the password. SSH.NET tries methods, and the keys in one, in the order
    /// given. Reads the key file now, so each (re)connect sees the file as it is.
    /// </summary>
    /// <param name="agentKeys">The agent's keys; the running agent's when null. A seam for tests.</param>
    /// <exception cref="KeyFileException">The key file can't be used.</exception>
    internal static AuthenticationMethod[] AuthenticationMethods(SshLogin login, Func<IPrivateKeySource[]>? agentKeys = null)
    {
        var methods = new List<AuthenticationMethod>();
        var keys = new List<IPrivateKeySource>();
        if (login.PrivateKey is { Length: > 0 } key)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(key));
            keys.Add(string.IsNullOrEmpty(login.Passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, login.Passphrase));
        }

        if (login.KeyFile is { Length: > 0 } keyFile)
        {
            keys.Add(SshKeyFile.Load(keyFile, login.KeyFilePassphrase));
        }

        if (login.UseAgent)
        {
            keys.AddRange((agentKeys ?? AgentKeys)());
        }

        if (keys.Count > 0)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(login.Username, [.. keys]));
        }

        if (login.Password is { } password)
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

            IPrivateKeySource[] keys = [.. agent.ListIdentities().Select(identity => new AgentKeySource(agent, identity))];
            Log.For("Ssh").LogDebug("The SSH agent offered {Count} keys", keys.Length);
            return keys;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or System.Net.Sockets.SocketException or System.ComponentModel.Win32Exception)
        {
            Log.For("Ssh").LogWarning("Couldn't use the SSH agent: {Message}", e.Message);
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
        try
        {
            // Under the lock, so sizes reach the server in the order they were asked for.
            lock (_lock)
            {
                (_columns, _rows) = (columns, rows);
                _shell?.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
            }
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
        if (pump is not null && !await pump.EndsWithinAsync(StopTimeout))
        {
            Log.For("Ssh").LogWarning("SSH session to {Host} left its reader or writer running after closing.", Target.Host);
        }
    }

    /// <summary>Ends the session as failed, unless it's being closed on purpose: that ends in Disconnected.</summary>
    void Fail(string message, Exception? error = null)
    {
        lock (_lock)
        {
            if (_closing) return;
        }

        if (State is SessionState.Disconnected or SessionState.Failed) return;
        SetState(SessionState.Failed, message, error);
    }

    void SetState(SessionState state, string? message = null, Exception? error = null)
    {
        lock (_lock)
        {
            if (State == state || State is SessionState.Disconnected or SessionState.Failed && state != SessionState.Connecting) return;
            State = state;
        }

        var change = new SessionStateChangedEventArgs(state, message, error);
        LastChange = change;
        StateChanged?.Invoke(this, change);
    }
}

/// <summary>Nothing to log in with: the agent had no keys (or went away) and there's no password or key.</summary>
sealed class NoLoginMethodException() : SshAuthenticationException("There's no key or password to try, and the SSH agent has no keys.");
