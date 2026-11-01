using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace Orrery.Protocols.Ssh.Tests;

public class ShellPumpTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Behaves like SSH.NET's ShellStream: what arrives is buffered until read, Read waits for data or close
    /// and returns 0 once closed and empty, and Length is what's still buffered. Writes can be held up.
    /// </summary>
    sealed class FakeShellStream : Stream
    {
        readonly object _sync = new();
        readonly List<byte> _buffer = [];
        readonly MemoryStream _written = new();
        readonly ManualResetEventSlim _writeGate = new(true);
        bool _closed;

        public SemaphoreSlim WriteEntered { get; } = new(0);

        /// <summary>What the server has received, as text.</summary>
        public string WrittenText
        {
            get { lock (_written) return Encoding.ASCII.GetString(_written.ToArray()); }
        }

        public void Push(byte[] data)
        {
            lock (_sync)
            {
                _buffer.AddRange(data);
                Monitor.PulseAll(_sync);
            }
        }

        public void BlockWrites() => _writeGate.Reset();
        public void ReleaseWrites() => _writeGate.Set();

        public override void Close()
        {
            lock (_sync)
            {
                _closed = true;
                Monitor.PulseAll(_sync);
            }

            _writeGate.Set();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                while (_buffer.Count == 0 && !_closed) Monitor.Wait(_sync);
                int n = Math.Min(count, _buffer.Count);
                _buffer.CopyTo(0, buffer, offset, n);
                _buffer.RemoveRange(0, n);
                return n;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteEntered.Release();
            _writeGate.Wait();
            lock (_sync) ObjectDisposedException.ThrowIf(_closed, this);
            lock (_written) _written.Write(buffer, offset, count);
        }

        public override long Length
        {
            get { lock (_sync) return _buffer.Count; }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Output_is_read_as_it_arrives_and_does_not_pile_up()
    {
        var stream = new FakeShellStream();
        var received = new MemoryStream();
        int closed = 0;
        var pump = new ShellPump(stream, data => { lock (received) received.Write(data); }, () => Interlocked.Increment(ref closed), (_, _) => { });
        pump.Start();

        // 8 MiB, in the 32 KiB packets a busy host sends.
        var expected = new MemoryStream();
        for (int i = 0; i < 256; i++)
        {
            var chunk = new byte[32 * 1024];
            for (int j = 0; j < chunk.Length; j++) chunk[j] = (byte)(i + j);
            stream.Push(chunk);
            expected.Write(chunk);
            Assert.True(SpinWait.SpinUntil(() => stream.Length == 0, Timeout), $"{stream.Length} bytes piled up in the stream.");
        }

        stream.Close();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref closed) == 1, Timeout));
        lock (received) Assert.Equal(expected.ToArray(), received.ToArray());

        pump.Stop();
        await pump.Completion.WaitAsync(Timeout);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Send_returns_at_once_while_the_writer_is_blocked()
    {
        var stream = new FakeShellStream();
        var failures = new ConcurrentQueue<string>();
        var pump = new ShellPump(stream, _ => { }, () => { }, (message, _) => failures.Enqueue(message));
        pump.Start();

        stream.BlockWrites();
        pump.Send("a"u8);
        Assert.True(stream.WriteEntered.Wait(Timeout), "The writer never wrote.");

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            pump.Send([(byte)('0' + i)]);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Send took {watch.Elapsed}.");
        Assert.Empty(failures);

        stream.ReleaseWrites();
        Assert.True(SpinWait.SpinUntil(() => stream.WrittenText == "a0123456789", Timeout), stream.WrittenText);
        Assert.Empty(failures);
    }

    [Fact]
    public void A_full_queue_fails_the_session()
    {
        var stream = new FakeShellStream();
        var failures = new ConcurrentQueue<string>();
        var pump = new ShellPump(stream, _ => { }, () => { }, (message, _) => failures.Enqueue(message));
        pump.Start();

        stream.BlockWrites();
        pump.Send("a"u8);
        Assert.True(stream.WriteEntered.Wait(Timeout), "The writer never wrote.");

        // The writer holds one; the queue takes QueueCapacity more; the rest don't fit.
        for (int i = 0; i < ShellPump.QueueCapacity + 2; i++)
        {
            pump.Send("b"u8);
        }

        Assert.Equal(["The server stopped accepting input."], failures);
    }

    [Fact]
    public async Task Stop_ends_both_loops_without_reporting_a_failure()
    {
        var stream = new FakeShellStream();
        var failures = new ConcurrentQueue<string>();
        var pump = new ShellPump(stream, _ => { }, () => { }, (message, _) => failures.Enqueue(message));
        pump.Start();

        stream.BlockWrites();
        pump.Send("a"u8);
        Assert.True(stream.WriteEntered.Wait(Timeout), "The writer never wrote.");

        // As DisconnectAsync does: stop, then close the stream, which fails the write that's under way.
        pump.Stop();
        stream.Close();
        await pump.Completion.WaitAsync(Timeout); // Throws TimeoutException if either is still running.

        pump.Send("b"u8);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task A_fault_while_handling_output_fails_the_session()
    {
        var stream = new FakeShellStream();
        var failures = new ConcurrentQueue<string>();
        var pump = new ShellPump(stream, _ => throw new InvalidOperationException("emulator bug"), () => { }, (message, _) => failures.Enqueue(message));
        pump.Start();

        stream.Push("output"u8.ToArray());
        Assert.True(SpinWait.SpinUntil(() => !failures.IsEmpty, Timeout), "The fault went unreported.");

        pump.Stop();
        stream.Close();
        await pump.Completion.WaitAsync(Timeout);
        Assert.Equal(["The SSH session stopped because of an internal error."], failures);
    }

    [Fact]
    public void A_login_prints_without_its_secrets()
    {
        var text = new SshLogin("alice", "hunter2-password", "-----BEGIN KEY-----", "key-passphrase", UseAgent: true).ToString();

        Assert.Contains("alice", text);
        Assert.DoesNotContain("hunter2-password", text);
        Assert.DoesNotContain("BEGIN KEY", text);
        Assert.DoesNotContain("key-passphrase", text);
    }
}
