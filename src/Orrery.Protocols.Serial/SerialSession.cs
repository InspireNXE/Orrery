using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;

namespace Orrery.Protocols.Serial;

/// <summary>How a serial line is set up: speed, character size, parity, stop bits and flow control.</summary>
public sealed record SerialLine(int BaudRate, int DataBits, SerialParity Parity, SerialStopBits StopBits, SerialFlowControl FlowControl)
{
    public static readonly SerialLine Default = new(SettingsResolver.DefaultBaudRate, SettingsResolver.DefaultDataBits, SerialParity.None, SerialStopBits.One, SerialFlowControl.None);

    public static SerialLine From(ResolvedSettings resolved) =>
        new(resolved.BaudRate.Value, resolved.DataBits.Value, resolved.Parity.Value, resolved.StopBits.Value, resolved.FlowControl.Value);

    /// <summary>The usual shorthand, such as "9600 8N1", with the flow control after it when there is some.</summary>
    public override string ToString()
    {
        char parity = Parity switch
        {
            SerialParity.Odd => 'O',
            SerialParity.Even => 'E',
            SerialParity.Mark => 'M',
            SerialParity.Space => 'S',
            _ => 'N',
        };
        var stop = StopBits switch
        {
            SerialStopBits.OnePointFive => "1.5",
            SerialStopBits.Two => "2",
            _ => "1",
        };
        var flow = FlowControl switch
        {
            SerialFlowControl.XOnXOff => ", XON/XOFF",
            SerialFlowControl.RtsCts => ", RTS/CTS",
            _ => string.Empty,
        };
        return $"{BaudRate} {DataBits}{parity}{stop}{flow}";
    }

    /// <summary>Why these settings can't be used, or null when they can. Checked again when a port opens.</summary>
    public string? Problem() =>
        BaudRate is < 50 or > 4_000_000 ? "The speed must be between 50 and 4,000,000 bits per second."
        : DataBits is < 5 or > 8 ? "The data bits must be 5, 6, 7 or 8."
        : null;
}

/// <summary>The serial port a session talks through; a real one, or a stand-in for tests.</summary>
public interface ISerialPort : IDisposable
{
    /// <summary>Reads and writes the line's bytes.</summary>
    Stream Stream { get; }
}

/// <summary>
/// A terminal on a serial port of this computer, such as a switch's or router's console cable (COM3 on Windows,
/// /dev/ttyUSB0 on Linux). Bytes go both ways as they are: no login, nothing negotiated. The connection's host is
/// the port's name, and its port number isn't used.
/// </summary>
public sealed class SerialSession : ITerminalSession
{
    readonly object _lock = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    readonly Func<string, SerialLine, ISerialPort> _open;
    readonly SerialLine _line;
    ISerialPort? _port;
    Task? _run;

    SerialSession(ConnectionTarget target, SerialLine line, Func<string, SerialLine, ISerialPort> open)
    {
        Target = target;
        _line = line;
        _open = open;
    }

