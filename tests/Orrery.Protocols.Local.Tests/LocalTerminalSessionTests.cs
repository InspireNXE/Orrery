using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Local.Tests;

/// <summary>
/// Real shells on real pseudo-terminals: cmd on Windows, /bin/sh elsewhere (the Unix runs are written but not yet
/// run). Every test ends its shell, and kills anything left of it, before it finishes.
/// </summary>
public class LocalTerminalSessionTests : IAsyncLifetime
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    static readonly bool Windows = OperatingSystem.IsWindows();

    readonly List<LocalTerminalSession> _sessions = [];
    readonly List<Process> _processes = [];

    sealed class Run(LocalTerminalSession session)
    {
        readonly StringBuilder _output = new();
        readonly object _lock = new();

        public LocalTerminalSession Session { get; } = session;

        public string Output { get { lock (_lock) return _output.ToString(); } }

        public void Attach() => Session.DataReceived += data =>
        {
            lock (_lock) _output.Append(Encoding.UTF8.GetString(data));
        };

        public void Type(string line) => Session.Send(Encoding.UTF8.GetBytes(line + "\r"));

        /// <summary>The output, without escape sequences, once <paramref name="pattern"/> matches it.</summary>
        public async Task<Match> WaitForAsync(string pattern)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (DateTime.UtcNow < deadline)
            {
                var match = Regex.Match(Plain(Output), pattern);
                if (match.Success)
                {
                    return match;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"No /{pattern}/ in the output:\n{Plain(Output)}");
        }

        /// <summary>
        /// ConPTY redraws with cursor moves rather than spaces and line breaks, so the sequences go and each one
        /// counts as a space.
        /// </summary>
        static string Plain(string text) => Regex.Replace(text, @"\x1b(\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(\x07|\x1b\\)|[@-Z\\-_])", " ");
    }

    LocalShell TestShell => Windows
        ? new LocalShell("cmd", "Command Prompt", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), [])
        : new LocalShell("sh", "sh", "/bin/sh", []);

    async Task<Run> StartAsync(int columns = 80, int rows = 24)
    {
        var session = LocalTerminalSession.Start(TestShell, columns, rows);
        _sessions.Add(session);
        var run = new Run(session);
        run.Attach();
        await WaitForStateAsync(session, SessionState.Connected);
        Track(session.ProcessId!.Value);
        return run;
    }

    static async Task WaitForStateAsync(LocalTerminalSession session, SessionState state)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, e) =>
        {
            if (e.State == state) reached.TrySetResult();
        };
        if (session.State == state) reached.TrySetResult();
        await reached.Task.WaitAsync(Wait);
    }

    [Fact]
    public async Task Echoes_what_is_typed_back_through_the_shell()
    {
        var run = await StartAsync();

        // The caret (cmd) or the empty quotes (sh) only vanish if the shell ran the line.
        run.Type(Windows ? "echo abc^def" : "echo abc''def");

        await run.WaitForAsync("abcdef");
        Assert.Equal(SessionState.Connected, run.Session.State);
    }

    [Fact]
    public async Task Starts_in_the_home_folder()
    {
        var run = await StartAsync();

        run.Type(Windows ? "cd" : "pwd");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await run.WaitForAsync(Regex.Escape(home) + @"\s");
    }

    [Fact]
    public async Task A_resize_reaches_programs_in_the_terminal()
    {
        var run = await StartAsync();

        run.Session.Resize(100, 30);
        // mode is part of cmd, so this doesn't wait on a program starting (PowerShell on a CI runner can take 30 s).
        run.Type(Windows ? "mode con" : "echo size=$(stty size)");

        await run.WaitForAsync(Windows ? @"Lines:\s+30\b[\s\S]*Columns:\s+100\b" : @"size=30 100\b");
    }

    [Fact]
    public async Task The_shell_exiting_ends_the_session_with_its_exit_code()
    {
        var run = await StartAsync();
        var ended = WaitForStateAsync(run.Session, SessionState.Disconnected);
        string? message = null;
        run.Session.StateChanged += (_, e) => message ??= e.State == SessionState.Disconnected ? e.Message : null;

        run.Type("exit 7");

        await ended;
        Assert.Equal(7, run.Session.ExitCode);
        Assert.Equal("The shell exited with code 7.", message);
    }

    [Fact]
    public async Task Closing_ends_the_shell_and_what_it_started()
    {
        var run = await StartAsync();
        var shell = _processes[^1];

        // A child that would otherwise sleep for ten minutes, printing its own process id first.
        run.Type(Windows
            ? "powershell -NoProfile -Command \"'child=' + $PID; Start-Sleep 600\""
            : "sh -c 'echo child=$$; exec sleep 600'");
        var child = Track(int.Parse((await run.WaitForAsync(@"child=(\d+)")).Groups[1].Value));
        Assert.False(child.HasExited);

        await run.Session.DisposeAsync();

        Assert.Equal(SessionState.Disconnected, run.Session.State);
        Assert.Null(run.Session.ExitCode);
        await WaitUntilAsync(() => shell.HasExited && child.HasExited);
    }

    [Fact]
    public async Task A_shell_that_cannot_start_fails()
    {
        var missing = new LocalShell("missing", "Missing", Path.Combine(Path.GetTempPath(), "orrery-no-such-shell-" + Guid.NewGuid().ToString("N")), []);
        var session = LocalTerminalSession.Start(missing, 80, 24);
        _sessions.Add(session);
        string? message = null;
        session.StateChanged += (_, e) => message ??= e.Message;

        await WaitForStateAsync(session, SessionState.Failed);

        Assert.Null(session.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    /// <summary>
    /// The process, held by handle on Windows so a later check or kill can't reach another process that reused its id.
    /// </summary>
    Process Track(int processId)
    {
        var process = Process.GetProcessById(processId);
        if (Windows)
        {
            _ = process.Handle;
        }

        _processes.Add(process);
        return process;
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(50);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Ends every shell, then kills anything of it still running, children included.</summary>
    public async Task DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            await session.DisposeAsync();
        }

        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone, which is what should happen.
            }

            process.Dispose();
        }
    }
}
