using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Orrery.Protocols.Local.UnixNative;

namespace Orrery.Protocols.Local;

/// <summary>
/// A program on a Unix pseudo-terminal. The child comes from posix_spawn rather than fork, since forking a .NET
/// process and running managed code in the child isn't safe. It starts a session of its own (POSIX_SPAWN_SETSID)
/// and opens the pty's other end by name as its terminal, which makes that its controlling terminal, so Ctrl+C,
/// job control and SIGWINCH on resize work. Written for Linux and macOS; not yet run on either.
/// </summary>
[UnsupportedOSPlatform("windows")]
sealed unsafe class UnixPseudoTerminal : IPseudoTerminal
{
    const int PollMilliseconds = 100;
    static readonly TimeSpan HangUpGrace = TimeSpan.FromSeconds(2), DrainLimit = TimeSpan.FromSeconds(1);

    readonly int _master;
    readonly ManualResetEventSlim _exited = new();
    long _endOutputAt; // Stopwatch ticks once the output should end; 0 until then.
    int _closed;

    UnixPseudoTerminal(int master, int processId) => (_master, ProcessId) = (master, processId);

    public int ProcessId { get; }

    public static UnixPseudoTerminal Start(LocalShell shell, int columns, int rows, string workingDirectory)
    {
        // Close-on-exec on Linux; macOS doesn't take the flag here, so POSIX_SPAWN_CLOEXEC_DEFAULT does it there.
        int master = posix_openpt(OpenPtFlags);
        if (master < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Couldn't open a pseudo-terminal.");
        }

        IntPtr attributes = IntPtr.Zero, actions = IntPtr.Zero, signals = IntPtr.Zero, argv = IntPtr.Zero, envp = IntPtr.Zero;
        try
        {
            var name = new byte[1024];
            if (grantpt(master) != 0 || unlockpt(master) != 0 || ptsname_r(master, name, (nuint)name.Length) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Couldn't set up the pseudo-terminal.");
            }

            var slave = System.Text.Encoding.UTF8.GetString(name, 0, Array.IndexOf(name, (byte)0));
            SetWindowSize(master, columns, rows);

            attributes = Opaque();
            actions = Opaque();
            signals = Opaque();
            Check(posix_spawnattr_init(attributes));
            Check(posix_spawn_file_actions_init(actions));

            // Every signal back to its default (.NET ignores SIGPIPE, which would carry over) and none blocked.
            sigfillset(signals);
            Check(posix_spawnattr_setsigdefault(attributes, signals));
            sigemptyset(signals);
            Check(posix_spawnattr_setsigmask(attributes, signals));
            Check(posix_spawnattr_setflags(attributes, SpawnFlags));

            Check(posix_spawn_file_actions_addopen(actions, 0, slave, ORdWr, 0));
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1));
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2));
            try
            {
                Check(posix_spawn_file_actions_addchdir_np(actions, workingDirectory));
            }
            catch (EntryPointNotFoundException)
            {
                // An older libc: the shell starts in Orrery's own folder instead.
            }

            var program = shell.Program;
            var argv0 = shell.Login ? "-" + Path.GetFileName(program) : program;
            argv = StringArray([argv0, .. shell.Arguments]);
            envp = StringArray(Environment());
            int result = posix_spawn(out int pid, program, actions, attributes, argv, envp);
            if (result != 0)
            {
                throw new Win32Exception(result, $"Couldn't start {program}.");
            }

            return new UnixPseudoTerminal(master, pid);
        }
        catch
        {
            close(master);
            throw;
        }
        finally
        {
            if (attributes != IntPtr.Zero) { posix_spawnattr_destroy(attributes); Marshal.FreeHGlobal(attributes); }
            if (actions != IntPtr.Zero) { posix_spawn_file_actions_destroy(actions); Marshal.FreeHGlobal(actions); }
            if (signals != IntPtr.Zero) Marshal.FreeHGlobal(signals);
            if (argv != IntPtr.Zero) FreeStringArray(argv);
            if (envp != IntPtr.Zero) FreeStringArray(envp);
        }
    }

    /// <summary>Orrery's environment, telling programs what the terminal understands.</summary>
    static List<string> Environment()
    {
        var variables = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.Ordinal);
        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";
        return variables.Select(v => $"{v.Key}={v.Value}").ToList();
    }

    static void Check(int result)
    {
        if (result != 0)
        {
            throw new Win32Exception(result, "Couldn't start the shell.");
        }
    }

    /// <summary>
    /// Waits for output in short polls, so the loop can give up once <see cref="EndOutput"/> says to: a program
    /// left running in the background can hold the terminal open after the shell has gone.
    /// </summary>
    public int Read(Span<byte> buffer)
    {
        while (true)
        {
            long end = Volatile.Read(ref _endOutputAt);
            if (Volatile.Read(ref _closed) != 0 || (end != 0 && Stopwatch.GetTimestamp() > end))
            {
                return 0;
            }

            var fd = new PollFd { Fd = _master, Events = PollIn };
            int ready = poll(ref fd, 1, PollMilliseconds);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == EIntr) continue;
                return 0;
            }

            if (ready == 0)
            {
                if (end != 0) return 0; // Nothing more after the shell exited.
                continue;
            }

            if ((fd.Revents & PollIn) == 0)
            {
                return 0; // Hung up, with nothing left to read.
            }

            fixed (byte* pointer = buffer)
            {
                var count = read(_master, pointer, (nuint)buffer.Length);
                if (count > 0) return (int)count;
                if (count < 0 && Marshal.GetLastPInvokeError() is var error && (error == EIntr || error == EAgain)) continue;
                return 0; // EIO: the last program holding the other end closed it.
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            int done = 0;
            while (done < data.Length && Volatile.Read(ref _closed) == 0)
            {
                var written = write(_master, pointer + done, (nuint)(data.Length - done));
                if (written > 0)
                {
                    done += (int)written;
                }
                else if (Marshal.GetLastPInvokeError() is var error && error != EIntr && error != EAgain)
                {
                    return; // The program has gone; its exit is reported elsewhere.
                }
            }
        }
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _closed) == 0)
        {
            SetWindowSize(_master, columns, rows); // The kernel sends the program SIGWINCH.
        }
    }

    public int WaitForExit()
    {
        try
        {
            while (true)
            {
                if (waitpid(ProcessId, out int status, 0) == ProcessId)
                {
                    int signal = status & 0x7f;
                    return signal == 0 ? (status >> 8) & 0xff : 128 + signal; // As shells report a signal.
                }

                if (Marshal.GetLastPInvokeError() != EIntr)
                {
                    return -1;
                }
            }
        }
        finally
        {
            _exited.Set();
        }
    }

    public void EndOutput()
    {
        Interlocked.CompareExchange(ref _endOutputAt, Stopwatch.GetTimestamp() + (long)(DrainLimit.TotalSeconds * Stopwatch.Frequency), 0);
    }

    /// <summary>
    /// SIGHUP to the shell's process group, as a terminal closing sends; shells pass it on to their jobs. Whatever
    /// is still there after a short wait gets SIGKILL.
    /// </summary>
    public void Kill()
    {
        // Once the shell has been waited for, its number can belong to someone else.
        if (_exited.IsSet)
        {
            return;
        }

        kill(-ProcessId, SigHup);
        kill(ProcessId, SigHup);
        if (!_exited.Wait(HangUpGrace))
        {
            kill(-ProcessId, SigKill);
            kill(ProcessId, SigKill);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            close(_master);
        }

        _exited.Dispose();
    }
}
