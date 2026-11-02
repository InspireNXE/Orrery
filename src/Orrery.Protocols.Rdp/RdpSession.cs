using System.Diagnostics;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Devolutions.IronRdp;
using Microsoft.Extensions.Logging;
using Orrery.Core.Security;
using Orrery.Core.Sessions;
using RdpPixelFormat = Devolutions.IronRdp.PixelFormat;
using RdpPointerButton = Devolutions.IronRdp.MouseButtonType;
using RdpPenPhase = Devolutions.IronRdp.PenPhase;
using RdpTouchPhase = Devolutions.IronRdp.TouchPhase;

namespace Orrery.Protocols.Rdp;

/// <summary>
/// An RDP session backed by IronRDP. Pixels are decoded into IronRDP's image, mirrored into a managed
/// RGBA buffer on each graphics update, and handed to the UI through <see cref="CopyRegion"/>.
/// </summary>
/// <remarks>
/// The shared clipboard works two ways. On Windows, IronRDP's own Windows clipboard talks to the local clipboard
/// directly and also copies images and files. Elsewhere, clipboard text goes through <see cref="IClipboardSession"/>,
/// so the view exchanges it with the local clipboard as it does for VNC. Setting <c>ORRERY_RDP_TEXT_CLIPBOARD=1</c>
/// uses the text-only way on Windows too, for testing it.
/// <para>
/// A server that redirects the connection (an RD Connection Broker sending it to a session host) is followed, up to
/// <see cref="RdpRedirection.MaxHops"/> times, carrying back what the broker sent; see <see cref="RdpRedirection"/>.
/// </para>
/// <para>
/// Sound, shared drives and the console session (<see cref="RdpOptions"/>) need IronRDP's native library to have them,
/// which Orrery's own build does on Windows. Where it doesn't, each is logged and left off, and the session goes on.
/// </para>
/// <para>
/// The desktop comes through the graphics pipeline (RDPGFX) when the server takes it, as it does from Windows' own
/// client, and else as bitmaps. Either way IronRDP draws it into the same image. A resize through the pipeline changes
/// that image's size in place (<see cref="FollowGraphicsReset"/>); one without it restarts the session's activation.
/// </para>
/// </remarks>
public sealed class RdpSession : IFrameBufferSession, IAudioSession, IClipboardSession, ITouchInput, IMultiMonitorSession, IDisplayScaleSession
{
    const int BytesPerPixel = 4;
    const uint ClipboardWaitMs = 1000;
    const uint MaxQueuedClipboardEvents = 64;
    const int MaxRemoteFormats = 256;
    static readonly TimeSpan ClipboardTimeoutsInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often shared drives' file operations that finish later (on Windows) are passed to the server.</summary>
    static readonly TimeSpan DrivesPollInterval = TimeSpan.FromMilliseconds(100);

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
    Task? _drivesLoop;
    WinCliprdr? _clipboard;
    CallbackCliprdr? _textClipboard;
    RdpClipboardBridge? _bridge;

    // Touch and pen frames for the input channel (MS-RDPEI): built under _stageLock, written in order by one loop.
    TouchFrameBuilder? _touches;
    PenFrameBuilder? _pens;
    readonly Channel<byte[]> _inputFrames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    Task? _inputFrameLoop;

    static readonly ILogger Logger = Orrery.Core.Diagnostics.Log.For("Rdp");

    readonly ServerCertificateValidator _certificateValidator;
    readonly ISessionRoute? _route;
    readonly RdpOptions _options;
    readonly RdpAudioTap? _audioTap;

    // The display's scaling in percent, when the server is told it, and the one it was last told.
    readonly bool _followsDisplayScale;
    volatile int _desktopScale;
    volatile int _sentScale;

    RdpSession(ConnectionTarget target, ServerCertificateValidator certificateValidator, ISessionRoute? route, RdpOptions options, int? desktopScale)
    {
        Target = target;
        _certificateValidator = certificateValidator;
        _route = route;
        _options = options;
        _audioTap = options.PlayAudio ? new RdpAudioTap() : null;
        _followsDisplayScale = desktopScale is not null;
        _desktopScale = _sentScale = desktopScale ?? 100;
    }

