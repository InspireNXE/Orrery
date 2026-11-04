using Devolutions.IronRdp;
using Xunit;

namespace Orrery.Protocols.Rdp.Tests;

public class RdpStrandedReadTests
{
    /// <summary>A stream whose one read finishes only when the test says.</summary>
    sealed class HeldStream : Stream
    {
        public readonly TaskCompletionSource<int> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Pending.Task);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_read_a_cancelled_ReadPdu_leaves_is_observed_once_drained(bool drained)
    {
        var marker = $"reset {Guid.NewGuid():N}";
        bool unobserved = false;
        EventHandler<UnobservedTaskExceptionEventArgs> seen = (_, e) => unobserved |= e.Exception.Flatten().InnerExceptions.Any(x => x.Message == marker);
        TaskScheduler.UnobservedTaskException += seen;
        try
        {
            await StrandAndFailAsync(drained, marker);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            // Without the drain the failure goes unobserved, which is what the test would miss if it caught nothing.
            Assert.Equal(!drained, unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= seen;
        }
    }

    // Its own method, so nothing on the test's stack keeps the stranded read alive for the collector.
    static async Task StrandAndFailAsync(bool drained, string marker)
    {
        var stream = new HeldStream();
        var framed = new Framed<HeldStream>(stream);
        using (var cancel = new CancellationTokenSource())
        {
            var read = framed.ReadPdu(cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }

        if (drained)
        {
            RdpSession.DrainStrandedRead(framed);
        }

        stream.Pending.SetException(new IOException(marker));
        await Task.Delay(50);
    }
}
