using System.IO.Pipelines;
using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Protocols.Serial.Tests;

public class SerialSessionTests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>A serial port with a device on the other end: what the device writes, the session reads, and the other way round.</summary>
    sealed class FakePort : ISerialPort
    {
        readonly Pipe _toHost = new(), _toDevice = new();

        public FakePort() => Stream = new Duplex(_toHost.Reader.AsStream(), _toDevice.Writer.AsStream());

        public Stream Stream { get; }
        public bool Disposed { get; private set; }

        public Task DeviceSendsAsync(string text) => _toHost.Writer.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

        public void DeviceGoesAway() => _toHost.Writer.Complete(new IOException("The device was unplugged."));

        public async Task<string> DeviceReadsAsync(int length)
        {
            using var timeout = new CancellationTokenSource(Wait);
            var text = new StringBuilder();
            while (text.Length < length)
            {
                var result = await _toDevice.Reader.ReadAsync(timeout.Token);
                foreach (var segment in result.Buffer)
                {
                    text.Append(Encoding.ASCII.GetString(segment.Span));
                }

                _toDevice.Reader.AdvanceTo(result.Buffer.End);
            }

            return text.ToString();
        }

        public void Dispose()
        {
            Disposed = true;
            _toHost.Writer.Complete();
        }

        sealed class Duplex(Stream read, Stream write) : Stream
        {
            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => read.ReadAsync(buffer, token);
            public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => write.WriteAsync(buffer, token);
            public override Task FlushAsync(CancellationToken token) => write.FlushAsync(token);
            public override void Flush() => write.Flush();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    static async Task<SessionState> StateAsync(IConnectionSession session, SessionState wanted)
    {
        var until = DateTime.UtcNow + Wait;
        while (session.State != wanted && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        return session.State;
    }

    [Fact]
    public async Task Opens_the_named_port_with_the_line_settings_and_passes_bytes_both_ways()
    {
        var port = new FakePort();
        (string Name, SerialLine Line)? opened = null;
        var line = new SerialLine(115200, 7, SerialParity.Even, SerialStopBits.Two, SerialFlowControl.RtsCts);
        var received = new StringBuilder();
        await using var session = SerialSession.Start(new ConnectionTarget("COM7", 0, null, null), line, (name, l) =>
        {
            opened = (name, l);
            return port;
        });
        session.DataReceived += data => { lock (received) received.Append(Encoding.ASCII.GetString(data)); };

        Assert.Equal(SessionState.Connected, await StateAsync(session, SessionState.Connected));
        Assert.Equal(("COM7", line), opened);

        await port.DeviceSendsAsync("switch> ");
        var until = DateTime.UtcNow + Wait;
        while (!received.ToString().Contains("switch> ") && DateTime.UtcNow < until)
        {
            await Task.Delay(10);
        }

        Assert.Contains("switch> ", received.ToString());

        session.Send("show version\r"u8);
        Assert.Equal("show version\r", await port.DeviceReadsAsync("show version\r".Length));
    }

    [Fact]
    public async Task A_port_that_wont_open_fails_with_a_plain_reason()
    {
        await using var session = SerialSession.Start(new ConnectionTarget("COM9", 0, null, null), SerialLine.Default,
            (_, _) => throw new UnauthorizedAccessException("Access to the path 'COM9' is denied."));
        string? message = null;
        session.StateChanged += (_, e) => message ??= e.Message;

        Assert.Equal(SessionState.Failed, await StateAsync(session, SessionState.Failed));
        Assert.StartsWith("COM9 is in use by another program", message ?? session.State.ToString());
    }

    [Fact]
    public async Task A_handler_added_after_the_port_failed_still_hears_why()
    {
        await using var session = SerialSession.Start(new ConnectionTarget("COM9", 0, null, null), SerialLine.Default,
            (_, _) => throw new UnauthorizedAccessException("Access to the path 'COM9' is denied."));
        Assert.Equal(SessionState.Failed, await StateAsync(session, SessionState.Failed));

        var heard = new List<SessionStateChangedEventArgs>();
        session.StateChanged += (_, e) => heard.Add(e);
        var change = Assert.Single(heard);
        Assert.Equal(SessionState.Failed, change.State);
        Assert.StartsWith("COM9 is in use by another program", change.Message);
        Assert.IsType<UnauthorizedAccessException>(change.Error);
    }

    [Fact]
    public async Task A_device_that_goes_away_fails_the_session_so_it_can_be_tried_again()
    {
        var port = new FakePort();
        await using var session = SerialSession.Start(new ConnectionTarget("/dev/ttyUSB0", 0, null, null), SerialLine.Default, (_, _) => port);
        Assert.Equal(SessionState.Connected, await StateAsync(session, SessionState.Connected));

        port.DeviceGoesAway();

        Assert.Equal(SessionState.Failed, await StateAsync(session, SessionState.Failed));
    }

    [Fact]
    public async Task Disconnecting_closes_the_port()
    {
        var port = new FakePort();
        var session = SerialSession.Start(new ConnectionTarget("COM3", 0, null, null), SerialLine.Default, (_, _) => port);
        Assert.Equal(SessionState.Connected, await StateAsync(session, SessionState.Connected));

        await session.DisposeAsync();

        Assert.True(port.Disposed);
        Assert.Equal(SessionState.Disconnected, session.State);
    }

    [Fact]
    public async Task Line_settings_that_cant_work_are_refused_before_the_port_is_opened()
    {
        bool opened = false;
        await using var session = SerialSession.Start(new ConnectionTarget("COM3", 0, null, null), SerialLine.Default with { DataBits = 9 }, (_, _) =>
        {
            opened = true;
            return new FakePort();
        });

        Assert.Equal(SessionState.Failed, await StateAsync(session, SessionState.Failed));
        Assert.False(opened);
    }

    [Theory]
    [InlineData(9600, 8, SerialParity.None, SerialStopBits.One, SerialFlowControl.None, "9600 8N1")]
    [InlineData(115200, 7, SerialParity.Even, SerialStopBits.Two, SerialFlowControl.RtsCts, "115200 7E2, RTS/CTS")]
    [InlineData(2400, 8, SerialParity.Odd, SerialStopBits.OnePointFive, SerialFlowControl.XOnXOff, "2400 8O1.5, XON/XOFF")]
    public void Describes_the_line_the_usual_way(int baud, int bits, SerialParity parity, SerialStopBits stop, SerialFlowControl flow, string expected) =>
        Assert.Equal(expected, new SerialLine(baud, bits, parity, stop, flow).ToString());

    [Fact]
    public void The_line_comes_from_the_resolved_settings()
    {
        var folder = new ConnectionFolder { Settings = { BaudRate = 115200, Parity = SerialParity.Even } };
        var tree = new ConnectionTree(new ConnectionFolder());
        tree.Add(folder, tree.Root);
        var entry = new ConnectionEntry { Host = "COM3", Settings = { Protocol = Protocol.Serial, DataBits = 7 } };
        tree.Add(entry, folder);

        Assert.Equal(new SerialLine(115200, 7, SerialParity.Even, SerialStopBits.One, SerialFlowControl.None), SerialLine.From(SettingsResolver.Resolve(entry)));
    }
}
