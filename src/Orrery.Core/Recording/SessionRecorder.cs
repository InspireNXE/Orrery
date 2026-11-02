using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;

namespace Orrery.Core.Recording;

/// <summary>
/// Records one tab's sessions into one file, across automatic reconnects (docs/design-session-recording.md §2.3).
/// Events go through a queue to a writer of their own, so a slow disk never holds up the session.
/// </summary>
public abstract class SessionRecorder : IDisposable
{
    protected static readonly ILogger Logger = Log.For("Recording");

    /// <summary>Output waiting for the disk past this stops a terminal recording and drops screen frames.</summary>
    protected const long MaxBacklog = 64L * 1024 * 1024;

    static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(2);

    readonly RecordingWriter _writer;
    readonly long _maxBytes;
    readonly Func<TimeSpan> _clock;
    readonly Channel<(RecordingEventType Type, TimeSpan Time, byte[] Payload)> _queue =
        Channel.CreateUnbounded<(RecordingEventType, TimeSpan, byte[])>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _drain;
    readonly object _lock = new();
    long _backlog;
    bool _stopping;
    IConnectionSession? _session;
    bool _attachedBefore;

    protected SessionRecorder(RecordingWriter writer, RecordingInfo info, long maxBytes, Func<TimeSpan>? clock)
    {
        _writer = writer;
        _maxBytes = maxBytes;
        var watch = Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed);
        Path = writer.Path;
        Enqueue(RecordingEventType.Info, RecordingFile.InfoPayload(info));
        _drain = Task.Run(DrainAsync);
    }

    /// <summary>The file being written.</summary>
    public string Path { get; }

    public bool IsRecording
    {
        get
        {
            lock (_lock)
            {
                return !_stopping;
            }
        }
    }

    /// <summary>Why it stopped by itself (the size limit, the disk), for the tab; null when it was stopped on purpose.</summary>
    public string? StopReason { get; private set; }

    /// <summary>It stopped, on purpose or by itself. Raised once, on a background thread.</summary>
    public event EventHandler? Stopped;

    protected TimeSpan Now => _clock();

    /// <summary>How long it has been recording, for the session toolbar.</summary>
    public TimeSpan Elapsed => _clock();

    /// <summary>Bytes in the file so far, for the session toolbar; 0 once it's closed.</summary>
    public long BytesWritten => _writer.Length;

    /// <summary>Bytes queued for the disk.</summary>
    protected long Backlog => Interlocked.Read(ref _backlog);

    /// <summary>Starts listening to <paramref name="session"/>; a later one (a reconnect) is marked on the timeline.</summary>
    public void Attach(IConnectionSession session)
    {
        Detach();
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }

            _session = session;
        }

        if (_attachedBefore)
        {
            Marker("Reconnected");
        }

        _attachedBefore = true;
        session.StateChanged += OnStateChanged;
        OnAttach(session);
    }

    /// <summary>Stops listening to the session it has, which carries on; the file stays open for the next.</summary>
    public void Detach()
    {
        IConnectionSession? old;
        lock (_lock)
        {
            old = _session;
            _session = null;
        }

        if (old is not null)
        {
            old.StateChanged -= OnStateChanged;
            OnDetach(old);
        }
    }

    protected abstract void OnAttach(IConnectionSession session);

    protected abstract void OnDetach(IConnectionSession session);

    void OnStateChanged(object? sender, SessionStateChangedEventArgs e)
    {
        if (e.State is SessionState.Disconnected or SessionState.Failed)
        {
            Marker(e.State == SessionState.Failed ? "Connection lost" : "Disconnected");
        }
    }

    /// <summary>A note on the timeline.</summary>
    public void Marker(string text) => Enqueue(RecordingEventType.Marker, System.Text.Encoding.UTF8.GetBytes(text));

    /// <returns>False once it has stopped.</returns>
    protected bool Enqueue(RecordingEventType type, byte[] payload)
    {
        lock (_lock)
        {
            if (_stopping)
            {
                return false;
            }

            Interlocked.Add(ref _backlog, payload.Length);
            _queue.Writer.TryWrite((type, Now, payload));
            return true;
        }
    }

    /// <summary>Stops recording and closes the file; what was queued is written first.</summary>
    public void Stop() => StopWith(null);

    protected void StopWith(string? reason)
    {
        lock (_lock)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            StopReason = reason;
            _queue.Writer.TryComplete();
        }

        Detach();
    }

    /// <summary>Waits for the file to be closed, after <see cref="Stop"/>.</summary>
    public Task Completion => _drain;

    async Task DrainAsync()
    {
        using var flush = new PeriodicTimer(FlushEvery);
        var reader = _queue.Reader;
        var flushTick = flush.WaitForNextTickAsync().AsTask();
        try
        {
            while (true)
            {
                var ready = reader.WaitToReadAsync().AsTask();
                if (await Task.WhenAny(ready, flushTick) == flushTick)
                {
                    _writer.Flush();
                    flushTick = flush.WaitForNextTickAsync().AsTask();
                    await ready;
                }

                if (!ready.Result)
                {
                    break;
                }

                while (reader.TryRead(out var item))
                {
                    Interlocked.Add(ref _backlog, -item.Payload.Length);
                    _writer.Append(item.Type, item.Time, item.Payload);
                    if (_writer.Length >= _maxBytes)
                    {
                        StopWith($"This recording reached its size limit ({Describe(_maxBytes)}), so it stopped. The session carries on.");
                    }
                }
            }

            _writer.Complete(Now);
            Logger.LogInformation("Recording stopped: {File}", System.IO.Path.GetFileName(Path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            Logger.LogWarning("Recording {File} stopped: {Message}", System.IO.Path.GetFileName(Path), e.Message);
            StopWith($"Recording stopped because the file couldn't be written: {e.Message}");
            _writer.Dispose();
        }
        finally
        {
            Stopped?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static string Describe(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : $"{bytes / (double)(1L << 20):0.#} MB";

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Records what a terminal session sends back, never what's typed (design §2.1).</summary>
public sealed class TerminalRecorder : SessionRecorder
{
    ITerminalSession? _terminal;

    public TerminalRecorder(RecordingWriter writer, RecordingInfo info, int columns, int rows, long maxBytes, Func<TimeSpan>? clock = null)
        : base(writer, info, maxBytes, clock)
    {
        Resized(columns, rows);
    }

    protected override void OnAttach(IConnectionSession session)
    {
        if (session is ITerminalSession terminal)
        {
            _terminal = terminal;
            terminal.DataReceived += OnData;
        }
    }

    protected override void OnDetach(IConnectionSession session)
    {
        if (session is ITerminalSession terminal)
        {
            terminal.DataReceived -= OnData;
        }

        _terminal = null;
    }

    void OnData(byte[] data)
    {
        if (Backlog > MaxBacklog)
        {
            // Never skip output: a recording with holes in it would mislead.
            StopWith("Recording stopped because the disk couldn't keep up with the session.");
            return;
        }

        Enqueue(RecordingEventType.Output, data.ToArray());
    }

    /// <summary>The terminal's size changed.</summary>
    public void Resized(int columns, int rows) => Enqueue(RecordingEventType.Resize, RecordingFile.Pair(columns, rows));
}

/// <summary>
/// Records a remote desktop from the session's frame (design §2.2): damage is gathered as it's reported and turned into
/// a frame at most <c>framesPerSecond</c> times a second, on a thread of its own.
/// </summary>
public sealed class ScreenRecorder : SessionRecorder
{
    static readonly TimeSpan KeyFrameEvery = TimeSpan.FromSeconds(30);

    /// <summary>How often sound is taken from the session; it's stamped with the time it's taken.</summary>
    static readonly TimeSpan SoundEvery = TimeSpan.FromMilliseconds(40);

    readonly DamageList _damage = new();
    readonly object _damageLock = new();
    readonly CancellationTokenSource _stop = new();
    readonly PeriodicTimer _frameTimer;
    int _framesPerSecond;
    IFrameBufferSession? _screen;
    bool _needsKeyFrame = true;
    TimeSpan _lastKeyFrame;
    byte[] _rgb = [];
    byte[]? _scratch;
    byte[] _xor = [];
    byte[] _rowRgb = [];
    int _width, _height;
    (int X, int Y)? _lastPointer;
    bool _recordSound;
    readonly object _soundLock = new();
    IAudioTap? _tap;
    IAudioSession? _audioSession;

    public ScreenRecorder(RecordingWriter writer, RecordingInfo info, int framesPerSecond, long maxBytes, Func<TimeSpan>? clock = null, bool recordSound = false)
        : base(writer, info, maxBytes, clock)
    {
        _framesPerSecond = Math.Clamp(framesPerSecond, 1, 30);
        _frameTimer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / _framesPerSecond));
        _recordSound = recordSound;
        Stopped += (_, _) =>
        {
            _stop.Cancel();
            SetTap(null);
        };
        _ = Task.Run(LoopAsync);

        // Sound can be turned on partway through, so the loop runs either way; without a tap it does nothing.
        _ = Task.Run(SoundLoopAsync);
    }

    /// <summary>How many frames a second are taken, 1 to 30; changing it takes effect at the next frame.</summary>
    public int FramesPerSecond
    {
        get => _framesPerSecond;
        set
        {
            _framesPerSecond = Math.Clamp(value, 1, 30);
            try
            {
                _frameTimer.Period = TimeSpan.FromSeconds(1.0 / _framesPerSecond);
            }
            catch (ObjectDisposedException)
            {
                // Stopped: there are no more frames to time.
            }
        }
    }

    /// <summary>Whether the session it's recording can give it sound (a remote desktop playing sound here).</summary>
    public bool CanRecordSound
    {
        get
        {
            lock (_soundLock)
            {
                return _audioSession?.AudioTap is not null;
            }
        }
    }

    /// <summary>Whether sound is recorded; it can be turned on or off while recording, and is marked on the timeline.</summary>
    public bool RecordsSound
    {
        get
        {
            lock (_soundLock)
            {
                return _recordSound;
            }
        }
        set
        {
            lock (_soundLock)
            {
                if (_recordSound == value)
                {
                    return;
                }

                _recordSound = value;
                SetTap(value ? _audioSession?.AudioTap : null);
            }

            if (IsRecording)
            {
                Marker(value ? "Sound on" : "Sound off");
            }
        }
    }

    /// <summary>Sound left out because the disk was behind.</summary>
    public int DroppedSound { get; private set; }

    /// <summary>Where the pointer is on the remote desktop, read with each frame; the view supplies it.</summary>
    public Func<(int X, int Y)?>? Pointer { get; set; }

    /// <summary>Frames left out because the disk was behind.</summary>
    public int DroppedFrames { get; private set; }

    protected override void OnAttach(IConnectionSession session)
    {
        if (session is IFrameBufferSession screen)
        {
            lock (_damageLock)
            {
                _screen = screen;
                _needsKeyFrame = true;
            }

            screen.FrameUpdated += OnFrame;
        }

        lock (_soundLock)
        {
            _audioSession = session as IAudioSession;
            if (_recordSound && _audioSession?.AudioTap is { } tap)
            {
                SetTap(tap);
            }
        }
    }

    void SetTap(IAudioTap? tap)
    {
        lock (_soundLock)
        {
            _tap?.SetEnabled(false);
            _tap = tap;
            _tap?.SetEnabled(true);
        }
    }

    protected override void OnDetach(IConnectionSession session)
    {
        if (session is IFrameBufferSession screen)
        {
            screen.FrameUpdated -= OnFrame;
        }

        lock (_damageLock)
        {
            _screen = null;
            _damage.Clear();
        }

        lock (_soundLock)
        {
            _audioSession = null;
            SetTap(null);
        }
    }

    async Task SoundLoopAsync()
    {
        using var timer = new PeriodicTimer(SoundEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                CaptureSound();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Records the sound the session has played since the last call. Called by the loop, and by tests.</summary>
    internal void CaptureSound()
    {
        lock (_soundLock)
        {
            while (_tap?.TryTake() is { } chunk)
            {
                // Silence takes room and plays the same as nothing.
                if (chunk.Pcm.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                {
                    continue;
                }

                if (Backlog > MaxBacklog)
                {
                    DroppedSound++;
                    continue;
                }

                Enqueue(RecordingEventType.Audio, RecordingFile.AudioPayload(chunk.SampleRate, chunk.Channels, chunk.Pcm));
            }
        }
    }

    void OnFrame(object? sender, FrameUpdatedEventArgs e)
    {
        lock (_damageLock)
        {
            if (e.Resized)
            {
                _needsKeyFrame = true;
            }

            _damage.Add(e.Damage);
        }
    }

    async Task LoopAsync()
    {
        using var timer = _frameTimer;
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                CaptureFrame();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            Logger.LogWarning("Screen recording stopped: {Message}", e.Message);
            StopWith("Recording stopped because a frame couldn't be recorded.");
        }
    }

    /// <summary>Takes one frame now, if anything changed. Called by the loop, and by tests.</summary>
    internal void CaptureFrame()
    {
        IFrameBufferSession? screen;
        FrameRect[] rects;
        bool keyFrame;
        lock (_damageLock)
        {
            screen = _screen;
            if (screen is null)
            {
                return;
            }

            keyFrame = _needsKeyFrame || Now - _lastKeyFrame >= KeyFrameEvery && !_damage.IsEmpty;
            rects = _damage.Rectangles.ToArray();
            _damage.Clear();
            _needsKeyFrame = false;
        }

        int width = screen.Width, height = screen.Height;
        if (width is < 1 or > ScreenFrames.MaxSide || height is < 1 or > ScreenFrames.MaxSide)
        {
            return;
        }

        var pointer = Pointer?.Invoke();
        bool pointerMoved = pointer is not null && pointer != _lastPointer;
        if (!keyFrame && rects.Length == 0 && !pointerMoved)
        {
            return;
        }

        if (Backlog > MaxBacklog)
        {
            // Behind: this frame's damage waits for the next key frame instead.
            DroppedFrames++;
            lock (_damageLock)
            {
                _needsKeyFrame = true;
            }

            return;
        }

        if (width != _width || height != _height)
        {
            _width = width;
            _height = height;
            _rgb = new byte[width * height * 3];
            keyFrame = true;
            Enqueue(RecordingEventType.Resize, RecordingFile.Pair(width, height));
        }

        if (keyFrame)
        {
            Copy(screen, new FrameRect(0, 0, width, height), xorInto: null, 0);
            Enqueue(RecordingEventType.KeyFrame, ScreenFrames.EncodeKeyFrame(_rgb, width, height));
            _lastKeyFrame = Now;
        }
        else if (rects.Length > 0)
        {
            var clipped = new List<FrameRect>(rects.Length);
            long total = 0;
            foreach (var r in rects)
            {
                int x = Math.Clamp(r.X, 0, width), y = Math.Clamp(r.Y, 0, height);
                var c = new FrameRect(x, y, Math.Clamp(r.Right, x, width) - x, Math.Clamp(r.Bottom, y, height) - y);
                if (!c.IsEmpty)
                {
                    clipped.Add(c);
                    total += (long)c.Width * c.Height * 3;
                }
            }

            if (clipped.Count > 0)
            {
                if (_xor.Length < total)
                {
                    _xor = new byte[total];
                }

                int offset = 0;
                foreach (var c in clipped)
                {
                    offset = Copy(screen, c, _xor, offset);
                }

                Enqueue(RecordingEventType.Change, ScreenFrames.EncodeChange(clipped, _xor.AsSpan(0, offset)));
            }
        }

        if (pointerMoved)
        {
            _lastPointer = pointer;
            Enqueue(RecordingEventType.Pointer, RecordingFile.Pair(pointer!.Value.X, pointer.Value.Y));
        }
    }

    // Copies a rectangle into the recorded frame; with xorInto, also writes how it differs from before there.
    int Copy(IFrameBufferSession screen, FrameRect rect, byte[]? xorInto, int offset)
    {
        var copied = FrameCopy.CopyOut(screen, rect, _width, _height, ref _scratch);
        int rowBytes = copied.Width * 3;
        if (_rowRgb.Length < rowBytes)
        {
            _rowRgb = new byte[rowBytes];
        }

        for (int row = 0; row < copied.Height; row++)
        {
            var source = _scratch.AsSpan(row * copied.Width * FrameCopy.BytesPerPixel, copied.Width * FrameCopy.BytesPerPixel);
            var target = _rgb.AsSpan(((copied.Y + row) * _width + copied.X) * 3, rowBytes);
            if (xorInto is null)
            {
                ScreenFrames.RgbaToRgb(source, target);
                continue;
            }

            var fresh = _rowRgb.AsSpan(0, rowBytes);
            ScreenFrames.RgbaToRgb(source, fresh);
            var xor = xorInto.AsSpan(offset, rowBytes);
            for (int i = 0; i < rowBytes; i++)
            {
                xor[i] = (byte)(fresh[i] ^ target[i]);
            }

            fresh.CopyTo(target);
            offset += rowBytes;
        }

        // A rectangle the session clipped further (it shrank meanwhile) still takes its full room, as zeros: no change.
        int expected = rect.Width * rect.Height * 3;
        int written = rowBytes * copied.Height;
        if (xorInto is not null && written < expected)
        {
            xorInto.AsSpan(offset, expected - written).Clear();
            offset += expected - written;
        }

        return offset;
    }
}