    public ConnectionTarget Target { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    /// <summary>A port that doesn't exist fails to open at once, often before the tab has added its handler.</summary>
    public SessionStateChangedEventArgs? LastChange { get; private set; }
    public event Action<byte[]>? DataReceived;

    /// <summary>Opens the port in the background and returns at once; watch <see cref="StateChanged"/>.</summary>
    /// <param name="target">The port's name is the target's host.</param>
    /// <param name="open">Opens the port; a real serial port when null.</param>
    public static SerialSession Start(ConnectionTarget target, SerialLine line, Func<string, SerialLine, ISerialPort>? open = null)
    {
        var session = new SerialSession(target, line, open ?? SystemSerialPort.Open);
        session._run = Task.Run(session.RunAsync);
        return session;
    }

    /// <summary>The serial ports this computer has now, sorted; empty when they can't be listed.</summary>
    public static IReadOnlyList<string> PortNames()
    {
        try
        {
            return SerialPort.GetPortNames().Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            Log.For("Serial").LogWarning("Couldn't list the serial ports: {Message}", e.Message);
            return [];
        }
    }

    async Task RunAsync()
    {
        var token = _cts.Token;
        bool opened = false;
        try
        {
            if (_line.Problem() is { } problem)
            {
                throw new ArgumentException(problem);
            }

            var port = _open(Target.Host, _line);
            lock (_lock)
            {
                if (token.IsCancellationRequested)
                {
                    port.Dispose();
                    return;
                }

                _port = port;
            }

            opened = true;
            Log.For("Serial").LogInformation("Opened {Port} at {Line}", Target.Host, _line.ToString());
            SetState(SessionState.Connected);
            Notice($"Connected to {Target.Host} at {_line}. Press Enter if the device doesn't show a prompt.");

            var buffer = new byte[4096];
            while (!token.IsCancellationRequested)
            {
                int read = await port.Stream.ReadAsync(buffer, token);
                if (read == 0)
                {
                    break;
                }

                DataReceived?.Invoke(buffer[..read]);
            }

            SetState(SessionState.Disconnected);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            SetState(SessionState.Disconnected);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            // Before it opened, the message says why it couldn't (in use, not there). After, the device or its USB adapter
            // went away, which is a drop worth trying again after, so it fails rather than ending.
            var message = opened ? $"{Target.Host} stopped working: {e.Message}" : Describe(e, Target.Host);
            Log.For("Serial").LogWarning("Serial session on {Port} {Ended}: {Message}", Target.Host, opened ? "ended" : "didn't open", e.Message);
            SetState(SessionState.Failed, message, e);
        }
        catch (Exception e)
        {
            Log.For("Serial").LogError(e, "Serial session on {Port} failed", Target.Host);
            SetState(SessionState.Failed, "The serial session stopped because of an internal error.", e);
        }
    }

    /// <summary>What went wrong opening <paramref name="port"/>, for the user.</summary>
    internal static string Describe(Exception e, string port) => e switch
    {
        UnauthorizedAccessException => $"{port} is in use by another program, or you aren't allowed to open it."
            + (OperatingSystem.IsLinux() ? " On Linux, your user may need to be in the dialout group (uucp on Arch), then log out and back in." : string.Empty),
        FileNotFoundException or DirectoryNotFoundException => $"There's no serial port called {port} on this computer. Check that the cable is plugged in.",
        ArgumentException argument when argument.ParamName == "portName" || argument.Message.Contains("port name", StringComparison.OrdinalIgnoreCase)
            => $"\"{port}\" isn't a serial port name. Use something like COM3 on Windows or /dev/ttyUSB0 on Linux.",
        IOException io when io.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || io.Message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
            => $"There's no serial port called {port} on this computer. Check that the cable is plugged in.",
        _ => e.Message,
    };

    public void Send(ReadOnlySpan<byte> data)
    {
        if (State != SessionState.Connected || data.IsEmpty)
        {
            return;
        }

        _ = WriteAsync(data.ToArray());
    }

    /// <summary>A serial line has no window size to tell.</summary>
    public void Resize(int columns, int rows)
    {
    }

    async Task WriteAsync(byte[] bytes)
    {
        ISerialPort? port;
        lock (_lock) port = _port;
        if (port is null)
        {
            return;
        }

        try
        {
            await _writeLock.WaitAsync(_cts.Token);
            try
            {
                await port.Stream.WriteAsync(bytes, _cts.Token);
                await port.Stream.FlushAsync(_cts.Token);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            // The read loop reports the end of the line.
        }
    }

    public async Task DisconnectAsync()
    {
        _cts.Cancel();
        ISerialPort? port;
        lock (_lock) (port, _port) = (_port, null);
        port?.Dispose();
        if (_run is not null)
        {
            try
            {
                await _run;
            }
            catch (Exception e)
            {
                Log.For("Serial").LogWarning("Serial read loop for {Port} ended with {ExceptionType}: {Message}", Target.Host, e.GetType().Name, e.Message);
            }
        }

        SetState(SessionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts.Dispose();
    }

    /// <summary>A line of Orrery's own in the terminal, set apart from the device's output by color.</summary>
    void Notice(string text) => DataReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes($"\x1b[90m» {text}\x1b[0m\r\n"));

    void SetState(SessionState state, string? message = null, Exception? error = null)
    {
        var change = new SessionStateChangedEventArgs(state, message, error);
        lock (_lock)
        {
            if (State == state || State is SessionState.Disconnected or SessionState.Failed) return;
            LastChange = change; // Before State, so whoever sees the new state can read why.
            State = state;
        }

        StateChanged?.Invoke(this, change);
    }

    /// <summary>A port of this computer, through System.IO.Ports.</summary>
    sealed class SystemSerialPort(SerialPort port) : ISerialPort
    {
        public Stream Stream => port.BaseStream;

        public static ISerialPort Open(string name, SerialLine line)
        {
            var port = new SerialPort(name, line.BaudRate, line.Parity switch
            {
                SerialParity.Odd => Parity.Odd,
                SerialParity.Even => Parity.Even,
                SerialParity.Mark => Parity.Mark,
                SerialParity.Space => Parity.Space,
                _ => Parity.None,
            }, line.DataBits, line.StopBits switch
            {
                SerialStopBits.OnePointFive => StopBits.OnePointFive,
                SerialStopBits.Two => StopBits.Two,
                _ => StopBits.One,
            })
            {
                Handshake = line.FlowControl switch
                {
                    SerialFlowControl.XOnXOff => Handshake.XOnXOff,
                    SerialFlowControl.RtsCts => Handshake.RequestToSend,
                    _ => Handshake.None,
                },
                // A console expects the line raised as a terminal would.
                DtrEnable = true,
                WriteTimeout = 5000,
            };

            // With RTS/CTS the port drives RTS itself, and setting it throws.
            if (line.FlowControl != SerialFlowControl.RtsCts)
            {
                port.RtsEnable = true;
            }

            try
            {
                port.Open();
                return new SystemSerialPort(port);
            }
            catch
            {
                port.Dispose();
                throw;
            }
        }

        public void Dispose() => port.Dispose();
    }
}
