using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.EncodingTypes;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.MessageTypes;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Protocol.Implementation.SecurityTypes;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Output;
using MarcusW.VncClient.Rendering;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;
using VncPixelFormat = MarcusW.VncClient.PixelFormat;
using VncSize = MarcusW.VncClient.Size;

namespace Orrery.Protocols.Vnc;

/// <summary>
/// A VNC session backed by MarcusW.VncClient (MIT). The library decodes straight into our RGBA buffer
/// through <see cref="IRenderTarget"/>; keys go over as X11 keysyms, so the server needs no keyboard layout.
/// </summary>
public sealed class VncSession : IFrameBufferSession, IKeysymInput, IClipboardSession, IRenderTarget, IOutputHandler, ICursorHandler,
    IRemotePointerSession, ILinkStatsSession
{
    const int BytesPerPixel = 4;
    const int WheelNotch = 120;
    static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(2);

    // Byte order R, G, B, then an unused byte: what RemoteFrameView's RGBA bitmap expects.
    static readonly VncPixelFormat Rgba = new("Orrery RGBA", 32, 24, bigEndian: false, trueColor: true, hasAlpha: false,
        redMax: 255, greenMax: 255, blueMax: 255, alphaMax: 0, redShift: 0, greenShift: 8, blueShift: 16, alphaShift: 0);

    // Held from GrabFramebufferReference until the library disposes the reference, so a copy for the screen
    // never sees half a buffer swap. A semaphore, because the release may come from another thread.
    readonly SemaphoreSlim _frameLock = new(1, 1);
    readonly object _inputLock = new();
    readonly HashSet<uint> _keysDown = [];
    readonly CancellationTokenSource _cts = new();
    readonly string _password;

    RfbConnection? _connection;
    IntPtr _buffer;
    int _bufferWidth, _bufferHeight;
    bool _sizeChanged;
    volatile bool _desktopRefused;
    MouseButtons _buttons;
    Position _pointer = new(0, 0);
    int _wheelVertical, _wheelHorizontal;
    Task? _connectTask;

    readonly ILoggerFactory _logging;
    readonly bool _shareClipboard;
    readonly ISessionRoute? _route;
    readonly DisplayQuality _quality;

    VncSession(ConnectionTarget target, string password, bool shareClipboard, ILoggerFactory? logging, ISessionRoute? route, DisplayQuality quality)
    {
        Target = target;
        _password = password;
        _shareClipboard = shareClipboard;
        _logging = logging ?? Log.Factory;
        _route = route;
        _quality = quality;
    }

    public ConnectionTarget Target { get; }

    /// <summary>The traffic and round trip the session pane shows.</summary>
    public LinkStats Link { get; } = new();
    public SessionState State { get; private set; } = SessionState.Connecting;
    public int Width => _bufferWidth;
    public int Height => _bufferHeight;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public SessionStateChangedEventArgs? LastChange { get; private set; }
    public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;

    public RemotePointerShape? PointerShape { get; private set; }

    public event EventHandler? PointerShapeChanged;
    public event EventHandler<string>? RemoteClipboardChanged;

    /// <summary>Starts connecting in the background and returns at once; watch <see cref="StateChanged"/>.</summary>
    /// <param name="logging">The VNC library's own diagnostics; none when null.</param>
    /// <param name="shareClipboard">Exchange clipboard text with the server (RFB carries Latin-1 only).</param>
    /// <param name="route">How to reach the server, such as an SSH tunnel; directly when null. Disposed with the session.</param>
    /// <param name="quality">How much image quality to give up for speed; it only changes the quality of Tight's JPEG.</param>
    public static VncSession Start(ConnectionTarget target, string password, bool shareClipboard = true, ILoggerFactory? logging = null, ISessionRoute? route = null,
        DisplayQuality quality = DisplayQuality.Balanced)
    {
        var session = new VncSession(target, password, shareClipboard, logging, route, quality);
        session._connectTask = Task.Run(session.ConnectAsync);
        return session;
    }

    async Task ConnectAsync()
    {
        try
        {
            var (host, port) = await _route.DialAsync(Target, _cts.Token);
            var client = new VncClient(_logging, new ClosableImplementation(this));
            var (jpegQuality, subsampling) = ImageSettings(_quality);
            var parameters = new ConnectParameters
            {
                JpegQualityLevel = jpegQuality,
                JpegSubsamplingLevel = subsampling,
                TransportParameters = new TcpTransportParameters { Host = host, Port = port },
                AuthenticationHandler = new Authenticator(_password),
                InitialRenderTarget = this,
                InitialOutputHandler = this,
                AllowSharedConnection = true,

                // A dropped session shows as disconnected, like RDP and SSH; reconnecting is the user's call.
                MaxReconnectAttempts = 0,
            };

            var connection = await client.ConnectAsync(parameters, _cts.Token);
            connection.ConnectionStateChanged += OnConnectionStateChanged;
            _connection = connection;
            Log.For("Vnc").LogInformation("Connected to {Host}: {ProtocolVersion}, desktop {Width}x{Height}, {Quality} quality", Target.Host,
                connection.ProtocolVersion.ToString(), connection.RemoteFramebufferSize.Width, connection.RemoteFramebufferSize.Height, _quality.ToString());
            if (connection.ConnectionState == ConnectionState.Connected)
            {
                SetState(SessionState.Connected);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            SetState(SessionState.Disconnected);
        }
        catch (Exception e)
        {
            Log.For("Vnc").LogError(e, "VNC session to {Host} failed", Target.Host);
            SetState(SessionState.Failed, Describe(e), Recognize(e));
        }
    }

    /// <summary>
    /// The VNC library's failures in terms <see cref="ConnectionFailure.Classify"/> knows, with the original inside:
    /// the server hanging up, and a refused password. Socket errors already say what happened.
    /// </summary>
    internal static Exception Recognize(Exception e) => e switch
    {
        UnexpectedEndOfStreamException => new SessionFailureException(FailureKind.ServerClosed, e.Message, e),
        HandshakeFailedException when IsRefusedLogin(e.Message) => new SessionFailureException(FailureKind.LoginFailed, e.Message, e),
        _ => e,
    };

    static bool IsRefusedLogin(string message) =>
        message.Contains("auth", StringComparison.OrdinalIgnoreCase) && message.Contains("fail", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// JPEG quality (percent) and chroma subsampling for <paramref name="quality"/>. Full quality is the library's
    /// default, so Best and Balanced are the same. JPEG only applies when the server sends Tight, which needs
    /// TurboJPEG installed here; otherwise the image is lossless. The library doesn't send a compression level
    /// (ConnectParameters.PreferredCompressionLevel is left out of what it asks for), so there's none to set.
    /// </summary>
    public static (int JpegQuality, JpegSubsamplingLevel Subsampling) ImageSettings(DisplayQuality quality) => quality switch
    {
        DisplayQuality.Fastest => (60, JpegSubsamplingLevel.ChrominanceSubsampling4X),
        _ => (100, JpegSubsamplingLevel.None),
    };

    static string Describe(Exception e) => e switch
    {
        RouteException route => route.Message,
        System.Net.Sockets.SocketException s => s.Message,
        _ when e.Message.Contains("auth", StringComparison.OrdinalIgnoreCase) => $"The server refused the login. {e.Message}",
        _ => e.Message,
    };

    void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        switch (e.CurrentState)
        {
            case ConnectionState.Connected:
                SetState(SessionState.Connected);
                break;
            case ConnectionState.Interrupted or ConnectionState.ReconnectFailed:
                SetState(SessionState.Failed, e.Exception?.Message ?? e.Reason ?? "The connection was lost.", e.Exception is null ? null : Recognize(e.Exception));
                break;
            case ConnectionState.Closed:
                SetState(SessionState.Disconnected);
                break;
        }
    }

    // ---- Frame buffer ----

    unsafe IFramebufferReference IRenderTarget.GrabFramebufferReference(VncSize size, IImmutableSet<Screen> layout)
    {
        // The server picks the size, and the library writes server pixels at that size's stride, so a
        // desktop we won't allocate ends the session. The session is failed here as well as by the library's
        // receive loop, which can lose the exception if it comes before the connection has finished starting.
        if (!DesktopLimits.IsSupported(size.Width, size.Height))
        {
            var message = DesktopLimits.RefusalMessage(size.Width, size.Height);
            _desktopRefused = true;
            SetState(SessionState.Failed, message);
            throw new RfbProtocolException(message);
        }

        _frameLock.Wait();
        try
        {
            if (size.Width != _bufferWidth || size.Height != _bufferHeight || _buffer == IntPtr.Zero)
            {
                FreeBuffer();
                _buffer = (IntPtr)NativeMemory.AllocZeroed(checked((nuint)size.Width * (nuint)size.Height * BytesPerPixel));
                (_bufferWidth, _bufferHeight) = (size.Width, size.Height);
                _sizeChanged = true;
            }

            return new FrameReference(this, size);
        }
        catch
        {
            _frameLock.Release();
            throw;
        }
    }

    /// <summary>The library has finished writing; the frame lock is released and the screen told.</summary>
    void EndFrame()
    {
        bool resized = _sizeChanged;
        _sizeChanged = false;
        int width = _bufferWidth, height = _bufferHeight;
        _frameLock.Release();

        // The library doesn't say which rectangles changed, so the whole frame is offered.
        FrameUpdated?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, width, height), resized));
    }

    public unsafe void CopyRegion(FrameRect region, IntPtr destination, int destinationStride)
    {
        _frameLock.Wait();
        try
        {
            if (_buffer == IntPtr.Zero)
            {
                return;
            }

            int x = Math.Max(0, region.X), y = Math.Max(0, region.Y);
            int right = Math.Min(_bufferWidth, region.Right), bottom = Math.Min(_bufferHeight, region.Bottom);
            int rowBytes = (right - x) * BytesPerPixel;
            if (rowBytes <= 0)
            {
                return;
            }

            nint sourceStride = (nint)_bufferWidth * BytesPerPixel;
            for (int row = y; row < bottom; row++)
            {
                var source = new ReadOnlySpan<byte>((byte*)_buffer + row * sourceStride + (nint)x * BytesPerPixel, rowBytes);
                source.CopyTo(new Span<byte>((byte*)destination + (nint)row * destinationStride + (nint)x * BytesPerPixel, rowBytes));
            }
        }
        finally
        {
            _frameLock.Release();
        }
    }

    unsafe void FreeBuffer()
    {
        if (_buffer != IntPtr.Zero)
        {
            NativeMemory.Free((void*)_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    sealed class FrameReference(VncSession session, VncSize size) : IFramebufferReference
    {
        bool _disposed;

        public IntPtr Address => session._buffer;
        public VncSize Size => size;
        public VncPixelFormat Format => Rgba;
        public double HorizontalDpi => 96;
        public double VerticalDpi => 96;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                session.EndFrame();
            }
        }
    }

    // ---- Input ----

    public void SendKeysym(uint keysym, bool pressed)
    {
        lock (_inputLock)
        {
            // Key repeat sends more downs; a release for a key that isn't down is dropped.
            if (!pressed && !_keysDown.Remove(keysym))
            {
                return;
            }

            if (pressed)
            {
                _keysDown.Add(keysym);
            }
        }

        Send(new KeyEventMessage(pressed, (KeySymbol)keysym));
    }

    /// <summary>VNC has no scancodes; <see cref="RemoteFrameView"/> uses <see cref="SendKeysym"/> instead.</summary>
    public void SendKey(ushort scancode, bool pressed)
    {
    }

    public void ReleaseAllKeys()
    {
        uint[] down;
        lock (_inputLock)
        {
            down = [.. _keysDown];
            _keysDown.Clear();
        }

        foreach (var keysym in down)
        {
            Send(new KeyEventMessage(false, (KeySymbol)keysym));
        }
    }

    public void SendPointerMove(int x, int y)
    {
        MouseButtons buttons;
        lock (_inputLock)
        {
            _pointer = new Position(Math.Max(0, x), Math.Max(0, y));
            buttons = _buttons;
        }

        Send(new PointerEventMessage(_pointer, buttons));
    }

    public void SendPointerButton(PointerButton button, bool pressed)
    {
        var mask = button switch
        {
            PointerButton.Left => MouseButtons.Left,
            PointerButton.Middle => MouseButtons.Middle,
            PointerButton.Right => MouseButtons.Right,
            _ => MouseButtons.None, // RFB has no back/forward buttons.
        };
        if (mask == MouseButtons.None)
        {
            return;
        }

        MouseButtons buttons;
        Position position;
        lock (_inputLock)
        {
            _buttons = pressed ? _buttons | mask : _buttons & ~mask;
            (buttons, position) = (_buttons, _pointer);
        }

        Send(new PointerEventMessage(position, buttons));
    }

    /// <summary>RFB scrolls with button presses, one press and release per notch.</summary>
    public void SendWheel(int units, bool horizontal)
    {
        int notches;
        lock (_inputLock)
        {
            ref int pending = ref horizontal ? ref _wheelHorizontal : ref _wheelVertical;
            pending += units;
            notches = pending / WheelNotch;
            pending -= notches * WheelNotch;
        }

        var button = (horizontal, notches > 0) switch
        {
            (false, true) => MouseButtons.WheelUp,
            (false, false) => MouseButtons.WheelDown,
            (true, true) => MouseButtons.WheelRight,
            (true, false) => MouseButtons.WheelLeft,
        };

        for (int i = 0; i < Math.Abs(notches); i++)
        {
            MouseButtons held;
            Position position;
            lock (_inputLock) (held, position) = (_buttons, _pointer);
            Send(new PointerEventMessage(position, held | button));
            Send(new PointerEventMessage(position, held));
        }
    }

    public void RequestResize(int width, int height)
    {
        // Asking for more would have the server announce a desktop the session then refuses.
        (width, height) = (Math.Min(width, DesktopLimits.MaxSide), Math.Min(height, DesktopLimits.MaxSide));
        if (State != SessionState.Connected || _connection is not { DesktopIsResizable: true } connection
            || (width == _bufferWidth && height == _bufferHeight) || width < 1 || height < 1)
        {
            return;
        }

        Send(new SetDesktopSizeMessage((_, layout) =>
        {
            var screen = layout.FirstOrDefault();
            var resized = new Screen(screen.Id, new MarcusW.VncClient.Rectangle(0, 0, width, height), screen.Flags);
            return (new VncSize(width, height), ImmutableHashSet.Create(resized));
        }));
    }

    void Send<TMessageType>(IOutgoingMessage<TMessageType> message) where TMessageType : class, IOutgoingMessageType
    {
        if (State != SessionState.Connected || _connection is not { } connection)
        {
            return;
        }

        try
        {
            connection.EnqueueMessage(message, _cts.Token);
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or ObjectDisposedException)
        {
            Log.For("Vnc").LogWarning("VNC input dropped: {Message}", e.Message);
        }
    }

    // ---- Clipboard ----

    public void SendClipboard(string text)
    {
        if (_shareClipboard)
        {
            Send(new ClientCutTextMessage(text));
        }
    }

    void IOutputHandler.HandleServerClipboardUpdate(string text)
    {
        if (_shareClipboard)
        {
            RemoteClipboardChanged?.Invoke(this, text);
        }
    }

    void IOutputHandler.RingBell()
    {
    }

    // ---- Lifetime ----

    public async Task DisconnectAsync()
    {
        _cts.Cancel();
        if (_connectTask is not null)
        {
            await _connectTask;
        }

        if (_connection is { } connection)
        {
            connection.ConnectionStateChanged -= OnConnectionStateChanged;
            // The library's Dispose leaves the socket open, and its CloseAsync waits on loops that only end
            // with the socket. Close the socket here; the library's own cleanup then finishes in the background.
            CloseTransport();
            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.CloseAsync().WaitAsync(CloseGracePeriod);
                }
                catch (Exception e)
                {
                    Log.For("Vnc").LogWarning("Closing the VNC connection didn't finish cleanly: {Message}", e.Message);
                }
                finally
                {
                    connection.Dispose();
                }
            });

            _connection = null;
        }

        _route?.Dispose();
        SetState(SessionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _frameLock.WaitAsync();
        FreeBuffer();
        _frameLock.Release();
        _cts.Dispose();
    }

    void SetState(SessionState state, string? message = null, Exception? error = null)
    {
        // A refused desktop size stays failed, even if the library goes on to report the connection as up.
        if (State == state || (State is SessionState.Failed or SessionState.Disconnected && (state != SessionState.Connected || _desktopRefused)))
        {
            return;
        }

        State = state;
        var change = new SessionStateChangedEventArgs(state, message, error);
        LastChange = change;
        StateChanged?.Invoke(this, change);
    }

    ITransport? _transport;

    void CloseTransport()
    {
        try
        {
            Interlocked.Exchange(ref _transport, null)?.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
        }
    }

    void ICursorHandler.UpdateCursor(int hotspotX, int hotspotY, int width, int height, byte[] pixelData, byte[] bitmask, VncPixelFormat pixelFormat) =>
        SetPointerShape(CursorFromPixels(hotspotX, hotspotY, width, height, pixelData, bitmask, pixelFormat));

    void ICursorHandler.UpdateCursorWithAlpha(int hotspotX, int hotspotY, int width, int height, byte[] rgbaData)
    {
        if (rgbaData.Length == width * height * 4)
        {
            RemotePointerShape.Unpremultiply(rgbaData);
        }

        SetPointerShape(RemotePointerShape.Create(width, height, hotspotX, hotspotY, rgbaData));
    }

    void ICursorHandler.UpdateXCursor(int hotspotX, int hotspotY, int width, int height, byte[] primaryColor, byte[] secondaryColor, byte[] bitmap, byte[] bitmask) =>
        SetPointerShape(CursorFromTwoColors(hotspotX, hotspotY, width, height, primaryColor, secondaryColor, bitmap, bitmask));

    void ICursorHandler.HideCursor() => SetPointerShape(RemotePointerShape.Hidden);

    void SetPointerShape(RemotePointerShape? shape)
    {
        if (shape is null)
        {
            Log.For("Vnc").LogDebug("Left out a cursor the server sent");
            return;
        }

        PointerShape = shape;
        PointerShapeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The Cursor pseudo-encoding (RFC 6143 §7.8.1): pixels in the connection's pixel format, then a mask, one bit a
    /// pixel from the top bit, each row whole bytes, where 1 is a pixel shown.
    /// </summary>
    internal static RemotePointerShape? CursorFromPixels(int hotspotX, int hotspotY, int width, int height, byte[] pixels, byte[] mask, VncPixelFormat format)
    {
        int bytesPerPixel = format.BytesPerPixel, maskRow = (width + 7) / 8;
        if (width < 1 || height < 1 || !format.TrueColor || bytesPerPixel is < 1 or > 4
            || pixels.Length < width * height * bytesPerPixel || mask.Length < maskRow * height)
        {
            return null;
        }

        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                uint value = 0;
                for (int b = 0; b < bytesPerPixel; b++)
                {
                    int shift = format.BigEndian ? (bytesPerPixel - 1 - b) * 8 : b * 8;
                    value |= (uint)pixels[i * bytesPerPixel + b] << shift;
                }

                rgba[i * 4] = Channel(value, format.RedShift, format.RedMax);
                rgba[i * 4 + 1] = Channel(value, format.GreenShift, format.GreenMax);
                rgba[i * 4 + 2] = Channel(value, format.BlueShift, format.BlueMax);
                rgba[i * 4 + 3] = Shown(mask, maskRow, x, y) ? (byte)255 : (byte)0;
            }
        }

        return RemotePointerShape.Create(width, height, hotspotX, hotspotY, rgba);
    }

    /// <summary>
    /// The XCursor pseudo-encoding: two RGB colors, then a bitmap (1 picks the first color) and a mask laid out as
    /// <see cref="CursorFromPixels"/>'s.
    /// </summary>
    internal static RemotePointerShape? CursorFromTwoColors(int hotspotX, int hotspotY, int width, int height, byte[] primary, byte[] secondary, byte[] bitmap, byte[] mask)
    {
        int maskRow = (width + 7) / 8;
        if (width < 1 || height < 1 || primary.Length < 3 || secondary.Length < 3 || bitmap.Length < maskRow * height || mask.Length < maskRow * height)
        {
            return null;
        }

        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                var color = Shown(bitmap, maskRow, x, y) ? primary : secondary;
                rgba[i] = color[0];
                rgba[i + 1] = color[1];
                rgba[i + 2] = color[2];
                rgba[i + 3] = Shown(mask, maskRow, x, y) ? (byte)255 : (byte)0;
            }
        }

        return RemotePointerShape.Create(width, height, hotspotX, hotspotY, rgba);
    }

    static bool Shown(byte[] bits, int rowBytes, int x, int y) => (bits[y * rowBytes + x / 8] & (0x80 >> (x % 8))) != 0;

    static byte Channel(uint value, int shift, int max) => max == 0 ? (byte)0 : (byte)(((value >> shift) & (uint)max) * 255 / max);

    /// <summary>
    /// The library's default protocol, but with only the security types Orrery allows, and it remembers the
    /// transport so the session can close it.
    /// </summary>
    sealed class ClosableImplementation(VncSession session)
        : DefaultImplementation(AllowedSecurityTypes, GetDefaultMessageTypes, context => EncodingTypes(context, session))
    {
        // The library's encodings, which already ask for the server's cursor; the session takes it before the first
        // update can bring one.
        static IEnumerable<IEncodingType> EncodingTypes(RfbConnectionContext context, VncSession session)
        {
            context.Connection.CursorHandler = session;
            return GetDefaultEncodingTypes(context);
        }

        // Only None and classic VNC authentication (audit S-2). The library's defaults add types that send the
        // saved username and password in clear text, or accept any TLS certificate, and it lets the server pick
        // the highest it offers. Don't put the defaults back until each type is checked; TLS, VeNCrypt and RA2
        // would first need Orrery's certificate prompt.
        static IEnumerable<ISecurityType> AllowedSecurityTypes(RfbConnectionContext context) =>
            [new NoneSecurityType(context), new VncAuthenticationSecurityType(context)];

        public override ITransportConnector CreateTransportConnector(RfbConnectionContext context) =>
            new Connector(base.CreateTransportConnector(context), session);

        sealed class Connector(ITransportConnector inner, VncSession session) : ITransportConnector
        {
            public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken)
            {
                var transport = new CountedTransport(await inner.ConnectAsync(cancellationToken), session.Link);
                // Through a tunnel the socket only reaches its local end, whose round trip says nothing.
                if (session._route is null && transport.Inner.Stream is System.Net.Sockets.NetworkStream network)
                {
                    session.Link.Socket = network.Socket;
                }

                session._transport = transport;
                return transport;
            }
        }

        /// <summary>The library's transport, with what goes through it counted for the Connection card.</summary>
        sealed class CountedTransport(ITransport inner, LinkStats link) : ITransport
        {
            public ITransport Inner => inner;
            public Stream Stream { get; } = new CountingStream(inner.Stream, link);
            public bool IsEncrypted => inner.IsEncrypted;
            public void Dispose() => inner.Dispose();
        }
    }

    /// <summary>
    /// Answers classic VNC authentication with the password. Anything else asking for it is refused, so the
    /// password only ever leaves as a DES response, even if another security type gets in (audit S-2).
    /// </summary>
    sealed class Authenticator(string password) : IAuthenticationHandler
    {
        public Task<TInput> ProvideAuthenticationInputAsync<TInput>(RfbConnection connection, ISecurityType securityType,
            IAuthenticationInputRequest<TInput> request) where TInput : class, IAuthenticationInput
        {
            if (securityType.GetType() != typeof(VncAuthenticationSecurityType) || typeof(TInput) != typeof(PasswordAuthenticationInput))
            {
                throw new HandshakeFailedException("The server asked for a login method Orrery doesn't support.");
            }

            return Task.FromResult((TInput)(IAuthenticationInput)new PasswordAuthenticationInput(password));
        }
    }
}
