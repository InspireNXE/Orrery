using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Orrery.Protocols.Local;

/// <summary>
/// A program on a Windows pseudo console (ConPTY, Windows 10 1809 and later). Console output comes back as
/// UTF-8 with VT sequences, and keys go in the same way. The program runs in a job object that kills everything
/// in it when the job closes, so closing the tab, or Orrery ending however it ends, takes its children too.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class WindowsPseudoTerminal : IPseudoTerminal
{
    readonly IntPtr _console;
    readonly FileStream _input, _output;
    readonly SafeProcessHandle _process;
    readonly SafeJobHandle _job;
    readonly object _consoleLock = new();
    bool _consoleClosed;

    WindowsPseudoTerminal(IntPtr console, FileStream input, FileStream output, SafeProcessHandle process, SafeJobHandle job, int processId)
    {
        (_console, _input, _output, _process, _job, ProcessId) = (console, input, output, process, job, processId);
    }

    public int ProcessId { get; }

    public static WindowsPseudoTerminal Start(LocalShell shell, int columns, int rows, string workingDirectory)
    {
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null;
        IntPtr console = IntPtr.Zero, attributes = IntPtr.Zero;
        SafeJobHandle? job = null;
        bool started = false;
        try
        {
            if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
            {
                throw new Win32Exception();
            }

            int result = CreatePseudoConsole(Size(columns, rows), inputRead, outputWrite, 0, out console);
            if (result != 0)
            {
                throw new Win32Exception(result, "Windows couldn't create a pseudo console.");
            }

            // The pseudo console's own one attribute: which console the new process attaches to.
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception();
            }

            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                throw new Win32Exception();
            }

            var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception();
            }

            // Standard handles set, and empty: without them a child of a process whose own output is redirected
            // (a test run, say) writes there instead of to the pseudo console.
            var startup = new StartupInfoEx
            {
                StartupInfo = { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = StartfUseStdHandles },
                lpAttributeList = attributes,
            };
            var commandLine = new StringBuilder(WindowsCommandLine.For(shell.Program, shell.Arguments));

            // Started suspended so it's in the job before it can start anything of its own.
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent | CreateSuspended | CreateUnicodeEnvironment,
                    IntPtr.Zero, workingDirectory, ref startup, out var info))
            {
                throw new Win32Exception();
            }

            var process = new SafeProcessHandle(info.hProcess, ownsHandle: true);
            try
            {
                if (!AssignProcessToJobObject(job, process))
                {
                    throw new Win32Exception();
                }

                ResumeThread(info.hThread);
            }
            catch
            {
                TerminateProcess(process, 1);
                process.Dispose();
                throw;
            }
            finally
            {
                CloseHandle(info.hThread);
            }

            started = true;
            return new WindowsPseudoTerminal(console, new FileStream(inputWrite, FileAccess.Write, 0), new FileStream(outputRead, FileAccess.Read, 0),
                process, job, info.dwProcessId);
        }
        finally
        {
            // The pseudo console has its own copies of these ends.
            inputRead?.Dispose();
            outputWrite?.Dispose();
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (!started)
            {
                inputWrite?.Dispose();
                outputRead?.Dispose();
                job?.Dispose();
                if (console != IntPtr.Zero)
                {
                    ClosePseudoConsole(console);
                }
            }
        }
    }

    public int Read(Span<byte> buffer)
    {
        try
        {
            return _output.Read(buffer);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return 0;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            _input.Write(data);
            _input.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The program has gone; its exit is reported elsewhere.
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (_consoleLock)
        {
            if (!_consoleClosed)
            {
                ResizePseudoConsole(_console, Size(columns, rows));
            }
        }
    }

    public int WaitForExit()
    {
        WaitForSingleObject(_process, Infinite);
        return GetExitCodeProcess(_process, out var code) ? (int)code : -1;
    }

    /// <summary>Closing the pseudo console sends what it still holds, then ends the output pipe.</summary>
    public void EndOutput()
    {
        lock (_consoleLock)
        {
            if (!_consoleClosed)
            {
                _consoleClosed = true;
                ClosePseudoConsole(_console);
            }
        }
    }

    public void Kill() => TerminateJobObject(_job, 1);

    public void Dispose()
    {
        EndOutput();
        _input.Dispose();
        _output.Dispose();
        _process.Dispose();
        _job.Dispose();
    }

    static Coord Size(int columns, int rows) => new((short)Math.Clamp(columns, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue));

    // ---- Win32 ----

    const int ExtendedStartupInfoPresent = 0x00080000, CreateSuspended = 0x00000004, CreateUnicodeEnvironment = 0x00000400;
    const int StartfUseStdHandles = 0x00000100;
    const uint Infinite = 0xFFFFFFFF;
    static readonly IntPtr ProcThreadAttributePseudoConsole = 0x00020016;
    const int JobObjectExtendedLimitInformationClass = 9;
    const uint JobObjectLimitKillOnJobClose = 0x00002000;

    sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Coord(short X, short Y);

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, int creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateJobObjectW")]
    static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}

/// <summary>How a program and its arguments become one Windows command line.</summary>
static class WindowsCommandLine
{
    /// <summary>
    /// A command line that splits back into <paramref name="program"/> and <paramref name="arguments"/> the way the
    /// C runtime does: quotes around anything with a space or a quote, and backslashes before a quote doubled.
    /// </summary>
    public static string For(string program, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { $"\"{program}\"" }.Concat(arguments.Select(Quote)));

    static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
