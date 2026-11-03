using System.ComponentModel;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;

namespace Orrery.Protocols.Local;

/// <summary>
/// A shell on this computer, in a pseudo-terminal so full-screen programs and resizing work as in any terminal.
/// "Connected" while the shell runs; it ends Disconnected when the shell exits, with the exit code in the message,
/// and Failed when it can't be started. Ending the session ends the shell and everything it started.
/// </summary>
public sealed class LocalTerminalSession : ITerminalSession, ILocalSession
{
    static readonly ILogger Logger = Log.For("Local");
    static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(10);

    readonly object _lock = new();
    readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    readonly string _workingDirectory;
    IPseudoTerminal? _pty;
    int _columns, _rows;
    bool _ending;
    Task? _run;

    LocalTerminalSession(LocalShell shell, int columns, int rows, string workingDirectory)
    {
        Shell = shell;
        (_columns, _rows) = (columns, rows);
        _workingDirectory = workingDirectory;
        Target = new ConnectionTarget(Environment.MachineName, 0, Environment.UserName, null);
    }

    public LocalShell Shell { get; }
    public ConnectionTarget Target { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;
    public string ProgramName => Shell.Name;
    public int? ExitCode { get; private set; }

    /// <summary>The shell's process id once it has started.</summary>
    public int? ProcessId { get; private set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public SessionStateChangedEventArgs? LastChange { get; private set; }
    public event Action<byte[]>? DataReceived;

    /// <summary>Starts the shell in the background and returns at once; watch <see cref="StateChanged"/>.</summary>
    /// <param name="workingDirectory">Where it starts; the user's home folder when null.</param>
    public static LocalTerminalSession Start(LocalShell shell, int columns, int rows, string? workingDirectory = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var session = new LocalTerminalSession(shell, columns, rows, workingDirectory ?? (Directory.Exists(home) ? home : Environment.CurrentDirectory));
        session._run = Task.Run(session.RunAsync);
        return session;
    }

    async Task RunAsync()
    {
        IPseudoTerminal pty;
        int columns, rows;
        try
        {
            lock (_lock) (columns, rows) = (_columns, _rows);
            pty = OperatingSystem.IsWindows()
                ? WindowsPseudoTerminal.Start(Shell, columns, rows, _workingDirectory)
                : UnixPseudoTerminal.Start(Shell, columns, rows, _workingDirectory);
        }
        catch (Exception e) when (e is Win32Exception or DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("Couldn't start {Shell} ({Program}): {Message}", Shell.Name, Shell.Program, e.Message);
            SetState(SessionState.Failed, e.Message);
            return;
        }

        bool endedAlready;
        lock (_lock)
        {
            _pty = pty;
            ProcessId = pty.ProcessId;
            endedAlready = _ending;

            // A resize that came while the shell was starting only changed the size wanted.
            if (!endedAlready && (_columns, _rows) != (columns, rows))
            {
                pty.Resize(_columns, _rows);
            }
        }

        Logger.LogInformation("Started {Shell} ({Program}) as process {ProcessId} in {Folder}", Shell.Name, Shell.Program, pty.ProcessId, _workingDirectory);
        if (endedAlready)
        {
            pty.Kill();
        }
        else
        {
            SetState(SessionState.Connected);
        }

        var reader = Task.Factory.StartNew(() => ReadLoop(pty), TaskCreationOptions.LongRunning);
        var writer = Task.Run(() => WriteLoopAsync(pty));
        int code = await Task.Factory.StartNew(pty.WaitForExit, TaskCreationOptions.LongRunning);

        // What the shell wrote last can still be on its way; the reader stops once it has arrived.
        pty.EndOutput();
        await reader;
        _writes.Writer.TryComplete();
        await writer;

        bool byUs;
        lock (_lock)
        {
            byUs = _ending;
            _pty = null;
            pty.Dispose();
        }

        if (byUs)
        {
            Logger.LogInformation("Ended {Shell} (process {ProcessId})", Shell.Name, pty.ProcessId);
            SetState(SessionState.Disconnected);
        }
        else
        {
            ExitCode = code;
            Logger.LogInformation("{Shell} (process {ProcessId}) exited with code {ExitCode}", Shell.Name, pty.ProcessId, code);
            SetState(SessionState.Disconnected, $"The shell exited with code {code}.");
        }
    }

    void ReadLoop(IPseudoTerminal pty)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int read;
            while ((read = pty.Read(buffer)) > 0)
            {
                DataReceived?.Invoke(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (Exception e)
        {
            // From a DataReceived handler. The shell can't be shown any more, so it's ended rather than left running unseen.
            Logger.LogError(e, "The local terminal for {Shell} stopped because of an internal error", Shell.Name);
            lock (_lock) _ending = true;
            pty.Kill();
        }
    }

    async Task WriteLoopAsync(IPseudoTerminal pty)
    {
        await foreach (var data in _writes.Reader.ReadAllAsync())
        {
            pty.Write(data);
        }
    }

    /// <summary>Keystrokes to the shell, in the order they were typed. Never blocks.</summary>
    public void Send(ReadOnlySpan<byte> data)
    {
        if (State == SessionState.Connected && data.Length > 0)
        {
            _writes.Writer.TryWrite(data.ToArray());
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_lock)
        {
            (_columns, _rows) = (columns, rows);
            _pty?.Resize(columns, rows);
        }
    }

    /// <summary>Ends the shell and everything it started, and waits for it to go.</summary>
    public async Task DisconnectAsync()
    {
        IPseudoTerminal? pty;
        lock (_lock)
        {
            _ending = true;
            pty = _pty;
        }

        if (pty is not null)
        {
            await Task.Run(() =>
            {
                try
                {
                    pty.Kill();
                }
                catch (ObjectDisposedException)
                {
                    // It exited and was cleaned up meanwhile.
                }
            });
        }

        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(EndTimeout);
            }
            catch (TimeoutException)
            {
                Logger.LogWarning("{Shell} (process {ProcessId}) didn't end within {Seconds} s", Shell.Name, ProcessId, EndTimeout.TotalSeconds);
            }
        }

        SetState(SessionState.Disconnected);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    void SetState(SessionState state, string? message = null)
    {
        lock (_lock)
        {
            if (State == state || State is SessionState.Disconnected or SessionState.Failed) return;
            State = state;
        }

        var change = new SessionStateChangedEventArgs(state, message);
        LastChange = change;
        StateChanged?.Invoke(this, change);
    }
}
