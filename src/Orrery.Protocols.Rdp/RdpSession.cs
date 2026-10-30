using System.Diagnostics;
using System.Runtime.InteropServices;
using Devolutions.IronRdp;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using RdpPixelFormat = Devolutions.IronRdp.PixelFormat;
using RdpPointerButton = Devolutions.IronRdp.MouseButtonType;

namespace Orrery.Protocols.Rdp;

/// <summary>
/// An RDP session backed by IronRDP. Pixels are decoded into IronRDP's image, mirrored into a managed
/// RGBA buffer on each graphics update, and handed to the UI through <see cref="CopyRegion"/>.
/// </summary>
public sealed class RdpSession : IFrameBufferSession
{
    const int BytesPerPixel = 4;
    const uint ClipboardWaitMs = 1000;
    static readonly TimeSpan ClipboardTimeoutsInterval = TimeSpan.FromSeconds(5);

    readonly object _stageLock = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly InputDatabase _inputDatabase = InputDatabase.New();
    readonly CancellationTokenSource _cts = new();

    Framed<Stream>? _framed;
    ActiveStage? _activeStage;
    DecodedImage? _image;
    byte[] _frame = [];
    bool _pointerSoftwareRendering;
    Task? _readLoop;
    Task? _clipboardLoop;
    WinCliprdr? _clipboard;

    readonly ServerCertificateValidator _certificateValidator;

    RdpSession(ConnectionTarget target, ServerCertificateValidator certificateValidator)
    {
        Target = target;
        _certificateValidator = certificateValidator;
    }

    public ConnectionTarget Target { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;

    /// <summary>
    /// Starts connecting in the background and returns at once; watch <see cref="StateChanged"/>.
    /// Call on the UI thread: on Windows the shared clipboard is created here, and pasting files from the
    /// remote machine needs that thread's single-threaded apartment.
    /// </summary>
    public static RdpSession Start(ConnectionTarget target, string password, int desktopWidth, int desktopHeight,
        ServerCertificateValidator certificateValidator, bool shareClipboard = true)
    {
        var session = new RdpSession(target, certificateValidator);
        if (shareClipboard && OperatingSystem.IsWindows())
        {
            try
            {
                session._clipboard = WinCliprdr.NewWithFileTransfer(true);
            }
            catch (Exception e)
            {
                // A session without a shared clipboard is still useful.
                Trace.TraceError($"Couldn't set up the RDP clipboard: {e.Message}");
            }
        }

        session._readLoop = Task.Run(() => session.RunAsync(password, desktopWidth, desktopHeight));
        return session;
    }

    async Task RunAsync(string password, int desktopWidth, int desktopHeight)
    {
        var token = _cts.Token;
        try
        {
            var config = BuildConfig(Target, password, desktopWidth, desktopHeight);
            var (result, framed) = await RdpConnector.ConnectAsync(config, Target, _certificateValidator, _clipboard?.BackendFactory(), token);
            _framed = new Framed<Stream>(framed.GetInner().Item1);

            lock (_stageLock)
            {
                // ActiveStage.New consumes the result, so read everything we need from it first.
                var desktopSize = result.DesktopSize;
                CheckDesktopSize(desktopSize.Width, desktopSize.Height);
                ResetImage(desktopSize.Width, desktopSize.Height);
                _pointerSoftwareRendering = result.PointerSoftwareRendering;
                _activeStage = ActiveStage.New(result);
            }

            SetState(SessionState.Connected);
            FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));
            if (_clipboard is not null)
            {
                _clipboardLoop = Task.Run(() => ClipboardLoopAsync(_clipboard, token));
            }

            await ReadLoopAsync(token);
            SetState(SessionState.Disconnected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetState(SessionState.Disconnected);
        }
        catch (CertificateRejectedException e)
        {
            SetState(SessionState.Failed, e.Message);
        }
        catch (Exception e)
        {
            Trace.TraceError($"RDP session to {Target.Host} failed: {e}");
            SetState(SessionState.Failed, e.Message);
        }
    }

