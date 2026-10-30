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
public sealed record SshLogin(string Username, string? Password, string? PrivateKey = null, string? Passphrase = null, bool UseAgent = false);

/// <summary>
/// An interactive shell over SSH.NET with an xterm-256color pty. The host key is checked during key
/// exchange, before any credentials are sent.
/// </summary>
public sealed class SshSession : ITerminalSession
{
    const string TerminalType = "xterm-256color";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    readonly HostKeyValidator _hostKeys;
    readonly object _lock = new();
    SshClient? _client;
    ShellStream? _shell;
    int _columns, _rows;

    SshSession(ConnectionTarget target, HostKeyValidator hostKeys, int columns, int rows)
    {
        Target = target;
        _hostKeys = hostKeys;
        (_columns, _rows) = (columns, rows);
    }

    public ConnectionTarget Target { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;

    /// <summary>True when the server turned down every way of logging in that was tried.</summary>
    public bool AuthenticationRejected { get; private set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event Action<byte[]>? DataReceived;

    /// <summary>Starts connecting in the background and returns at once; watch <see cref="StateChanged"/>.</summary>
    public static SshSession Start(ConnectionTarget target, SshLogin login, HostKeyValidator hostKeys, int columns, int rows)
    {
        var session = new SshSession(target, hostKeys, columns, rows);
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

            var client = new SshClient(info) { KeepAliveInterval = KeepAlive };
            bool hostKeyRejected = false;
            client.HostKeyReceived += (_, e) =>
            {
                // SSH.NET asks synchronously on its own thread; the prompt runs on the UI thread meanwhile.
                e.CanTrust = _hostKeys.ValidateAsync(Target.Host, Target.Port, e.HostKeyName, e.FingerPrintSHA256)
                    .GetAwaiter().GetResult();
                hostKeyRejected = !e.CanTrust;
            };
            client.ErrorOccurred += (_, e) => Fail(e.Exception.Message);

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
            shell.DataReceived += (_, e) => DataReceived?.Invoke(e.Data);
            shell.Closed += (_, _) => SetState(SessionState.Disconnected);
            shell.ErrorOccurred += (_, e) => Fail(e.Exception.Message);

            lock (_lock) _shell = shell;
            SetState(SessionState.Connected);
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
            Fail(e.Message);
        }
    }

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

    public void Send(ReadOnlySpan<byte> data)
    {
        ShellStream? shell;
        lock (_lock) shell = _shell;
        if (shell is null || State != SessionState.Connected)
        {
            return;
        }

        try
        {
            shell.Write(data);
            shell.Flush();
        }
        catch (Exception e) when (e is ObjectDisposedException or SshException or IOException)
        {
            Fail(e.Message);
        }
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
        lock (_lock)
        {
            (client, shell) = (_client, _shell);
            (_client, _shell) = (null, null);
        }

        return Task.Run(() =>
        {
            try
            {
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
                SetState(SessionState.Disconnected);
            }
        });
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

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
