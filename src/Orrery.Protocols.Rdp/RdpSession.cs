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
    /// </summary>
    public static RdpSession Start(ConnectionTarget target, string password, int desktopWidth, int desktopHeight,
        ServerCertificateValidator certificateValidator)
    {
        var session = new RdpSession(target, certificateValidator);
        session._readLoop = Task.Run(() => session.RunAsync(password, desktopWidth, desktopHeight));
        return session;
    }

    async Task RunAsync(string password, int desktopWidth, int desktopHeight)
    {
        var token = _cts.Token;
        try
        {
            var config = BuildConfig(Target, password, desktopWidth, desktopHeight);
            var (result, framed) = await RdpConnector.ConnectAsync(config, Target, _certificateValidator, token);
            _framed = new Framed<Stream>(framed.GetInner().Item1);

            lock (_stageLock)
            {
                // ActiveStage.New consumes the result, so read everything we need from it first.
                var desktopSize = result.DesktopSize;
                ResetImage(desktopSize.Width, desktopSize.Height);
                _pointerSoftwareRendering = result.PointerSoftwareRendering;
                _activeStage = ActiveStage.New(result);
            }

            SetState(SessionState.Connected);
            FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));

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

    // Caller holds _stageLock.
    void ResetImage(ushort width, ushort height)
    {
        var previous = _image;
        _image = DecodedImage.New(RdpPixelFormat.RgbA32, width, height);
        previous?.Dispose();
        Width = width;
        Height = height;
        _frame = new byte[width * height * BytesPerPixel];
        _image.FillData(_frame);
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
                Marshal.Copy(_frame, row * stride + x * BytesPerPixel,
                    destination + row * destinationStride + x * BytesPerPixel, rowBytes);
            }
        }
    }

    public void SendKey(ushort scancode, bool pressed)
    {
        var key = Scancode.FromU16(scancode);
        SendInput(pressed ? key.AsOperationKeyPressed() : key.AsOperationKeyReleased());
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
        if (_readLoop is not null)
        {
            await _readLoop;
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
        return builder.Build();
    }
}
