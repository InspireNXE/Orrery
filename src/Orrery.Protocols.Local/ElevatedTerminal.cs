using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Orrery.Protocols.Local;

/// <summary>
/// A shell run as administrator in an Orrery tab, on Windows. A normal process can't host an elevated console, so
/// Orrery starts a copy of itself through UAC (<see cref="ElevatedHost"/>) that runs the shell on its own pseudo
/// console and passes the terminal's bytes over a named pipe. The pipe is made by this side, open to this user only,
/// one connection at most, under a name nobody can guess; this side accepts only the process it started, and the
/// helper accepts only the Orrery that started it. The helper ends the shell if the pipe breaks, so it never outlives
/// the tab. Anything already running as this user could still reach the shell through Orrery while it's open, which
/// is why the tab says it's an administrator shell.
/// </summary>
[SupportedOSPlatform("windows")]
sealed class ElevatedPseudoTerminal : IPseudoTerminal
{
    /// <summary>How long the UAC prompt can wait for an answer before the tab gives up.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);

    readonly NamedPipeServerStream _pipe;
    readonly Process _helper;
    readonly object _writeLock = new();
    readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    byte[]? _pending;
    int _pendingAt;

    internal ElevatedPseudoTerminal(NamedPipeServerStream pipe, Process helper, int processId)
    {
        (_pipe, _helper, ProcessId) = (pipe, helper, processId);
        _ = Task.Factory.StartNew(ReadFrames, TaskCreationOptions.LongRunning);
    }

    public int ProcessId { get; }

    /// <summary>Starts the helper through UAC and waits for it. Fails with <see cref="ElevationException"/> when it's refused.</summary>
    public static ElevatedPseudoTerminal Start(LocalShell shell, int columns, int rows, string workingDirectory)
    {
        var exe = Environment.ProcessPath ?? throw new ElevationException("Orrery couldn't find its own program file.");
        var name = "orrery-elevated-" + Guid.NewGuid().ToString("N");
        var pipe = NewPipe(name);

        Process helper;
        try
        {
            helper = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = ElevatedHost.Arguments(name, Environment.ProcessId, shell.Id, columns, rows, workingDirectory),
            }) ?? throw new ElevationException("Windows didn't start the administrator shell.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            pipe.Dispose();
            throw new ElevationException("Administrator access wasn't given, so the shell didn't start.");
        }
        catch
        {
            pipe.Dispose();
            throw;
        }

        try
        {
            using var timeout = new CancellationTokenSource(ConnectTimeout);
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != helper.Id)
            {
                throw new ElevationException("Something other than Orrery's administrator helper answered, so the shell was refused.");
            }

