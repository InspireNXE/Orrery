using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Renci.SshNet.Common;

namespace Orrery.Protocols.Ssh;

/// <summary>
/// Moves terminal bytes through a shell stream. A reader drains output as it arrives, so it never piles up in the
/// stream's buffer, and one background writer sends queued input, so a server that stops taking input never
/// blocks the caller. Reading and writing at once is safe: ShellStream keeps separate buffers for each.
/// </summary>
sealed class ShellPump
{
    /// <summary>Writes waiting to go out before the session is failed for not taking input.</summary>
    internal const int QueueCapacity = 512;
    const int ReadBufferSize = 64 * 1024;

    readonly Stream _stream;
    readonly Action<byte[]> _received;
    readonly Action _closed;
    readonly Action<string, Exception?> _failed;
    readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true });
    readonly CancellationTokenSource _stop = new();
    int _queueFull;

    /// <param name="received">Output, on the reader's thread.</param>
    /// <param name="closed">The stream ended, after the last of its output.</param>
    /// <param name="failed">Reading or writing failed, or the server stopped taking input: a message, and what was thrown if anything.</param>
    public ShellPump(Stream stream, Action<byte[]> received, Action closed, Action<string, Exception?> failed)
    {
        (_stream, _received, _closed, _failed) = (stream, received, closed, failed);
    }

    /// <summary>
    /// Both loops; they end once <see cref="Stop"/> is called and the stream is closed. The same task each time it's
    /// read, so it can be compared with what <see cref="Task.WhenAny(Task[])"/> returns.
    /// </summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Start()
    {
        var reader = Task.Factory.StartNew(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var writer = Task.Run(WriteLoopAsync);
        Completion = Task.WhenAll(reader, writer);
    }

    /// <summary>Waits up to <paramref name="timeout"/> for both loops to end; false when one is still running. Never throws.</summary>
    public async Task<bool> EndsWithinAsync(TimeSpan timeout)
    {
        var completion = Completion;
        return await Task.WhenAny(completion, Task.Delay(timeout)) == completion;
    }

    /// <summary>Queues a copy of <paramref name="data"/> and returns at once.</summary>
    public void Send(ReadOnlySpan<byte> data)
    {
        if (_stop.IsCancellationRequested || _queue.Writer.TryWrite(data.ToArray()))
        {
            return;
        }

        if (Interlocked.Exchange(ref _queueFull, 1) == 0)
        {
            _failed("The server stopped accepting input.", null);
        }
    }

    /// <summary>Tells both loops to finish; closing the stream wakes them if they're waiting on it.</summary>
    public void Stop()
    {
        _stop.Cancel();
        _queue.Writer.TryComplete();
    }

    void ReadLoop()
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            // Read waits for data and returns 0 once the stream is closed and drained.
            int read;
            while ((read = _stream.Read(buffer)) > 0)
            {
                _received(buffer.AsSpan(0, read).ToArray());
            }
        }
        catch (ObjectDisposedException)
        {
            // Closed.
        }
        catch (Exception e) when (e is SshException or IOException)
        {
            if (!_stop.IsCancellationRequested) _failed(e.Message, e);
            return;
        }
        catch (Exception e)
        {
            // From a DataReceived handler. Report it, or the tab would look connected with no output coming.
            Log.For("Ssh").LogError(e, "SSH output stopped");
            if (!_stop.IsCancellationRequested) _failed("The SSH session stopped because of an internal error.", null);
            return;
        }

        _closed();
    }

    async Task WriteLoopAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stop.Token))
            {
                // Whatever queued up meanwhile goes out together.
                while (_queue.Reader.TryRead(out var data))
                {
                    _stream.Write(data);
                }

                _stream.Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        catch (Exception e) when (e is ObjectDisposedException or SshException or IOException)
        {
            if (!_stop.IsCancellationRequested) _failed(e.Message, e);
        }
    }
}