    public ConnectionTarget Target { get; }

    public IAudioTap? AudioTap => _audioTap;
    public SessionState State { get; private set; } = SessionState.Connecting;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public SessionStateChangedEventArgs? LastChange { get; private set; }
    public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
    public event EventHandler<string>? RemoteClipboardChanged;

    /// <inheritdoc/>
    public event EventHandler<string>? ClipboardRefused;

    /// <summary>Whether clipboard text goes through <see cref="SendClipboard"/> and <see cref="RemoteClipboardChanged"/>.</summary>
    public bool ExchangesClipboard => _textClipboard is not null;

    /// <summary>
    /// Starts connecting in the background and returns at once; watch <see cref="StateChanged"/>.
    /// Call on the UI thread: on Windows the shared clipboard is created here, and pasting files from the
    /// remote machine needs that thread's single-threaded apartment.
    /// </summary>
    /// <param name="route">How to reach the server, such as an SSH tunnel; directly when null. Disposed with the session.</param>
    /// <param name="quality">Which of the remote desktop's visual effects to ask for.</param>
    /// <param name="options">Sound, shared drives and the console session; IronRDP's defaults and sound here when null.</param>
    /// <param name="displayScale">This display's scaling (1.5 for 150%) to tell the server, with the desktop size in
    /// device pixels, so it draws at full resolution and sizes its UI to match; null leaves the server at its own.</param>
    public static RdpSession Start(ConnectionTarget target, string password, int desktopWidth, int desktopHeight,
        ServerCertificateValidator certificateValidator, bool shareClipboard = true, ISessionRoute? route = null,
        DisplayQuality quality = DisplayQuality.Balanced, RdpOptions? options = null, double? displayScale = null)
    {
        var session = new RdpSession(target, certificateValidator, route, options ?? new RdpOptions(),
            displayScale is { } scale ? ScalePercent(scale) : null);
        if (shareClipboard)
        {
            try
            {
                if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("ORRERY_RDP_TEXT_CLIPBOARD") != "1")
                {
                    session._clipboard = WinCliprdr.NewWithFileTransfer(true);
                }
                else
                {
                    session._textClipboard = CallbackCliprdr.WithLimits(
                        MaxQueuedClipboardEvents,
                        (uint)RdpClipboardBridge.MaxDataBytes,
                        (uint)(RdpClipboardBridge.MaxDataBytes * RdpClipboardBridge.MaxPendingRequests));
                }
            }
            catch (EntryPointNotFoundException)
            {
                // A native library built before IronRDP had the callback clipboard.
                Logger.LogWarning("RDP clipboard is off: this build's IronRDP library has no clipboard for {Os}", RuntimeInformation.OSDescription);
            }
            catch (Exception e)
            {
                // A session without a shared clipboard is still useful.
                Logger.LogError("Couldn't set up the RDP clipboard: {Message}", e.Message);
            }
        }

