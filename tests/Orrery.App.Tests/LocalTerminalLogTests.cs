using System.Diagnostics;
using System.Text;
using Orrery.App.Logging;
using Orrery.Core.Sessions;
using Orrery.Core.Storage;
using Orrery.Protocols.Local;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>A real local shell, with detailed logging on: the log says what ran and how it ended, never what was typed or shown.</summary>
[Collection(nameof(LoggingTests))]
public class LocalTerminalLogTests : IDisposable
{
    const string Typed = "leak-typed-local-P4q8";

    readonly string _root = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Keystrokes_and_output_of_a_local_shell_never_reach_the_log()
    {
        var location = new DataLocation(StorageMode.Portable, _root, Path.Combine(_root, "cache"), Path.Combine(_root, "logs"));
        Directory.CreateDirectory(_root);
        var shell = OperatingSystem.IsWindows()
            ? new LocalShell("cmd", "Command Prompt", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), [])
            : new LocalShell("sh", "sh", "/bin/sh", []);

        AppLog.Start(location);
        AppLog.Detailed = true;
        LocalTerminalSession? session = null;
        Process? process = null;
        try
        {
            session = LocalTerminalSession.Start(shell, 80, 24);
            var output = new StringBuilder();
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.DataReceived += data =>
            {
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(data));
                    if (output.ToString().Split(Typed).Length > 2) seen.TrySetResult(); // The echo of the line, then what it printed.
                }
            };
            session.StateChanged += (_, e) =>
            {
                if (e.State == SessionState.Connected) connected.TrySetResult();
                if (e.State == SessionState.Disconnected) ended.TrySetResult();
            };

            if (session.State == SessionState.Connected) connected.TrySetResult();
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
            process = Process.GetProcessById(session.ProcessId!.Value);
            if (OperatingSystem.IsWindows())
            {
                _ = process.Handle; // Held, so the cleanup below can't reach a process that reused the id.
            }

            session.Send(Encoding.UTF8.GetBytes($"echo {Typed}\r"));
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
            session.Send("exit 0\r"u8);
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }

            process?.Dispose();
            AppLog.Detailed = false;
            AppLog.Stop();
        }

        var log = string.Join("\n", Directory.GetFiles(location.LogDir).Select(File.ReadAllText));
        Assert.Contains($"Started {shell.Name}", log);
        Assert.Contains("exited with code 0", log);
        Assert.DoesNotContain(Typed, log);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