    async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var (action, payload) = await _framed!.ReadPdu(token);
            var outputs = WithStageLock(() => _activeStage!.Process(_image!, action, payload));
            if (!await HandleOutputsAsync(outputs, token))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Forwards local clipboard changes and paste requests to the server. The wait wakes at least every
    /// second, and timeouts are driven every few seconds so stale clipboard locks and file transfers expire.
    /// </summary>
    async Task ClipboardLoopAsync(WinCliprdr clipboard, CancellationToken token)
    {
        var sinceTimeouts = Stopwatch.StartNew();
        while (!token.IsCancellationRequested && State == SessionState.Connected)
        {
            try
            {
                // Not disposed: SubmitClipboardMessage hands it to IronRDP, as in IronRDP's own example.
                var message = clipboard.NextClipboardMessageTimeout(ClipboardWaitMs);
                if (message is not null)
                {
                    await SubmitClipboardAsync(() => _activeStage!.SubmitClipboardMessage(message), token);
                }

                if (sinceTimeouts.Elapsed >= ClipboardTimeoutsInterval)
                {
                    sinceTimeouts.Restart();
                    await SubmitClipboardAsync(() => _activeStage!.DriveClipboardTimeouts(), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (IronRdpException e)
            {
                // Only this clipboard operation failed; the session goes on.
                Trace.TraceWarning($"RDP clipboard operation failed: {e.Message}");
            }
            catch (Exception e)
            {
                // A write to a dropped connection, say. The read loop reports the disconnect.
                Trace.TraceWarning($"RDP clipboard loop ended: {e.Message}");
                return;
            }
        }
    }

    async Task SubmitClipboardAsync(Func<VecU8> submit, CancellationToken token)
    {
        using var frame = WithStageLock(submit);
        if (frame.Size > 0)
        {
            await WriteAsync(ToBytes(frame), token);
        }
    }

    /// <returns>False when the server ended the session.</returns>
    async Task<bool> HandleOutputsAsync(ActiveStageOutputIterator outputs, CancellationToken token)
    {
        using (outputs)
        {
            while (!outputs.IsEmpty())
            {
                using var output = outputs.Next()!;
                switch (output.EnumType)
                {
                    case ActiveStageOutputType.Terminate:
                        return false;

                    case ActiveStageOutputType.ResponseFrame:
                        using (var frame = output.ResponseFrame)
                        {
                            await WriteAsync(ToBytes(frame), token);
                        }
                        break;

                    case ActiveStageOutputType.GraphicsUpdate:
                        OnGraphicsUpdate(output.GraphicsUpdate);
                        break;

                    case ActiveStageOutputType.DeactivateAll:
                        await ReactivateAsync(token);
                        break;

                    default:
                        Trace.TraceInformation($"Unhandled RDP output: {output.EnumType}");
                        break;
                }
            }
        }

        return true;
    }

    void OnGraphicsUpdate(InclusiveRectangle damage)
    {
        FrameRect rect;
        lock (_stageLock)
        {
            _image!.CopyRectInto(damage, _frame, (nuint)(Width * BytesPerPixel));
            rect = new FrameRect(damage.Left, damage.Top, damage.Right - damage.Left + 1, damage.Bottom - damage.Top + 1);
        }

        FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(rect, resized: false));
    }

    /// <summary>The server restarted the activation sequence, usually because the desktop was resized.</summary>
    async Task ReactivateAsync(CancellationToken token)
    {
        var activation = WithStageLock(() => _activeStage!.CreateConnectionActivation());
        var writeBuf = WriteBuf.New();

        while (activation.State.Type != ConnectionActivationStateType.Finalized)
        {
            await Connection.SingleSequenceStep(activation, writeBuf, _framed!, token);
        }

        var finalized = activation.State.Finalized;
        lock (_stageLock)
        {
            CheckDesktopSize(finalized.DesktopSize.Width, finalized.DesktopSize.Height);
            ResetImage(finalized.DesktopSize.Width, finalized.DesktopSize.Height);
            _activeStage!.Reactivate(
                activation.IoChannelId,
                activation.UserChannelId,
                finalized.ShareId,
                finalized.EnableServerPointer,
                finalized.PointerSoftwareRendering,
                finalized.StaticChannelChunkSize,
                finalized.WindowSupportLevel);
            _activeStage.EnableServerPointer = finalized.EnableServerPointer;
            _pointerSoftwareRendering = finalized.PointerSoftwareRendering;
        }

        FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));
    }