            // The helper's first word: the shell started, with its process id, or why it didn't.
            var (type, payload) = ElevatedFrames.Read(pipe) ?? throw new ElevationException("The administrator helper ended before the shell started.");
            return type switch
            {
                ElevatedFrames.Started => new ElevatedPseudoTerminal(pipe, helper, BinaryPrimitives.ReadInt32LittleEndian(payload)),
                ElevatedFrames.Failed => throw new ElevationException(Encoding.UTF8.GetString(payload)),
                _ => throw new ElevationException("The administrator helper said something Orrery doesn't understand."),
            };
        }
        catch (OperationCanceledException)
        {
            pipe.Dispose();
            helper.Dispose();
            throw new ElevationException("The administrator shell didn't start within two minutes.");
        }
        catch
        {
            pipe.Dispose();
            helper.Dispose();
            throw;
        }
    }

    /// <summary>The pipe: this user (or the administrator a standard account's UAC prompt logs in as), one connection, made fresh.</summary>
    internal static NamedPipeServerStream NewPipe(string name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        // A standard account's UAC prompt asks for an administrator's login, and the helper then runs as that account.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    void ReadFrames()
    {
        try
        {
            while (ElevatedFrames.Read(_pipe) is var (type, payload))
            {
                switch (type)
                {
                    case ElevatedFrames.Output:
                        _output.Writer.TryWrite(payload);
                        break;
                    case ElevatedFrames.Exited:
                        _output.Writer.TryComplete();
                        _exit.TrySetResult(BinaryPrimitives.ReadInt32LittleEndian(payload));
                        return;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException)
        {
        }

        // The helper went without saying how the shell ended.
        _output.Writer.TryComplete();
        _exit.TrySetResult(-1);
    }

    public int Read(Span<byte> buffer)
    {
        if (_pending is null || _pendingAt >= _pending.Length)
        {
            if (!_output.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult() || !_output.Reader.TryRead(out _pending))
            {
                return 0;
            }

            _pendingAt = 0;
        }

        int count = Math.Min(buffer.Length, _pending.Length - _pendingAt);
        _pending.AsSpan(_pendingAt, count).CopyTo(buffer);
        _pendingAt += count;
        return count;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        // A long paste goes in pieces, each well under the largest frame.
        for (int at = 0; at < data.Length; at += 64 * 1024)
        {
            Send(ElevatedFrames.Input, data.Slice(at, Math.Min(64 * 1024, data.Length - at)));
        }
    }

    public void Resize(int columns, int rows)
    {
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(size, columns);
        BinaryPrimitives.WriteInt32LittleEndian(size[4..], rows);
        Send(ElevatedFrames.Resize, size);
    }

    public int WaitForExit() => _exit.Task.GetAwaiter().GetResult();

    // The helper sends what's left of the output before it says the shell exited.
    public void EndOutput()
    {
    }

    public void Kill()
    {
        Send(ElevatedFrames.Kill, []);

        // Without the pipe the helper ends the shell too; a process run as administrator can't be ended from here.
        if (!_exit.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            _pipe.Dispose();
        }
    }

    public void Dispose()
    {
        _pipe.Dispose();
        _helper.Dispose();
    }

    void Send(byte type, ReadOnlySpan<byte> payload)
    {
        lock (_writeLock)
        {
            try
            {
                ElevatedFrames.Write(_pipe, type, payload);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The helper has gone; its end is reported by the reader.
            }
        }
    }

    const int ErrorCancelled = 1223;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}

/// <summary>The administrator shell couldn't start: refused at the UAC prompt, timed out, or the helper wasn't Orrery's.</summary>
public sealed class ElevationException(string message) : Exception(message);

/// <summary>
/// The elevated side (<see cref="ElevatedPseudoTerminal"/>): Orrery started through UAC with <see cref="Flag"/>. It
/// runs no window, connects to the pipe it was given, checks that the other end is the Orrery that started it (by
/// process id and program file), runs one of this computer's shells and relays the terminal until either side ends.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevatedHost
{
    public const string Flag = "--elevated-shell";

    internal static string Arguments(string pipe, int parent, string shellId, int columns, int rows, string workingDirectory) =>
        $"{Flag} {pipe} {parent} {Encode(shellId)} {columns} {rows} {Encode(workingDirectory)}";

    /// <summary>Runs the helper for <paramref name="args"/> (from after <see cref="Flag"/>); returns the process exit code.</summary>
    public static int Run(IReadOnlyList<string> args)
    {
        if (args.Count != 6 || !int.TryParse(args[1], out int parent) || !int.TryParse(args[3], out int columns) || !int.TryParse(args[4], out int rows)
            || !args[0].StartsWith("orrery-elevated-", StringComparison.Ordinal))
        {
            return 2;
        }

        // Overlapped, so reading the keys and writing the output don't wait on each other.
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return 3;
        }

        // Only the Orrery that asked: the pipe's maker is that process, and it runs this same program file.
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != parent || !SameProgram(parent))
        {
            return 4;
        }

        IPseudoTerminal pty;
        try
        {
            // One of this computer's shells by its id: the command line can't name a program of its own.
            var shellId = Decode(args[2]);
            var shell = LocalShells.Available().FirstOrDefault(s => s.Id == shellId)
                ?? throw new IOException("That shell isn't on this computer any more.");
            var folder = Decode(args[5]);
            pty = WindowsPseudoTerminal.Start(shell, columns, rows, Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or FormatException)
        {
            ElevatedFrames.Write(pipe, ElevatedFrames.Failed, Encoding.UTF8.GetBytes(e.Message));
            return 5;
        }

        var writeLock = new object();
        void Send(byte type, ReadOnlySpan<byte> payload)
        {
            lock (writeLock)
            {
                try
                {
                    ElevatedFrames.Write(pipe, type, payload);
                }
                catch (IOException)
                {
                }
            }
        }

        using (pty)
        {
            Span<byte> id = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(id, pty.ProcessId);
            Send(ElevatedFrames.Started, id);

            var output = new Thread(() =>
            {
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = pty.Read(buffer)) > 0)
                {
                    Send(ElevatedFrames.Output, buffer.AsSpan(0, read));
                }
            }) { IsBackground = true };
            output.Start();

            // Orrery's side: keys, sizes, the end. A broken pipe means the tab has gone, and the shell goes with it.
            var input = new Thread(() =>
            {
                try
                {
                    while (ElevatedFrames.Read(pipe) is var (type, payload))
                    {
                        switch (type)
                        {
                            case ElevatedFrames.Input:
                                pty.Write(payload);
                                break;
                            case ElevatedFrames.Resize when payload.Length == 8:
                                pty.Resize(BinaryPrimitives.ReadInt32LittleEndian(payload), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)));
                                break;
                            case ElevatedFrames.Kill:
                                pty.Kill();
                                return;
                        }
                    }

                    pty.Kill();
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException)
                {
                    // Gone already: the shell exited and the helper is closing, or the pipe broke and it's ended below.
                    try
                    {
                        pty.Kill();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }) { IsBackground = true };
            input.Start();

            int code = pty.WaitForExit();
            pty.EndOutput();
            output.Join(TimeSpan.FromSeconds(5));
            Span<byte> exit = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(exit, code);
            Send(ElevatedFrames.Exited, exit);
            return 0;
        }
    }

    static bool SameProgram(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid)
        {
            return false;
        }

        var path = new StringBuilder(32768);
        int length = path.Capacity;
        return QueryFullProcessImageName(process, 0, path, ref length)
            && string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(Environment.ProcessPath ?? string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Decode(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=')));
    }

    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int size);
}

/// <summary>What goes over the pipe: a type byte, a 32-bit little-endian length, the payload.</summary>
static class ElevatedFrames
{
    public const byte Started = 1, Failed = 2, Output = 3, Exited = 4, Input = 5, Resize = 6, Kill = 7;
    const int MaxPayload = 1024 * 1024;

    public static void Write(Stream pipe, byte type, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        pipe.Write(frame);
        pipe.Flush();
    }

    /// <summary>The next frame, or null at the end of the pipe.</summary>
    public static (byte Type, byte[] Payload)? Read(Stream pipe)
    {
        var head = new byte[5];
        if (!ReadAll(pipe, head))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(1));
        if (length is < 0 or > MaxPayload)
        {
            throw new InvalidDataException("A frame from the other side is too large.");
        }

        var payload = new byte[length];
        return ReadAll(pipe, payload) ? (head[0], payload) : null;
    }

    static bool ReadAll(Stream pipe, byte[] buffer)
    {
        int at = 0;
        while (at < buffer.Length)
        {
            int read = pipe.Read(buffer, at, buffer.Length - at);
            if (read == 0)
            {
                return false;
            }

            at += read;
        }

        return true;
    }
}
