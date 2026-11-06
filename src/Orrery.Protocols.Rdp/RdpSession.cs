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
public sealed class RdpSession : IFrameBufferSession, IAudioSession, IClipboardSession, ITouchInput, IMultiMonitorSession, IDisplayScaleSession, IRemotePointerSession
{
    const int BytesPerPixel = 4;
    const uint ClipboardWaitMs = 1000;
    const uint MaxQueuedClipboardEvents = 64;
    const int MaxRemoteFormats = 256;
    static readonly TimeSpan ClipboardTimeoutsInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often shared drives' file operations that finish later (on Windows) are passed to the server.</summary>
    static readonly TimeSpan DrivesPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long closing waits for the server to take the shutdown, and then for the loops to end, before closing the connection.</summary>
    static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(2);

    readonly object _stageLock = new();

    // The managed copy of the picture (_frame) and the size it's laid out at, as CopyRegion reads them. Taken inside
    // _stageLock, never around it, so the view and a recording copy pixels out without waiting while IronRDP decodes
    // the next update under _stageLock.
    readonly object _frameLock = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly InputDatabase _inputDatabase = InputDatabase.New();
    readonly CancellationTokenSource _cts = new();

    // The connector's own, kept rather than wrapped again: it holds bytes the server already sent that aren't read yet.
    Framed<SslStream>? _framed;
    ActiveStage? _activeStage;
    DecodedImage? _image;
    byte[] _frame = [];
    bool _pointerSoftwareRendering;

    // Resizes asked for and not yet sent: before the display-control channel opened, or while the server was still
    // working on the last one. Under _stageLock.
    readonly ResizeGate _resizes = new();

    // For the detailed log: when the session connected or the server resized, until the first picture after it.
    long _firstPictureFrom;
    string? _firstPictureAfter;

    // Keyboard and mouse input goes out after the input before it, in the order it was applied. Under _stageLock.
    Task _inputChain = Task.CompletedTask;

    // Input has a lock of its own: under _stageLock, a mouse move on the UI thread waited for a picture to decode.
    readonly object _inputLock = new();

    // The newest pointer position not yet sent, while a send for it is queued. Moves made before that send runs are
    // merged into it, so a fast mouse sends only where it's got to, not every step on the way.
    readonly LatestOnly<MousePosition> _latestMove = new();
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

    public RemotePointerShape? PointerShape { get; private set; }

    public event EventHandler? PointerShapeChanged;
    public event EventHandler<string>? RemoteClipboardChanged;

    /// <inheritdoc/>
    public event EventHandler<string>? ClipboardRefused;

    /// <summary>Whether clipboard text goes through <see cref="SendClipboard"/> and <see cref="RemoteClipboardChanged"/>.</summary>
    public bool ExchangesClipboard => _textClipboard is not null;

    /// <summary>Whether images and files go through this interface too: the same, as the callback clipboard carries both.</summary>
    public bool ExchangesFiles => _textClipboard is not null;

    /// <inheritdoc/>
    public event EventHandler<byte[]>? RemoteClipboardImageChanged;

    /// <inheritdoc/>
    public event EventHandler<IReadOnlyList<string>>? RemoteClipboardFilesChanged;

