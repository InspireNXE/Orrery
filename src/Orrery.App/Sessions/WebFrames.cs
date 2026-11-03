using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Sessions;
using SkiaSharp;

namespace Orrery.App.Sessions;

/// <summary>
/// A web page's picture, for recording it, on Windows. Chromium's screencast (the DevTools protocol's
/// Page.startScreencast, reached through WebView2) sends a JPEG each time the page changes; it runs only while
/// something listens to <see cref="FrameUpdated"/>, so a page that isn't being recorded costs nothing. WebKit on Linux
/// and macOS has nothing like it, only slow one-off snapshots, so pages there aren't recorded.
/// </summary>
sealed unsafe partial class WebFrames
{
    static readonly ILogger Logger = Log.For("Recording");
    static readonly Guid WebViewId = new("76eceacb-0462-4d94-ac83-423a6793775e");
    static readonly StrategyBasedComWrappers Wrappers = new();

    // ICoreWebView2, and the DevTools event receiver it hands out.
    const int CallDevToolsProtocolMethod = 36;
    const int GetDevToolsProtocolEventReceiver = 42;
    const int AddEventReceived = 3;
    const int RemoveEventReceived = 4;

    // ICoreWebView2DevToolsProtocolEventReceivedEventArgs.
    const int GetParameterObjectAsJson = 3;

    readonly Lock _lock = new();
    byte[] _pixels = [];
    int _width, _height;
    EventHandler<FrameUpdatedEventArgs>? _frameUpdated;
    int _listeners;

    IntPtr _webView;
    IntPtr _receiver;
    long _token;
    bool _casting;

    /// <summary>Whether this computer can record pages: Windows, where the engine is WebView2.</summary>
    public static bool Supported => OperatingSystem.IsWindows();

    public int Width
    {
        get
        {
            lock (_lock)
            {
                return _width;
            }
        }
    }

    public int Height
    {
        get
        {
            lock (_lock)
            {
                return _height;
            }
        }
    }

    /// <summary>A new picture of the page; raised on a background thread. Listening starts the screencast.</summary>
    public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated
    {
        add
        {
            bool first;
            lock (_lock)
            {
                _frameUpdated += value;
                first = ++_listeners == 1;
            }

            if (first)
            {
                Dispatcher.UIThread.Post(Start);
            }
        }
        remove
        {
            bool last;
            lock (_lock)
            {
                _frameUpdated -= value;
                last = --_listeners == 0;
            }

            if (last)
            {
                Dispatcher.UIThread.Post(Stop);
            }
        }
    }

