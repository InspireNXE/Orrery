using System.Diagnostics;
using System.Text;
using Xunit;

namespace Orrery.Protocols.Local.Tests;

// The UAC prompt itself can't be tested; everything after it can, with the helper run in this process. Its checks
// pass here because this process made the pipe and is the same program.
public class ElevatedTerminalTests
{
    static string PipeName() => "orrery-elevated-" + Guid.NewGuid().ToString("N");

    static string[] Args(string pipe, int parent, string shellId, string folder) =>
        ElevatedHost.Arguments(pipe, parent, shellId, 80, 24, folder).Split(' ')[1..];

    [Fact]
    public void The_helper_refuses_arguments_it_did_not_make()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(2, ElevatedHost.Run([]));
        Assert.Equal(2, ElevatedHost.Run(["some-other-pipe", "1", "Y21k", "80", "24", "fg"]));
    }

    [Fact]
    public async Task The_helper_runs_no_shell_for_a_pipe_another_process_made()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = PipeName();
        using var pipe = ElevatedPseudoTerminal.NewPipe(name);
        var waiting = pipe.WaitForConnectionAsync();

        // The pipe is this process's, but the helper is told its Orrery is another.
        using var other = Process.GetProcessesByName("explorer").FirstOrDefault() ?? Process.GetCurrentProcess();
        int code = await Task.Run(() => ElevatedHost.Run(Args(name, other.Id == Environment.ProcessId ? 4 : other.Id, LocalShells.Cmd, Path.GetTempPath())));
        await waiting;

        Assert.Equal(4, code);
        Assert.Null(ElevatedFrames.Read(pipe)); // Not a word, not a shell.
    }

    [Fact]
    public async Task A_shell_runs_through_the_helper_and_its_output_and_exit_come_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = PipeName();
        var pipe = ElevatedPseudoTerminal.NewPipe(name);
        var waiting = pipe.WaitForConnectionAsync();
        var helper = Task.Run(() => ElevatedHost.Run(Args(name, Environment.ProcessId, LocalShells.Cmd, Path.GetTempPath())));
        await waiting;

        var (type, payload) = ElevatedFrames.Read(pipe)!.Value;
        Assert.Equal(ElevatedFrames.Started, type);
        using var pty = new ElevatedPseudoTerminal(pipe, Process.GetCurrentProcess(), BitConverter.ToInt32(payload));
        Assert.True(pty.ProcessId > 0);

        var output = new StringBuilder();
        var reader = Task.Run(() =>
        {
            var buffer = new byte[4096];
            int read;
            while ((read = pty.Read(buffer)) > 0)
            {
                lock (output) output.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        });

        pty.Resize(100, 30);
        pty.Write("echo elevated-relay-works\r"u8);
        pty.Write("exit 7\r"u8);
        int exit = await Task.Run(pty.WaitForExit).WaitAsync(TimeSpan.FromSeconds(30));
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7, exit);
        lock (output) Assert.Contains("elevated-relay-works", output.ToString());
        Assert.Equal(0, await helper);
    }

    [Fact]
    public async Task Closing_Orrery_s_end_of_the_pipe_ends_the_shell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = PipeName();
        var pipe = ElevatedPseudoTerminal.NewPipe(name);
        var waiting = pipe.WaitForConnectionAsync();
        var helper = Task.Run(() => ElevatedHost.Run(Args(name, Environment.ProcessId, LocalShells.Cmd, Path.GetTempPath())));
        await waiting;
        var (_, payload) = ElevatedFrames.Read(pipe)!.Value;
        using var shell = Process.GetProcessById(BitConverter.ToInt32(payload));

        pipe.Dispose();

        Assert.Equal(0, await helper.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(shell.WaitForExit(TimeSpan.FromSeconds(10)));
    }
}