    /// <inheritdoc/>
    public event EventHandler<ClipboardTransfer>? ClipboardTransferChanged;

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
                        (uint)RdpClipboardBridge.MaxDataBytes * 2);
                    session._textClipboard.EnableFileTransfer();
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
            _framed = framed;

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
                _bridge.RemoteImageChanged += (_, image) => RemoteClipboardImageChanged?.Invoke(this, image);
                _bridge.RemoteFilesChanged += (_, paths) => RemoteClipboardFilesChanged?.Invoke(this, paths);
                _bridge.TransferChanged += (_, transfer) => ClipboardTransferChanged?.Invoke(this, transfer);
            }

            (_firstPictureFrom, _firstPictureAfter) = (Stopwatch.GetTimestamp(), "connecting");
            SetState(SessionState.Connected);
            FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));
            _inputFrameLoop = Task.Run(() => InputFrameLoopAsync(token));
            // Each waits for the clipboard in a blocking call, so on a thread of its own rather than the pool's.
            if (_clipboard is not null)
            {
                var clipboard = _clipboard;
                _clipboardLoop = OwnThread.Run("RDP clipboard", () => ClipboardLoopAsync(clipboard, token));
            }
            else if (_bridge is not null)
            {
                var bridge = _bridge;
                _clipboardLoop = OwnThread.Run("RDP clipboard", () => BridgeLoopAsync(bridge, token));
            }

            if (drivesShared)
            {
                _drivesLoop = Task.Run(() => DrivesLoopAsync(token));
            }

            await ReadLoopAsync(token);
            SetState(SessionState.Disconnected);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Closing the session also closes the connection, so a read under way ends with that rather than cancellation.
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
            DesktopRequest? waitingResize = null;
            var outputs = WithStageLock(() =>
            {
                var processed = _activeStage!.Process(_image!, action, payload);
                try
                {
                    resized = FollowGraphicsReset();
                    waitingResize = TakeReadyResize();
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

            if (waitingResize is { } size)
            {
                Logger.LogDebug("{Host}: the server can resize now, asking for the size wanted meanwhile", Target.Host);
                _ = Task.Run(() => SendResizeAsync(size));
            }
        }
    }

    /// <summary>
    /// The size asked for while the server couldn't take it (before its display-control channel opened, or while it
    /// was still resizing), once it can, and forgets it. Null while there's none, or it still can't. Caller holds
    /// _stageLock.
    /// </summary>
    DesktopRequest? TakeReadyResize()
    {
        if (!_resizes.HasWaiting)
        {
            return null;
        }

        bool ready;
        try
        {
            ready = _activeStage!.DisplayControlState == DisplayControlState.Ready;
        }
        catch (EntryPointNotFoundException)
        {
            // A native library that can't say: the size is dropped, as it was before.
            _resizes.Drop();
            return null;
        }

        return ready ? _resizes.TakeWaiting(Stopwatch.GetTimestamp()) : null;
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

    public void SendClipboardImage(ClipboardImage image) => _bridge?.SetLocalImage(image);

    public void SendClipboardFiles(IReadOnlyList<string> paths) => _bridge?.SetLocalFiles(paths);

    public void CancelClipboardTransfer() => _bridge?.CancelDownload();

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
                ClipboardEventType.RemoteFormatList => ReadFormats(next.RemoteFormats),
                ClipboardEventType.FormatDataRequested => new(RdpClipboardEventKind.FormatDataRequested, Format: next.RequestedFormat?.Value ?? 0),
                ClipboardEventType.FormatDataResponse => new(RdpClipboardEventKind.FormatDataResponse, Format: next.ResponseFormat?.Value ?? 0, Data: ReadData(next.FormatData)),
                ClipboardEventType.RemoteFileList => new(RdpClipboardEventKind.RemoteFileList, Format: next.ResponseFormat?.Value ?? 0)
                {
                    Files = ReadFiles(next.RemoteFiles),
                    LockId = next.HasRemoteFilesLockId() ? next.RemoteFilesLockId() : null,
                },
                ClipboardEventType.FileContentsRequested when next.FileContentsRequest is { } request => new(RdpClipboardEventKind.FileContentsRequested)
                {
                    FileRequest = new(request.StreamId(), request.Index(), request.IsSizeRequest(), (long)Math.Min(request.Position(), long.MaxValue),
                        (int)Math.Min(request.RequestedSize(), int.MaxValue), request.HasDataId() ? request.DataId() : null),
                },
                ClipboardEventType.FileContentsResponse when next.FileContentsResponse is { } response => ReadFileContents(response),
                ClipboardEventType.Lock => new(RdpClipboardEventKind.Lock) { LockId = next.LockId() },
                ClipboardEventType.Unlock => new(RdpClipboardEventKind.Unlock) { LockId = next.LockId() },
                _ => new(RdpClipboardEventKind.Other),
            };
        }

        public bool FileTransfer => clipboard.FileTransferNegotiated;

        static RdpClipboardEvent ReadFormats(ClipboardFormatIterator? formats)
        {
            int count = formats is null ? 0 : (int)Math.Min(formats.Len(), MaxRemoteFormats);
            var ids = new uint[count];
            var names = new string?[count];
            for (int i = 0; i < count; i++)
            {
                if (formats!.Get((nuint)i) is { } format)
                {
                    ids[i] = format.Id.Value;
                    names[i] = format.HasName() ? format.Name : null;
                }
            }

            return new(RdpClipboardEventKind.RemoteFormatList, Formats: ids) { Names = names };
        }

        static RdpRemoteFile[] ReadFiles(FileDescriptorList? list)
        {
            if (list is null)
            {
                return [];
            }

            using (list)
            {
                // One past the limit, so the bridge sees there were too many.
                int count = (int)Math.Min(list.Len(), (nuint)RdpClipboardFiles.MaxEntries + 1);
                var files = new RdpRemoteFile[count];
                for (int i = 0; i < count; i++)
                {
                    using var file = list.Get((nuint)i)!;
                    files[i] = new(file.GetName(), file.GetRelativePath(),
                        file.HasSize() ? (long)Math.Min(file.GetSize(), long.MaxValue) : null, file.IsDirectory());
                }

                return files;
            }
        }

        static RdpClipboardEvent ReadFileContents(FfiFileContentsResponse response)
        {
            using (response)
            {
                byte[]? data = null;
                if (!response.IsError())
                {
                    using var bytes = response.Data();
                    data = ToBytes(bytes);
                }

                return new(RdpClipboardEventKind.FileContentsResponse, Data: data) { StreamId = response.StreamId() };
            }
        }

        static byte[]? ReadData(FormatDataResponse? response)
        {
            if (response is null)
            {
                return null;
            }

            using (response)
            {
                // IronRDP passes data over its limit as an error, so this is only a second check.
                if (response.IsError() || response.DataSize() > (nuint)RdpClipboardBridge.MaxDataBytes)
                {
                    return null;
                }

                using var data = response.Data();
                return ToBytes(data);
            }
        }

        public Task AnnounceFormatsAsync(IReadOnlyList<RdpClipboardFormat> formats, CancellationToken token)
        {
            var list = ClipboardFormatIterator.New();
            foreach (var format in formats)
            {
                var id = ClipboardFormatId.New(format.Id);
                list.Push(format.Name is { } name ? ClipboardFormat.NewNamed(id, name) : ClipboardFormat.New(id));
            }

            return session.SubmitClipboardAsync(() => session._activeStage!.InitiateClipboardCopy(list), token);
        }

        public async Task OfferFilesAsync(IReadOnlyList<LocalClipboardFile> files, CancellationToken token)
        {
            using var list = FileDescriptorList.New();
            foreach (var file in files)
            {
                using var descriptor = FileDescriptor.New(file.Name, file.RelativePath, (ulong)file.Size, file.Directory, (ulong)file.LastWriteUtc.ToFileTimeUtc());
                list.Push(descriptor);
            }

            using var message = ClipboardMessage.NewInitiateFileCopy(list);
            await SubmitMessageAsync(message, token);
        }

        public async Task RequestFileContentsAsync(uint streamId, int index, bool sizeRequest, long position, int length, uint? lockId, CancellationToken token)
        {
            using var message = ClipboardMessage.NewFileContentsRequest(streamId, index, sizeRequest, (ulong)position, (uint)length, lockId is not null, lockId ?? 0);
            await SubmitMessageAsync(message, token);
        }

        public async Task SendFileContentsAsync(uint streamId, byte[]? data, CancellationToken token)
        {
            using var message = data is null ? ClipboardMessage.NewFileContentsError(streamId) : ClipboardMessage.NewFileContentsData(streamId, data);
            await SubmitMessageAsync(message, token);
        }

        public async Task SendFileSizeAsync(uint streamId, long size, CancellationToken token)
        {
            using var message = ClipboardMessage.NewFileContentsSize(streamId, (ulong)size);
            await SubmitMessageAsync(message, token);
        }

        public Task DriveTimeoutsAsync(CancellationToken token) =>
            session.SubmitClipboardAsync(() => session._activeStage!.DriveClipboardTimeouts(), token);

        Task SubmitMessageAsync(ClipboardMessage message, CancellationToken token) =>
            session.SubmitClipboardAsync(() => session._activeStage!.SubmitClipboardMessage(message), token);

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
                            // IronRDP gives one for nearly every message the server sends, and most are empty: with
                            // the graphics pipeline, every 1.6 KB piece of a picture has one. On Windows an empty
                            // write still sends an encrypted record of its own, and each waits on the lock input
                            // goes through.
                            if (frame.Size > 0)
                            {
                                await WriteAsync(ToBytes(frame), token);
                            }
                        }
                        break;

                    case ActiveStageOutputType.GraphicsUpdate:
                        OnGraphicsUpdate(output.GraphicsUpdate);
                        break;

                    case ActiveStageOutputType.DeactivateAll:
                        await ReactivateAsync(token);
                        break;

                    case ActiveStageOutputType.PointerBitmap:
                        using (var pointer = output.PointerBitmap)
                        {
                            SetPointerShape(PointerFrom(pointer));
                        }
                        break;

                    case ActiveStageOutputType.PointerDefault:
                        SetPointerShape(null);
                        break;

                    case ActiveStageOutputType.PointerHidden:
                        SetPointerShape(RemotePointerShape.Hidden);
                        break;

                    case ActiveStageOutputType.PointerPosition:
                        // The server moving the pointer itself; the view follows the local pointer.
                        break;

                    default:
                        Logger.LogDebug("Unhandled RDP output: {EnumType}", output.EnumType);
                        break;
                }
            }
        }

        return true;
    }

    /// <summary>A shape the server sent: RGBA with straight alpha, as IronRDP decodes it when Orrery draws the pointer.</summary>
    static RemotePointerShape? PointerFrom(DecodedPointer pointer)
    {
        using var data = pointer.Data;
        var shape = RemotePointerShape.Create(pointer.Width, pointer.Height, pointer.HotspotX, pointer.HotspotY, ToBytes(data));
        if (shape is null)
        {
            Logger.LogDebug("Left out a {Width}x{Height} pointer", pointer.Width, pointer.Height);
        }

        return shape;
    }

    void SetPointerShape(RemotePointerShape? shape)
    {
        if (ReferenceEquals(shape, PointerShape))
        {
            return;
        }

        PointerShape = shape;
        PointerShapeChanged?.Invoke(this, EventArgs.Empty);
    }

    void OnGraphicsUpdate(InclusiveRectangle damage)
    {
        if (_firstPictureAfter is { } after)
        {
            _firstPictureAfter = null;
            Logger.LogDebug("{Host}: first picture {Ms:0} ms after {After}", Target.Host, Stopwatch.GetElapsedTime(_firstPictureFrom).TotalMilliseconds, after);
        }

        FrameRect rect;
        lock (_stageLock)
        {
            lock (_frameLock)
            {
                _image!.CopyRectInto(damage, _frame, (nuint)(Width * BytesPerPixel));
            }

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
            NoteServerResized();
        }

        FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, Width, Height), resized: true));
    }

    /// <summary>
    /// The server resized, so a size held back meanwhile can go. For the detailed log, how long it took after Orrery
    /// asked, and then to draw. Caller holds _stageLock.
    /// </summary>
    void NoteServerResized()
    {
        if (_resizes.Answered() is var asked and not 0)
        {
            Logger.LogDebug("{Host}: the server resized {Ms:0} ms after Orrery asked", Target.Host, Stopwatch.GetElapsedTime(asked).TotalMilliseconds);
            (_firstPictureFrom, _firstPictureAfter) = (Stopwatch.GetTimestamp(), "resizing");
        }
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
        lock (_frameLock)
        {
            var frame = FrameFor(width, height);
            _image.FillData(frame);
            (_frame, Width, Height) = (frame, width, height);
        }

        Logger.LogInformation("{Host}: the server reset its graphics, desktop {Width}x{Height}", Target.Host, width, height);
        NoteServerResized();
        return true;
    }

    // Caller holds _stageLock. Width and Height change only once both buffers exist, so a failed
    // allocation leaves the previous frame whole.
    void ResetImage(ushort width, ushort height)
    {
        var image = DecodedImage.New(RdpPixelFormat.RgbA32, width, height);
        try
        {
            lock (_frameLock)
            {
                var frame = FrameFor(width, height);
                image.FillData(frame);
                (_frame, Width, Height) = (frame, width, height);
            }
        }
        catch
        {
            image.Dispose();
            throw;
        }

        var previous = _image;
        _image = image;
        previous?.Dispose();
    }

    /// <summary>
    /// A buffer for a picture of this size: the current one when it's big enough, as it is when the desktop shrinks,
    /// so a window dragged smaller doesn't allocate a large array for each step. Otherwise a new one, not cleared
    /// first, as the image is copied over the part that's used. Caller holds _frameLock.
    /// </summary>
    byte[] FrameFor(int width, int height)
    {
        int size = checked((int)((long)width * height * BytesPerPixel));
        return _frame.Length >= size ? _frame : GC.AllocateUninitializedArray<byte>(size);
    }

    public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride)
    {
        lock (_frameLock)
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
        if (State != SessionState.Connected)
        {
            return;
        }

        var position = MousePosition.New((ushort)Math.Clamp(x, 0, ushort.MaxValue), (ushort)Math.Clamp(y, 0, ushort.MaxValue));
        lock (_inputLock)
        {
            if (_latestMove.Offer(position))
            {
                _inputChain = InOrder(_inputChain, () => TakeLatestMove() is { } fastPath ? SendFastPathAsync(fastPath) : Task.CompletedTask);
            }
        }
    }

    /// <summary>The newest move not yet sent, applied to the input state; null when something else already sent it.</summary>
    FastPathInputEventIterator? TakeLatestMove()
    {
        lock (_inputLock)
        {
            return _latestMove.Take() is { } position ? _inputDatabase.Apply(position.AsMoveOperation()) : null;
        }
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
        var wanted = new DesktopRequest(Math.Clamp(width, 200, DesktopLimits.MaxSide) & ~1, Math.Clamp(height, 200, DesktopLimits.MaxSide), _desktopScale);
        _ = Task.Run(() => SendResizeAsync(wanted));
    }

    /// <summary>
    /// Asks the server for <paramref name="wanted"/> unless the desktop is that already. While the server can't take
    /// it, as before its display-control channel opens or while it's still working on the last resize, it's kept and
    /// asked for once it can (see <see cref="ResizeGate"/>).
    /// </summary>
    async Task SendResizeAsync(DesktopRequest wanted)
    {
        try
        {
            var token = _cts.Token;
            var (decision, outputs, heldFor) = WithStageLock(() =>
            {
                long now = Stopwatch.GetTimestamp();
                var decision = _resizes.Offer(wanted, new DesktopRequest(Width, Height, _sentScale), now);
                if (decision != ResizeGate.Decision.Send)
                {
                    return (decision, (ActiveStageOutputIterator?)null, _resizes.HeldFor(now));
                }

                // Null when the server hasn't opened the display-control channel, i.e. it can't resize yet.
                var encoded = _followsDisplayScale
                    ? _activeStage!.EncodedResizeWithScale((uint)wanted.Width, (uint)wanted.Height, (uint)wanted.Scale)
                    : _activeStage!.EncodedResize((uint)wanted.Width, (uint)wanted.Height);
                if (encoded is null)
                {
                    _resizes.Keep(wanted);
                }
                else
                {
                    // Before it's written, so an answer can't come before the request is counted as asked.
                    _resizes.Sent(wanted, now);
                    _sentScale = wanted.Scale;
                }

                return (decision, encoded, TimeSpan.Zero);
            });

            if (decision == ResizeGate.Decision.Hold)
            {
                Logger.LogDebug("{Host}: holding a {Width}x{Height} desktop until the server has finished resizing", Target.Host, wanted.Width, wanted.Height);
                _ = SendHeldResizeAsync(heldFor, token);
            }
            else if (decision == ResizeGate.Decision.Send && outputs is null)
            {
                Logger.LogDebug("{Host}: can't resize yet, the server has no display control channel", Target.Host);
            }
            else if (outputs is not null)
            {
                Logger.LogDebug("{Host}: asking for a {Width}x{Height} desktop at {Scale}%", Target.Host, wanted.Width, wanted.Height, wanted.Scale);
                await HandleOutputsAsync(outputs, token);
            }
        }
        catch (ObjectDisposedException) when (State != SessionState.Connected)
        {
            // Closed meanwhile.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Logger.LogError("RDP resize to {Width}x{Height} failed: {Message}", wanted.Width, wanted.Height, e.Message);
        }
    }

    /// <summary>
    /// Asks for a size held back once the resize before it stops holding it back, for a server that leaves the size
    /// as it was without a word. When the server resizes first, the read loop has already sent it.
    /// </summary>
    async Task SendHeldResizeAsync(TimeSpan after, CancellationToken token)
    {
        try
        {
            await Task.Delay(after + TimeSpan.FromMilliseconds(10), token);
            if (State == SessionState.Connected && WithStageLock(TakeReadyResize) is { } held)
            {
                await SendResizeAsync(held);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed meanwhile.
        }
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

        lock (_inputLock)
        {
            // A click goes where the pointer is now, so a move still waiting is sent first, with it.
            if (TakeLatestMove() is { } move)
            {
                _inputChain = InOrder(_inputChain, () => SendFastPathAsync(move));
            }

            // Applied and queued under one lock, so a key's release can't reach the server before its press.
            var fastPath = _inputDatabase.Apply(operation);
            _inputChain = InOrder(_inputChain, () => SendFastPathAsync(fastPath));
        }
    }

    /// <summary>Starts <paramref name="next"/> on the thread pool once <paramref name="previous"/> has ended, however it ended.</summary>
    internal static Task InOrder(Task previous, Func<Task> next) =>
        previous.ContinueWith(_ => next(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

    async Task SendFastPathAsync(FastPathInputEventIterator fastPath)
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
    }

    public async Task DisconnectAsync()
    {
        if (State == SessionState.Connected)
        {
            try
            {
                // Bounded: a write to a connection that died quietly would otherwise wait for TCP to give up.
                var outputs = WithStageLock(() => _activeStage!.GracefulShutdown());
                await HandleOutputsAsync(outputs, _cts.Token).WaitAsync(GracefulShutdownTimeout);
            }
            catch (TimeoutException)
            {
                Logger.LogWarning("{Host} didn't take the RDP shutdown in time; closing the connection", Target.Host);
            }
            catch (Exception e)
            {
                Logger.LogError("Graceful RDP shutdown failed: {Message}", e.Message);
            }
        }

        _cts.Cancel();
        _inputFrames.Writer.TryComplete();
        // Each loop has already reported its own outcome, so a fault here is only traced.
        var loops = Task.WhenAll(AwaitLoopAsync(_readLoop), AwaitLoopAsync(_clipboardLoop), AwaitLoopAsync(_inputFrameLoop), AwaitLoopAsync(_drivesLoop));
        try
        {
            await loops.WaitAsync(GracefulShutdownTimeout);
        }
        catch (TimeoutException)
        {
            // One is stuck in a write to a dead connection, which takes no token; closing the connection ends it.
            Logger.LogDebug("{Host}: the RDP session's loops didn't end in time; closing the connection", Target.Host);
            CloseConnection();
            await loops;
        }

        // Done with the connection, so it's closed now rather than when the stream is collected; that also ends the
        // read a cancelled ReadPdu left in flight, which is then looked at.
        CloseConnection();
        if (_framed is { } framed)
        {
            DrainStrandedRead(framed);
        }

        _route?.Dispose();
    }

    /// <summary>
    /// Observes the stream read that a cancelled <see cref="Framed{TS}.ReadPdu(CancellationToken)"/> leaves in flight
    /// for the next read to finish. When the session ends there's no next read, so its failure once the connection
    /// closes or the server resets it went unobserved, and was logged as an unobserved task exception.
    /// </summary>
    internal static void DrainStrandedRead<TS>(Framed<TS> framed) where TS : Stream =>
        _ = framed.ReadPdu(CancellationToken.None).ContinueWith(read => _ = read.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// Closes the TLS stream and the socket under it, which ends a read or write stuck on a dead connection, and the
    /// loops waiting behind it.
    /// </summary>
    void CloseConnection()
    {
        try
        {
            _framed?.GetInner().Item1.Dispose();
        }
        catch (Exception e)
        {
            Logger.LogDebug("Closing the RDP connection: {Message}", e.Message);
        }
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
        _bridge?.DeleteDownloads();

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

        // The server sends its pointer's shapes, decoded for Orrery to draw (not drawn into the picture), so a
        // recording can show them.
        try
        {
            builder.EnableServerPointer = true;
            builder.PointerSoftwareRendering = false;
        }
        catch (EntryPointNotFoundException e)
        {
            Logger.LogWarning("Can't ask the server for its pointer's shape: {Message}", e.Message);
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