    /// <summary>Copies a region of the latest picture, as RGBA32.</summary>
    public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride)
    {
        lock (_lock)
        {
            if (region.IsEmpty || region.Right > _width || region.Bottom > _height)
            {
                return;
            }

            for (int row = 0; row < region.Height; row++)
            {
                Marshal.Copy(_pixels, ((region.Y + row) * _width + region.X) * 4, destination + row * destinationStride, region.Width * 4);
            }
        }
    }

    /// <summary>The page's engine is ready: <paramref name="coreWebView2"/> is WebView2's ICoreWebView2.</summary>
    public void Connect(IntPtr coreWebView2)
    {
        Disconnect();
        if (coreWebView2 == IntPtr.Zero || Marshal.QueryInterface(coreWebView2, in WebViewId, out _webView) != 0)
        {
            _webView = IntPtr.Zero;
            return;
        }

        bool listening;
        lock (_lock)
        {
            listening = _listeners > 0;
        }

        if (listening)
        {
            Start();
        }
    }

    /// <summary>The engine has gone, with its page.</summary>
    public void Disconnect()
    {
        Stop();
        if (_webView != IntPtr.Zero)
        {
            Marshal.Release(_webView);
            _webView = IntPtr.Zero;
        }
    }

    void Start()
    {
        if (_casting || _webView == IntPtr.Zero || !OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_lock)
        {
            if (_listeners == 0)
            {
                return;
            }
        }

        IntPtr receiver;
        fixed (char* name = "Page.screencastFrame")
        {
            if (((delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)Slot(_webView, GetDevToolsProtocolEventReceiver))(_webView, name, &receiver) != 0)
            {
                Logger.LogWarning("Couldn't record the page: WebView2 has no screencast");
                return;
            }
        }

        var callback = Interface<IDevToolsEventHandler>(new FrameHandler(this));
        long token;
        int result = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(receiver, AddEventReceived))(receiver, callback, &token);
        Marshal.Release(callback);
        if (result != 0)
        {
            Marshal.Release(receiver);
            return;
        }

        _receiver = receiver;
        _token = token;
        _casting = true;
        Call("Page.startScreencast", """{"format":"jpeg","quality":80}""");
    }

    void Stop()
    {
        if (!_casting)
        {
            return;
        }

        _casting = false;
        Call("Page.stopScreencast", "{}");
        ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(_receiver, RemoveEventReceived))(_receiver, _token);
        Marshal.Release(_receiver);
        _receiver = IntPtr.Zero;
    }

    void Call(string method, string parameters)
    {
        if (_webView == IntPtr.Zero)
        {
            return;
        }

        var callback = Interface<IDevToolsCallCompletedHandler>(new CallCompleted());
        fixed (char* m = method, p = parameters)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, char*, char*, IntPtr, int>)Slot(_webView, CallDevToolsProtocolMethod))(_webView, m, p, callback);
        }

        Marshal.Release(callback);
    }

    /// <summary>A frame arrived: it's acknowledged at once, so the next can come, and decoded away from the UI.</summary>
    void OnFrame(string json)
    {
        string data;
        int session;
        try
        {
            using var document = JsonDocument.Parse(json);
            data = document.RootElement.GetProperty("data").GetString() ?? string.Empty;
            session = document.RootElement.GetProperty("sessionId").GetInt32();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return;
        }

        Call("Page.screencastFrameAck", $$"""{"sessionId":{{session}}}""");
        _ = Task.Run(() => Decode(data));
    }

    void Decode(string base64)
    {
        byte[] jpeg;
        try
        {
            jpeg = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return;
        }

        using var codec = SKCodec.Create(new SKMemoryStream(jpeg));
        if (codec is null)
        {
            return;
        }

        var info = codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Opaque);
        var pixels = new byte[info.BytesSize];
        fixed (byte* target = pixels)
        {
            if (codec.GetPixels(info, (IntPtr)target) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                return;
            }
        }

        EventHandler<FrameUpdatedEventArgs>? listeners;
        bool resized;
        lock (_lock)
        {
            resized = info.Width != _width || info.Height != _height;
            _pixels = pixels;
            _width = info.Width;
            _height = info.Height;
            listeners = _frameUpdated;
        }

        listeners?.Invoke(this, new FrameUpdatedEventArgs(new FrameRect(0, 0, info.Width, info.Height), resized));
    }

    static IntPtr Interface<T>(object handler)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
        var id = typeof(T).GUID;
        Marshal.QueryInterface(unknown, in id, out var pointer);
        Marshal.Release(unknown);
        return pointer;
    }

    static void* Slot(IntPtr instance, int index) => (*(void***)instance)[index];

    [GeneratedComClass]
    sealed partial class FrameHandler(WebFrames frames) : IDevToolsEventHandler
    {
        public int Invoke(IntPtr sender, IntPtr args)
        {
            IntPtr json;
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(args, GetParameterObjectAsJson))(args, &json) != 0 || json == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                frames.OnFrame(Marshal.PtrToStringUni(json) ?? string.Empty);
            }
            finally
            {
                Marshal.FreeCoTaskMem(json);
            }

            return 0;
        }
    }

    [GeneratedComClass]
    sealed partial class CallCompleted : IDevToolsCallCompletedHandler
    {
        public int Invoke(int errorCode, IntPtr returnObjectAsJson) => 0;
    }
}

/// <summary>ICoreWebView2DevToolsProtocolEventReceivedEventHandler.</summary>
[GeneratedComInterface]
[Guid("e2fda4be-5456-406c-a261-3d452138362c")]
partial interface IDevToolsEventHandler
{
    [PreserveSig]
    int Invoke(IntPtr sender, IntPtr args);
}

/// <summary>ICoreWebView2CallDevToolsProtocolMethodCompletedHandler.</summary>
[GeneratedComInterface]
[Guid("5c4889f0-5ef6-4c5a-952c-d8f1b92d0574")]
partial interface IDevToolsCallCompletedHandler
{
    [PreserveSig]
    int Invoke(int errorCode, IntPtr returnObjectAsJson);
}