        session._readLoop = Task.Run(() => session.RunAsync(password, desktopWidth, desktopHeight, quality));
        return session;
    }

    async Task RunAsync(string password, int desktopWidth, int desktopHeight, DisplayQuality quality)
    {
        var token = _cts.Token;
        try
        {
            var (result, framed, drivesShared) = await ConnectFollowingRedirectionsAsync(password, desktopWidth, desktopHeight, quality, token);
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

            Logger.LogInformation("Connected to {Host}: desktop {Width}x{Height} (asked for {AskedWidth}x{AskedHeight}), scaling {Scale}, {Quality} quality, "
                + "clipboard {Clipboard}, sound {Sound}, {Drives} shared drive(s), {Session} session",
                Target.Host, Width, Height, desktopWidth, desktopHeight, _followsDisplayScale ? $"{_sentScale}%" : "the server's", quality.ToString(),
                _clipboard is not null ? "on" : _textClipboard is not null ? "text" : "off",
                _options.PlayAudio ? "here" : "off", drivesShared ? _options.Drives.Count : 0, _options.AdminSession ? "console" : "own");

            // Before the state changes: the view sends the local clipboard as soon as the session is connected.
            if (_textClipboard is not null)
            {
                _bridge = new RdpClipboardBridge(new CallbackChannel(this, _textClipboard), () => State == SessionState.Connected);
                _bridge.RemoteTextChanged += (_, text) => RemoteClipboardChanged?.Invoke(this, text);
                _bridge.TextRefused += (_, reason) => ClipboardRefused?.Invoke(this, reason);
            }

            SetState(SessionState.Connected);
            FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));
            _inputFrameLoop = Task.Run(() => InputFrameLoopAsync(token));
            if (_clipboard is not null)
            {
                _clipboardLoop = Task.Run(() => ClipboardLoopAsync(_clipboard, token));
            }
            else if (_bridge is not null)
            {
                _clipboardLoop = Task.Run(() => BridgeLoopAsync(_bridge, token));
            }

            if (drivesShared)
            {
                _drivesLoop = Task.Run(() => DrivesLoopAsync(token));
            }

            await ReadLoopAsync(token);
            SetState(SessionState.Disconnected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetState(SessionState.Disconnected);
        }
        catch (Exception e) when (e is CertificateRejectedException or RouteException)
        {
            Logger.LogWarning("RDP session to {Host} didn't start: {Message}", Target.Host, e.Message);
            SetState(SessionState.Failed, e.Message, e);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "RDP session to {Host} failed", Target.Host);
            SetState(SessionState.Failed, e.Message, Recognize(e));
        }
    }

    /// <summary>
    /// Connects, following the server when it redirects the connection to another session host, as an RD Connection
    /// Broker does, at most <see cref="RdpRedirection.MaxHops"/> times. Each host's certificate is checked under its own
    /// name, and the login goes to each through CredSSP again.
    /// </summary>
    async Task<(ConnectionResult Result, Framed<SslStream> Framed, bool DrivesShared)> ConnectFollowingRedirectionsAsync(string password,
        int width, int height, DisplayQuality quality, CancellationToken token)
    {
        var target = Target;
        RdpRedirection? redirectedFrom = null;
        for (int hop = 0; ; hop++)
        {
            var config = BuildConfig(target, password, width, height, quality, _options.PlayAudio, _followsDisplayScale ? _sentScale : null);
            var dial = await _route.DialAsync(target, token);
            var clipboardFactory = _clipboard?.BackendFactory() ?? _textClipboard?.BackendFactory();
            try
            {
                return await RdpConnector.ConnectAsync(config, target, dial, _certificateValidator, clipboardFactory, _options, redirectedFrom, token, _audioTap);
            }
            catch (ServerRedirectedException e)
            {
                var redirection = e.Redirection;
                Logger.LogInformation("{Host} redirected the connection to {Next} (session {Session}{Token}{Cookie})", target.Host,
                    redirection.StayOnServer ? "itself, to call back with its routing token" : redirection.NextHost ?? "nowhere", redirection.SessionId,
                    redirection.RoutingToken.Length > 0 ? ", with a routing token" : string.Empty,
                    redirection.Cookie.Length > 0 ? ", with a password cookie" : string.Empty);
                Logger.LogDebug("{Host} routing token: {Token}", target.Host, redirection.RoutingTokenText);
                target = NextHop(redirection, target, hop);
                redirectedFrom = redirection;
            }
        }
    }

    /// <summary>Where a redirection sends the connection, or why it can't be followed.</summary>
    /// <param name="hop">How many redirections came before this one.</param>
    internal static ConnectionTarget NextHop(RdpRedirection redirection, ConnectionTarget current, int hop)
    {
        if (redirection.Next(current) is not { } next)
        {
            throw new NotSupportedException("The server redirected the connection without saying where to.");
        }

        if (hop + 1 >= RdpRedirection.MaxHops)
        {
            throw new NotSupportedException($"The server redirected the connection {RdpRedirection.MaxHops} times without letting Orrery in. "
                + "Connect to the session host directly instead.");
        }

        return next;
    }

    /// <summary>
    /// Passes the server shared drives' file operations that finished after their request was answered, which the
    /// Windows file backend does for some. Stops at the first failure; the read loop reports a dropped connection.
    /// </summary>
    async Task DrivesLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && State == SessionState.Connected)
        {
            try
            {
                await Task.Delay(DrivesPollInterval, token);
                var outputs = WithStageLock(() => _activeStage!.PollRdpdrDeferred());
                if (!await HandleOutputsAsync(outputs, token))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Logger.LogWarning("RDP shared drives stopped: {Message}", e.Message);
                return;
            }
        }
    }

    /// <summary>
    /// IronRDP's failures in terms <see cref="ConnectionFailure.Classify"/> knows, with the original inside: its own
    /// name lookup, the server hanging up, and access denied. Socket errors and timeouts already say what happened.
    /// </summary>
    internal static Exception Recognize(Exception e)
    {
        var kind = e switch
        {
            IronRdpLibException { ErrorType: IronRdpLibExceptionType.CannotResolveDns } => FailureKind.NameNotFound,
            IronRdpLibException { ErrorType: IronRdpLibExceptionType.EndOfFile } => FailureKind.ServerClosed,
            IronRdpException rdp when KindOf(rdp) == IronRdpErrorKind.AccessDenied => FailureKind.LoginFailed,
            _ => (FailureKind?)null,
        };
        return kind is { } known ? new SessionFailureException(known, e.Message, e) : e;
    }

    static IronRdpErrorKind? KindOf(IronRdpException e)
    {
        try
        {
            return e.Inner.Kind;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    async Task ReadLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var (action, payload) = await _framed!.ReadPdu(token);
            bool resized = false;
            var outputs = WithStageLock(() =>
            {
                var processed = _activeStage!.Process(_image!, action, payload);
                try
                {
                    resized = FollowGraphicsReset();
                }
                catch
                {
                    processed.Dispose();
                    throw;
                }

                return processed;
            });

            if (resized)
            {
                // Before the outputs, so the view takes the new size before the updates drawn at it.
                FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));
            }

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
                Logger.LogWarning("RDP clipboard operation failed: {Message}", e.Message);
            }
            catch (Exception e)
            {
                // A write to a dropped connection, say. The read loop reports the disconnect.
                Logger.LogWarning("RDP clipboard loop ended: {Message}", e.Message);
                return;
            }
        }
    }

    /// <summary>Runs the text clipboard. A failed operation is logged and the bridge goes on with the next event.</summary>
    static async Task BridgeLoopAsync(RdpClipboardBridge bridge, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await bridge.RunAsync(token);
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (IronRdpException e)
            {
                Logger.LogWarning("RDP clipboard operation failed: {Message}", e.Message);
            }
            catch (Exception e)
            {
                // A write to a dropped connection, say. The read loop reports the disconnect.
                Logger.LogWarning("RDP clipboard loop ended: {Message}", e.Message);
                return;
            }
        }
    }

    public void SendClipboard(string text) => _bridge?.SetLocalText(text);

    /// <summary>IronRDP's callback clipboard, answered under the stage lock. Event data is copied out and capped here.</summary>
    sealed class CallbackChannel(RdpSession session, CallbackCliprdr clipboard) : IRdpClipboardChannel
    {
        public RdpClipboardEvent? NextEvent(TimeSpan timeout)
        {
            using var next = clipboard.NextEventTimeout((uint)timeout.TotalMilliseconds);
            return next?.EventType switch
            {
                null => null,
                ClipboardEventType.Ready => new(RdpClipboardEventKind.Ready),
                ClipboardEventType.FormatListRequested => new(RdpClipboardEventKind.FormatListRequested),
                ClipboardEventType.RemoteFormatList => new(RdpClipboardEventKind.RemoteFormatList, Formats: ReadFormats(next.RemoteFormats)),
                ClipboardEventType.FormatDataRequested => new(RdpClipboardEventKind.FormatDataRequested, Format: next.RequestedFormat?.Value ?? 0),
                ClipboardEventType.FormatDataResponse => new(RdpClipboardEventKind.FormatDataResponse, Format: next.ResponseFormat?.Value ?? 0, Data: ReadData(next.FormatData)),
                _ => new(RdpClipboardEventKind.Other),
            };
        }

        static uint[] ReadFormats(ClipboardFormatIterator? formats)
        {
            if (formats is null)
            {
                return [];
            }

            int count = (int)Math.Min(formats.Len(), MaxRemoteFormats);
            var ids = new uint[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = formats.Get((nuint)i)?.Id.Value ?? 0;
            }

            return ids;
        }

        byte[]? ReadData(FormatDataResponse? response)
        {
            if (response is null)
            {
                return null;
            }

            using (response)
            {
                if (response.IsError())
                {
                    return null;
                }

                if (response.DataSize() > (nuint)RdpClipboardBridge.MaxDataBytes)
                {
                    session._bridge?.RefuseRemote((long)response.DataSize());
                    return null;
                }

                using var data = response.Data();
                return ToBytes(data);
            }
        }

        public Task AnnounceFormatsAsync(IReadOnlyList<uint> formats, CancellationToken token)
        {
            var list = ClipboardFormatIterator.New();
            foreach (var format in formats)
            {
                list.Push(ClipboardFormat.New(ClipboardFormatId.New(format)));
            }

            return session.SubmitClipboardAsync(() => session._activeStage!.InitiateClipboardCopy(list), token);
        }

        public Task RequestDataAsync(uint format, CancellationToken token) =>
            session.SubmitClipboardAsync(() => session._activeStage!.InitiateClipboardPaste(ClipboardFormatId.New(format)), token);

        public async Task SendDataAsync(byte[]? data, CancellationToken token)
        {
            using var response = data is null ? FormatDataResponse.NewError() : FormatDataResponse.NewData(data);
            await session.SubmitClipboardAsync(() => session._activeStage!.SubmitClipboardFormatData(response), token);
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
                        Logger.LogDebug("Unhandled RDP output: {EnumType}", output.EnumType);
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
        Logger.LogInformation("{Host}: the server restarted the session's activation, desktop {Width}x{Height}",
            Target.Host, finalized.DesktopSize.Width, finalized.DesktopSize.Height);
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

    /// <summary>
    /// Follows the graphics pipeline resizing IronRDP's image in place, which it does when the server resets its
    /// graphics output, as it does for a resize instead of restarting the activation. Caller holds _stageLock.
    /// </summary>
    /// <returns>Whether the desktop changed size.</returns>
    bool FollowGraphicsReset()
    {
        ushort width = _image!.Width, height = _image.Height;
        if (width == Width && height == Height)
        {
            return false;
        }

        CheckDesktopSize(width, height);
        var frame = new byte[checked((long)width * height * BytesPerPixel)];
        _image.FillData(frame);
        _frame = frame;
        Width = width;
        Height = height;
        Logger.LogInformation("{Host}: the server reset its graphics, desktop {Width}x{Height}", Target.Host, width, height);
        return true;
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

    /// <inheritdoc/>
    public bool AcceptsTouch => RdpeiState() is { Touch: true, Suspended: false };

    /// <inheritdoc/>
    public bool AcceptsPen => RdpeiState() is { Touch: true, Pen: true, Suspended: false };

    /// <summary>What the input channel takes now; null until connected.</summary>
    RdpeiInputState? RdpeiState()
    {
        if (State != SessionState.Connected)
        {
            return null;
        }

        lock (_stageLock)
        {
            return _activeStage?.RdpeiInputState;
        }
    }

    public void SendTouch(ulong contact, TouchContactPhase phase, int x, int y, double? pressure)
    {
        var frames = new List<byte[]>();
        lock (_stageLock)
        {
            if (State != SessionState.Connected || _activeStage is not { } stage)
            {
                return;
            }

            var touches = _touches ??= TouchFrameBuilder.New(RdpConnector.MaxTouchContacts);
            var rdpPhase = phase switch
            {
                TouchContactPhase.Started => RdpTouchPhase.Started,
                TouchContactPhase.Moved => RdpTouchPhase.Moved,
                TouchContactPhase.Ended => RdpTouchPhase.Ended,
                _ => RdpTouchPhase.Canceled,
            };
            if (pressure is { } known)
            {
                touches.PushWithPressure(contact, rdpPhase, x, y, RdpeiValues.Pressure(known));
            }
            else
            {
                touches.Push(contact, rdpPhase, x, y);
            }

            EncodePending(() => stage.EncodeRdpeiTouch(touches), () => touches.HasPendingFrames, touches.Reset, frames);
        }

        QueueInputFrames(frames);
    }

    public void SendPen(ulong pen, PenContactPhase phase, int x, int y, PenState state)
    {
        var frames = new List<byte[]>();
        lock (_stageLock)
        {
            if (State != SessionState.Connected || _activeStage is not { } stage)
            {
                return;
            }

            var pens = _pens ??= PenFrameBuilder.New();
            var rdpPhase = phase switch
            {
                PenContactPhase.Hovering => RdpPenPhase.Hovering,
                PenContactPhase.Touching => RdpPenPhase.Touching,
                PenContactPhase.OutOfRange => RdpPenPhase.OutOfRange,
                _ => RdpPenPhase.Canceled,
            };
            pens.PushWithAttributes(pen, rdpPhase, x, y, new PenAttributes
            {
                BarrelPressed = state.Barrel,
                EraserPressed = state.Eraser,
                Inverted = state.Inverted,
                HasPressure = state.Pressure is not null,
                Pressure = RdpeiValues.Pressure(state.Pressure ?? 0),
                HasTiltX = state.TiltX is not null,
                TiltX = RdpeiValues.Tilt(state.TiltX ?? 0),
                HasTiltY = state.TiltY is not null,
                TiltY = RdpeiValues.Tilt(state.TiltY ?? 0),
                HasRotation = state.Rotation is not null,
                Rotation = RdpeiValues.Rotation(state.Rotation ?? 0),
            });
            EncodePending(() => stage.EncodeRdpeiPen(pens), () => pens.HasPendingFrames, pens.Reset, frames);
        }

        QueueInputFrames(frames);
    }

    /// <summary>
    /// Encodes every pending frame. When the channel can't take them (it closed, or the server paused input), what's
    /// pending is dropped rather than kept to grow, and the next touch starts afresh. Caller holds _stageLock.
    /// </summary>
    static void EncodePending(Func<VecU8?> encode, Func<bool> pending, System.Action drop, List<byte[]> into)
    {
        try
        {
            while (pending())
            {
                using var frame = encode();
                if (frame is null)
                {
                    break;
                }

                into.Add(ToBytes(frame));
            }
        }
        catch (IronRdpException e)
        {
            Logger.LogDebug("Touch or pen input not sent: {Message}", e.Message);
            drop();
        }
    }

    void QueueInputFrames(List<byte[]> frames)
    {
        foreach (var frame in frames)
        {
            _inputFrames.Writer.TryWrite(frame);
        }
    }

    /// <summary>Writes touch and pen frames in the order they were made, which one task per frame wouldn't keep.</summary>
    async Task InputFrameLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var frame in _inputFrames.Reader.ReadAllAsync(token))
            {
                await WriteAsync(frame, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            // A write to a dropped connection, say. The read loop reports the disconnect.
            Logger.LogWarning("RDP touch input stopped: {Message}", e.Message);
        }
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
        int scale = _desktopScale;
        if (width == Width && height == Height && scale == _sentScale)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Null when the server hasn't opened the display-control channel, i.e. it can't resize.
                var outputs = WithStageLock(() => _followsDisplayScale
                    ? _activeStage!.EncodedResizeWithScale((uint)width, (uint)height, (uint)scale)
                    : _activeStage!.EncodedResize((uint)width, (uint)height));
                if (outputs is not null)
                {
                    _sentScale = scale;
                    Logger.LogDebug("{Host}: asking for a {Width}x{Height} desktop at {Scale}%", Target.Host, width, height, scale);
                    await HandleOutputsAsync(outputs, _cts.Token);
                }
                else
                {
                    Logger.LogDebug("{Host}: can't resize, the server has no display control channel", Target.Host);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Logger.LogError("RDP resize to {Width}x{Height} failed: {Message}", width, height, e.Message);
            }
        });
    }

    /// <summary>
    /// Asks for a desktop of several monitors through the display-control channel, as for a resize. False when the
    /// server hasn't opened that channel, or IronRDP refuses the layout; the caller then asks for one monitor.
    /// </summary>
    public bool RequestMonitorLayout(IReadOnlyList<RemoteMonitor> monitors)
    {
        if (State != SessionState.Connected)
        {
            return false;
        }

        ActiveStageOutputIterator? outputs;
        try
        {
            var layout = DisplayMonitorLayout.New();
            foreach (var monitor in monitors)
            {
                if (_followsDisplayScale)
                {
                    layout.AddMonitorWithScale(monitor.Left, monitor.Top, (uint)monitor.Width, (uint)monitor.Height, monitor.IsPrimary, (uint)_desktopScale);
                }
                else
                {
                    layout.AddMonitor(monitor.Left, monitor.Top, (uint)monitor.Width, (uint)monitor.Height, monitor.IsPrimary);
                }
            }

            outputs = WithStageLock(() => _activeStage!.EncodedMonitorLayout(layout));
        }
        catch (Exception e) when (e is IronRdpException or EntryPointNotFoundException)
        {
            Logger.LogWarning("{Host}: can't spread the desktop over {Count} displays: {Message}", Target.Host, monitors.Count, e.Message);
            return false;
        }

        if (outputs is null)
        {
            Logger.LogDebug("{Host}: can't spread the desktop over displays, the server has no display control channel", Target.Host);
            return false;
        }

        Logger.LogDebug("{Host}: asking for {Count} monitors", Target.Host, monitors.Count);
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleOutputsAsync(outputs, _cts.Token);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Logger.LogError("RDP monitor layout failed: {Message}", e.Message);
            }
        });
        return true;
    }

    /// <inheritdoc/>
    public bool FollowsDisplayScale => _followsDisplayScale;

    /// <inheritdoc/>
    public void SetDisplayScale(double renderScaling)
    {
        if (_followsDisplayScale)
        {
            _desktopScale = ScalePercent(renderScaling);
        }
    }

    /// <summary>A display's scaling as the percentage RDP takes, 100 to 500 (MS-RDPBCGR 2.2.1.3.2).</summary>
    internal static int ScalePercent(double renderScaling) =>
        double.IsFinite(renderScaling) ? (int)Math.Clamp(Math.Round(renderScaling * 100), 100, 500) : 100;

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
                Logger.LogError("Sending RDP input failed: {Message}", e.Message);
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
                Logger.LogError("Graceful RDP shutdown failed: {Message}", e.Message);
            }
        }

        _cts.Cancel();
        _inputFrames.Writer.TryComplete();
        // Each loop has already reported its own outcome, so a fault here is only traced.
        await AwaitLoopAsync(_readLoop);
        await AwaitLoopAsync(_clipboardLoop);
        await AwaitLoopAsync(_inputFrameLoop);
        await AwaitLoopAsync(_drivesLoop);
        _route?.Dispose();
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
            Logger.LogWarning("RDP session loop had failed: {Message}", e.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        lock (_stageLock)
        {
            _image?.Dispose();
            _image = null;
            _touches?.Dispose();
            _touches = null;
            _pens?.Dispose();
            _pens = null;
        }

        _audioTap?.Dispose();

        // The loop has ended, so nothing is waiting on the clipboard any more.
        _clipboard?.Dispose();
        _clipboard = null;
        _textClipboard?.Dispose();
        _textClipboard = null;

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

    void SetState(SessionState state, string? message = null, Exception? error = null)
    {
        State = state;
        var change = new SessionStateChangedEventArgs(state, message, error);
        LastChange = change;
        StateChanged?.Invoke(this, change);
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

    /// <summary>
    /// The performance flags for <paramref name="quality"/>, as mstsc's experience settings set them. Balanced is
    /// IronRDP's own default; the server leaves out what's disabled, so less is drawn and sent.
    /// </summary>
    public static IReadOnlyList<PerformanceFlagsType> VisualEffects(DisplayQuality quality) => quality switch
    {
        DisplayQuality.Best => [PerformanceFlagsType.EnableFontSmoothing, PerformanceFlagsType.EnableDesktopComposition],
        DisplayQuality.Fastest =>
        [
            PerformanceFlagsType.DisableWallpaper, PerformanceFlagsType.DisableFullWindowDrag, PerformanceFlagsType.DisableMenuAnimations,
            PerformanceFlagsType.DisableTheming, PerformanceFlagsType.DisableCursorShadow, PerformanceFlagsType.DisableCursorSettings,
        ],
        _ => [PerformanceFlagsType.DisableFullWindowDrag, PerformanceFlagsType.DisableMenuAnimations, PerformanceFlagsType.EnableFontSmoothing],
    };

    static Config BuildConfig(ConnectionTarget target, string password, int width, int height, DisplayQuality quality, bool playAudio, int? desktopScale)
    {
        var builder = ConfigBuilder.New();
        builder.WithUsernameAndPassword(target.Username ?? string.Empty, password);
        if (!string.IsNullOrEmpty(target.Domain))
        {
            builder.Domain = target.Domain;
        }

        // IronRDP takes height first.
        builder.SetDesktopSize((ushort)height, (ushort)width);
        builder.ClientName = RdpConnector.ClientName;
        builder.ClientDir = "C:\\";
        var flags = PerformanceFlags.NewEmpty();
        foreach (var flag in VisualEffects(quality))
        {
            flags.AddFlag(flag);
        }

        builder.PerformanceFlags = flags;
        if (!playAudio)
        {
            try
            {
                // Tells the server not to send sound at all, rather than sending it to no one.
                builder.EnableAudioPlayback = false;
            }
            catch (EntryPointNotFoundException e)
            {
                Logger.LogWarning("Can't ask the server to keep its sound: {Message}", e.Message);
            }
        }

        if (desktopScale is { } scale)
        {
            try
            {
                builder.DesktopScaleFactor = (uint)scale;
            }
            catch (EntryPointNotFoundException e)
            {
                Logger.LogWarning("Can't tell the server this display's scaling: {Message}", e.Message);
            }
        }

        // NLA is required: credentials only ever go through CredSSP, inside a certificate-checked TLS session.
        // These are IronRDP's defaults today; they're set here so a changed default can't weaken the sign-in.
        builder.EnableCredssp = true;
        builder.EnableTls = false;
        return builder.Build();
    }
}

/// <summary>Touch and pen values in the units MS-RDPEI wants (2.2.3.3.1.1, 2.2.3.7.1.1).</summary>
internal static class RdpeiValues
{
    /// <summary>Pressure from 0 to 1 as 0 to 1024.</summary>
    public static uint Pressure(double pressure) => (uint)Math.Round(Math.Clamp(double.IsNaN(pressure) ? 0 : pressure, 0, 1) * 1024);

    /// <summary>Tilt in degrees, -90 to 90.</summary>
    public static short Tilt(double degrees) => (short)Math.Round(Math.Clamp(double.IsNaN(degrees) ? 0 : degrees, -90, 90));

    /// <summary>Rotation in degrees, wrapped into 0 to 359.</summary>
    public static ushort Rotation(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees))
        {
            return 0;
        }

        int whole = (int)Math.Round(degrees % 360);
        return (ushort)((whole % 360 + 360) % 360);
    }
}
