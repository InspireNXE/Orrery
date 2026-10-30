using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MarcusW.VncClient;
using MarcusW.VncClient.Protocol.Implementation.MessageTypes.Outgoing;
using MarcusW.VncClient.Protocol;
using MarcusW.VncClient.Protocol.Implementation;
using MarcusW.VncClient.Protocol.MessageTypes;
using MarcusW.VncClient.Protocol.Services;
using MarcusW.VncClient.Protocol.Implementation.Services.Transports;
using MarcusW.VncClient.Protocol.SecurityTypes;
using MarcusW.VncClient.Output;
using MarcusW.VncClient.Rendering;
using MarcusW.VncClient.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orrery.Core.Sessions;
using VncPixelFormat = MarcusW.VncClient.PixelFormat;
using VncSize = MarcusW.VncClient.Size;

namespace Orrery.Protocols.Vnc;

/// <summary>
/// A VNC session backed by MarcusW.VncClient (MIT). The library decodes straight into our RGBA buffer
/// through <see cref="IRenderTarget"/>; keys go over as X11 keysyms, so the server needs no keyboard layout.
/// </summary>
public sealed class VncSession : IFrameBufferSession, IKeysymInput, IClipboardSession, IRenderTarget, IOutputHandler
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
    MouseButtons _buttons;
    Position _pointer = new(0, 0);
    int _wheelVertical, _wheelHorizontal;
    Task? _connectTask;

    readonly ILoggerFactory _logging;
    readonly bool _shareClipboard;

    VncSession(ConnectionTarget target, string password, bool shareClipboard, ILoggerFactory? logging)
    {
        Target = target;
        _password = password;
        _shareClipboard = shareClipboard;
        _logging = logging ?? NullLoggerFactory.Instance;
    }

    public ConnectionTarget Target { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;
    public int Width => _bufferWidth;
    public int Height => _bufferHeight;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
    public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated;
    public event EventHandler<string>? RemoteClipboardChanged;

    /// <summary>Starts connecting in the background and returns at once; watch <see cref="StateChanged"/>.</summary>
    /// <param name="logging">The VNC library's own diagnostics; none when null.</param>
    /// <param name="shareClipboard">Exchange clipboard text with the server (RFB carries Latin-1 only).</param>
    public static VncSession Start(ConnectionTarget target, string password, bool shareClipboard = true, ILoggerFactory? logging = null)
    {
        var session = new VncSession(target, password, shareClipboard, logging);
        session._connectTask = Task.Run(session.ConnectAsync);
        return session;
    }

    async Task ConnectAsync()
    {
        try
        {
            var client = new VncClient(_logging, new ClosableImplementation(this));
            var parameters = new ConnectParameters
            {
                TransportParameters = new TcpTransportParameters { Host = Target.Host, Port = Target.Port },
                AuthenticationHandler = new Authenticator(Target.Username, _password),
                InitialRenderTarget = this,
                InitialOutputHandler = this,
                AllowSharedConnection = true,

                // A dropped session shows as disconnected, like RDP and SSH; reconnecting is the user's call.
                MaxReconnectAttempts = 0,
            };

            var connection = await client.ConnectAsync(parameters, _cts.Token);
            connection.ConnectionStateChanged += OnConnectionStateChanged;
            _connection = connection;
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
            Trace.TraceError($"VNC session to {Target.Host} failed: {e}");
            SetState(SessionState.Failed, Describe(e));
        }
    }

    static string Describe(Exception e) => e switch
    {
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
                SetState(SessionState.Failed, e.Exception?.Message ?? e.Reason ?? "The connection was lost.");
                break;
            case ConnectionState.Closed:
                SetState(SessionState.Disconnected);
                break;
        }
    }

    // ---- Frame buffer ----

    unsafe IFramebufferReference IRenderTarget.GrabFramebufferReference(VncSize size, IImmutableSet<Screen> layout)
    {
        _frameLock.Wait();
        try
        {
            if (size.Width != _bufferWidth || size.Height != _bufferHeight || _buffer == IntPtr.Zero)
            {
                FreeBuffer();
                _buffer = (IntPtr)NativeMemory.AllocZeroed((nuint)(size.Width * size.Height * BytesPerPixel));
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

            int sourceStride = _bufferWidth * BytesPerPixel;
            for (int row = y; row < bottom; row++)
            {
                var source = new ReadOnlySpan<byte>((byte*)_buffer + row * sourceStride + x * BytesPerPixel, rowBytes);
                source.CopyTo(new Span<byte>((byte*)destination + row * destinationStride + x * BytesPerPixel, rowBytes));
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
            Trace.TraceWarning($"VNC input dropped: {e.Message}");
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
                    Trace.TraceWarning($"Closing the VNC connection didn't finish cleanly: {e.Message}");
                }
                finally
                {
                    connection.Dispose();
                }
            });

            _connection = null;
        }

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

    void SetState(SessionState state, string? message = null)
    {
        if (State == state || (State is SessionState.Failed or SessionState.Disconnected && state != SessionState.Connected))
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
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

    /// <summary>The library's default protocol, but it remembers the transport so the session can close it.</summary>
    sealed class ClosableImplementation(VncSession session) : DefaultImplementation
    {
        public override ITransportConnector CreateTransportConnector(RfbConnectionContext context) =>
            new Connector(base.CreateTransportConnector(context), session);

        sealed class Connector(ITransportConnector inner, VncSession session) : ITransportConnector
        {
            public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken)
            {
                var transport = await inner.ConnectAsync(cancellationToken);
                session._transport = transport;
                return transport;
            }
        }
    }

    /// <summary>Answers the server's login request with the password, plus the username where it asks for one.</summary>
    sealed class Authenticator(string? username, string password) : IAuthenticationHandler
    {
        public Task<TInput> ProvideAuthenticationInputAsync<TInput>(RfbConnection connection, ISecurityType securityType,
            IAuthenticationInputRequest<TInput> request) where TInput : class, IAuthenticationInput
        {
            IAuthenticationInput input = typeof(TInput) == typeof(CredentialsAuthenticationInput)
                ? new CredentialsAuthenticationInput(username ?? string.Empty, password)
                : new PasswordAuthenticationInput(password);
            return Task.FromResult((TInput)input);
        }
    }
}