    /// <summary>
    /// The server picks the desktop size. One we won't buffer ends the session with a plain message: the
    /// exception reaches <see cref="RunAsync"/>, which fails the session with it.
    /// </summary>
    static void CheckDesktopSize(ushort width, ushort height)
    {
        if (!DesktopLimits.IsSupported(width, height))
        {
            throw new NotSupportedException(DesktopLimits.RefusalMessage(width, height));
        }
    }

    // Caller holds _stageLock. Width and Height change only once both buffers exist, so a failed
    // allocation leaves the previous frame whole.
    void ResetImage(ushort width, ushort height)
    {
        var frame = new byte[checked((long)width * height * BytesPerPixel)];
        var image = DecodedImage.New(RdpPixelFormat.RgbA32, width, height);
        try
        {
            image.FillData(frame);
        }
        catch
        {
            image.Dispose();
            throw;
        }

        var previous = _image;
        (_image, _frame) = (image, frame);
        previous?.Dispose();
        Width = width;
        Height = height;
    }

    public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride)
    {
        lock (_stageLock)
        {
            int x = Math.Clamp(region.X, 0, Width), y = Math.Clamp(region.Y, 0, Height);
            int right = Math.Clamp(region.Right, x, Width), bottom = Math.Clamp(region.Bottom, y, Height);
            int rowBytes = (right - x) * BytesPerPixel;
            int stride = Width * BytesPerPixel;

            for (int row = y; row < bottom; row++)
            {
                // The frame is at most DesktopLimits.MaxSide squared, so its offsets fit in an int.
                Marshal.Copy(_frame, row * stride + x * BytesPerPixel,
                    destination + (nint)row * destinationStride + (nint)x * BytesPerPixel, rowBytes);
            }
        }
    }

    // Shift, Ctrl, Alt, Windows, and the lock keys.
    static readonly HashSet<ushort> NonRepeatingKeys =
        [0x2A, 0x36, 0x1D, 0xE01D, 0x38, 0xE038, 0xE05B, 0xE05C, 0x3A, 0x45, 0x46];

    readonly HashSet<ushort> _heldKeys = [];

    public void SendKey(ushort scancode, bool pressed)
    {
        // Holding a key makes the OS repeat key-down events, and IronRDP turns each repeat of a held key
        // into release + press. For modifiers the server then sees repeated taps (five Shift taps start
        // Sticky Keys), and repeated lock keys toggle, so forward only their first press.
        lock (_heldKeys)
        {
            if (pressed && !_heldKeys.Add(scancode) && NonRepeatingKeys.Contains(scancode))
            {
                return;
            }

            if (!pressed)
            {
                _heldKeys.Remove(scancode);
            }
        }

        var key = Scancode.FromU16(scancode);
        SendInput(pressed ? key.AsOperationKeyPressed() : key.AsOperationKeyReleased());
    }

    public void ReleaseAllKeys()
    {
        ushort[] held;
        lock (_heldKeys)
        {
            held = [.. _heldKeys];
        }

        foreach (var scancode in held)
        {
            SendKey(scancode, pressed: false);
        }
    }

    public void SendPointerMove(int x, int y)
    {
        var position = MousePosition.New((ushort)Math.Clamp(x, 0, ushort.MaxValue), (ushort)Math.Clamp(y, 0, ushort.MaxValue));
        SendInput(position.AsMoveOperation());
    }

    public void SendPointerButton(PointerButton button, bool pressed)
    {
        var rdpButton = MouseButton.New(button switch
        {
            PointerButton.Left => RdpPointerButton.Left,
            PointerButton.Right => RdpPointerButton.Right,
            PointerButton.Middle => RdpPointerButton.Middle,
            PointerButton.X1 => RdpPointerButton.X1,
            _ => RdpPointerButton.X2,
        });
        SendInput(pressed ? rdpButton.AsOperationMouseButtonPressed() : rdpButton.AsOperationMouseButtonReleased());
    }

    public void SendWheel(int units, bool horizontal)
    {
        if (units == 0)
        {
            return;
        }

        var rotation = WheelRotations.New(!horizontal, (short)Math.Clamp(units, short.MinValue, short.MaxValue));
        SendInput(rotation.AsOperation());
    }

    public void RequestResize(int width, int height)
    {
        if (State != SessionState.Connected)
        {
            return;
        }

        // MS-RDPEDISP: 200 to 8192 pixels, and the width must be even.
        width = Math.Clamp(width, 200, DesktopLimits.MaxSide) & ~1;
        height = Math.Clamp(height, 200, DesktopLimits.MaxSide);
        if (width == Width && height == Height)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Null when the server hasn't opened the display-control channel, i.e. it can't resize.
                var outputs = WithStageLock(() => _activeStage!.EncodedResize((uint)width, (uint)height));
                if (outputs is not null)
                {
                    await HandleOutputsAsync(outputs, _cts.Token);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Trace.TraceError($"RDP resize to {width}x{height} failed: {e.Message}");
            }
        });
    }

    void SendInput(Operation operation)
    {
        if (State != SessionState.Connected)
        {
            return;
        }

        FastPathInputEventIterator fastPath;
        lock (_stageLock)
        {
            fastPath = _inputDatabase.Apply(operation);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (_pointerSoftwareRendering)
                {
                    // The server expects us to draw the pointer, which touches the image.
                    var outputs = WithStageLock(() => _activeStage!.ProcessFastpathInput(_image!, fastPath));
                    await HandleOutputsAsync(outputs, _cts.Token);
                    return;
                }

                using var frame = fastPath.Encode();
                await WriteAsync(ToBytes(frame), _cts.Token);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Trace.TraceError($"Sending RDP input failed: {e.Message}");
            }
        });
    }

    public async Task DisconnectAsync()
    {
        if (State == SessionState.Connected)
        {
            try
            {
                var outputs = WithStageLock(() => _activeStage!.GracefulShutdown());
                await HandleOutputsAsync(outputs, _cts.Token);
            }
            catch (Exception e)
            {
                Trace.TraceError($"Graceful RDP shutdown failed: {e.Message}");
            }
        }

        _cts.Cancel();
        // Each loop has already reported its own outcome, so a fault here is only traced.
        await AwaitLoopAsync(_readLoop);
        await AwaitLoopAsync(_clipboardLoop);
    }

    static async Task AwaitLoopAsync(Task? loop)
    {
        if (loop is null)
        {
            return;
        }

        try
        {
            await loop;
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"RDP session loop had failed: {e.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        lock (_stageLock)
        {
            _image?.Dispose();
            _image = null;
        }

        // The loop has ended, so nothing is waiting on the clipboard any more.
        _clipboard?.Dispose();
        _clipboard = null;

        _cts.Dispose();
        _writeLock.Dispose();
    }

    async Task WriteAsync(byte[] bytes, CancellationToken token)
    {
        await _writeLock.WaitAsync(token);
        try
        {
            await _framed!.Write(bytes);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    void SetState(SessionState state, string? message = null)
    {
        State = state;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
    }

    T WithStageLock<T>(Func<T> call)
    {
        lock (_stageLock)
        {
            return call();
        }
    }

    static byte[] ToBytes(VecU8 vec)
    {
        var bytes = new byte[vec.Size];
        vec.Fill(bytes);
        return bytes;
    }

    static Config BuildConfig(ConnectionTarget target, string password, int width, int height)
    {
        var builder = ConfigBuilder.New();
        builder.WithUsernameAndPassword(target.Username ?? string.Empty, password);
        if (!string.IsNullOrEmpty(target.Domain))
        {
            builder.Domain = target.Domain;
        }

        // IronRDP takes height first.
        builder.SetDesktopSize((ushort)height, (ushort)width);
        builder.ClientName = "Orrery";
        builder.ClientDir = "C:\\";
        builder.PerformanceFlags = PerformanceFlags.NewDefault();

        // NLA is required: credentials only ever go through CredSSP, inside a certificate-checked TLS session.
        // These are IronRDP's defaults today; they're set here so a changed default can't weaken the sign-in.
        builder.EnableCredssp = true;
        builder.EnableTls = false;
        return builder.Build();
    }
}
