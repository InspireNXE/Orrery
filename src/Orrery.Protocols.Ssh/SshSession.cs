using System.Diagnostics;
using System.Text;
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

/// <summary>
/// An interactive shell over SSH.NET with an xterm-256color pty. The host key is checked during key
/// exchange, before any credentials are sent.
/// </summary>
public sealed class SshSession : ITerminalSession
{
    const string TerminalType = "xterm-256color";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    readonly HostKeyValidator _hostKeys;
    readonly bool _allowLegacy;
    readonly object _lock = new();
    SshClient? _client;
    ShellStream? _shell;
    ShellPump? _pump;
    int _columns, _rows;

    SshSession(ConnectionTarget target, HostKeyValidator hostKeys, int columns, int rows, bool allowLegacy)
    {
        Target = target;
        _hostKeys = hostKeys;
        _allowLegacy = allowLegacy;
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
    /// are only negotiated when <paramref name="allowLegacy"/> is set.
    /// </summary>
    public static SshSession Start(ConnectionTarget target, SshLogin login, HostKeyValidator hostKeys, int columns, int rows, bool allowLegacy = false)
    {
        var session = new SshSession(target, hostKeys, columns, rows, allowLegacy);
        _ = Task.Run(() => session.Run(login));
        return session;
    }

    void Run(SshLogin login)
    {
        try
        {
            var info = new ConnectionInfo(Target.Host, Target.Port, login.Username, AuthenticationMethods(login))
            {
                Timeout = ConnectTimeout,
            };
            SshAlgorithms.Apply(info, _allowLegacy);

            var client = new SshClient(info) { KeepAliveInterval = KeepAlive };
            bool hostKeyRejected = false;
            client.HostKeyReceived += (_, e) =>
            {
                // SSH.NET asks synchronously on its own thread; the prompt runs on the UI thread meanwhile.
                e.CanTrust = _hostKeys.ValidateAsync(Target.Host, Target.Port, e.HostKeyName, e.FingerPrintSHA256)
                    .GetAwaiter().GetResult();
                hostKeyRejected = !e.CanTrust;
            };
            client.ErrorOccurred += (_, e) => Fail(Describe(e.Exception));

            lock (_lock) _client = client;
            try
            {
                client.Connect();
            }
            catch (SshConnectionException) when (hostKeyRejected)
            {
                Fail("The server's host key was not trusted.");
                return;
            }

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
        catch (NoLoginMethodException e)
        {
            AuthenticationRejected = true;
            Fail(e.Message);
        }
        catch (SshAuthenticationException)
        {
            AuthenticationRejected = true;
            Fail(login switch
            {
                { UseAgent: true, Password: null, PrivateKey: null } => "The server didn't accept any of the SSH agent's keys.",
                { PrivateKey: not null } => "The server didn't accept the key.",
                _ => "The username or password was rejected.",
            });
        }
        catch (Exception e) when (e is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException or InvalidOperationException or ArgumentException)
        {
            Trace.TraceError($"SSH session to {Target.Host} failed: {e}");
            Fail(Describe(e));
        }
    }

    // SSH.NET may report the same failure through ErrorOccurred and by throwing; whichever lands first is shown.
    string Describe(Exception e) => e is SshConnectionException connection ? SshAlgorithms.ExplainFailure(connection, _allowLegacy) : e.Message;

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

    static AuthenticationMethod[] AuthenticationMethods(SshLogin login)
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
        lock (_lock)
        {
            (client, shell, pump) = (_client, _shell, _pump);
            (_client, _shell, _pump) = (null, null, null);
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
