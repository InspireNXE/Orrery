using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Protocols.Stream.Windows;

/// <summary>
/// A bare Win32 window for a stream to be presented into: phase 1's stand-in for the popout (docs/design-stream.md
/// §5, §8), either top-level with no Avalonia in the way, or a child inside another window (Avalonia's native control
/// host). Create a top-level one and run <see cref="RunMessageLoop"/> on the same thread; present into
/// <see cref="Handle"/> from any thread.
/// </summary>
[SupportedOSPlatform("windows")]
sealed unsafe class StreamWindow
{
    /// <summary>The window class, which OrreryHost's <c>--measure</c> looks for.</summary>
    public const string ClassName = "OrreryStreamWindow";

    static bool _registered;

    StreamWindow(nint handle) => Handle = handle;

    public nint Handle { get; }

    /// <summary>
    /// Opens a window whose client area is <paramref name="width"/> x <paramref name="height"/> physical pixels. The
    /// process is made per-monitor DPI aware first, so Windows doesn't stretch a blurred bitmap of it.
    /// </summary>
    public static StreamWindow Open(string title, int width, int height)
    {
        SetProcessDpiAwarenessContext(Win.DpiAwarenessPerMonitorV2);
        nint instance = Register();
        fixed (char* className = ClassName)
        {
            var rect = new Rect { Right = width, Bottom = height };
            AdjustWindowRect(&rect, Win.WsOverlappedWindow, 0);
            fixed (char* text = title)
            {
                nint handle = CreateWindowExW(0, className, text, Win.WsOverlappedWindow, Win.CwUseDefault, Win.CwUseDefault,
                    rect.Right - rect.Left, rect.Bottom - rect.Top, 0, 0, instance, 0);
                if (handle == 0)
                {
                    throw new COMException("CreateWindowExW failed", Marshal.GetHRForLastWin32Error());
                }

                ShowWindow(handle, Win.SwShowNormal);
                return new StreamWindow(handle);
            }
        }
    }

    /// <summary>
    /// Creates a child window filling none of <paramref name="parent"/> yet: the parent's owner (Avalonia's native
    /// control host) sizes and shows it. Must be called on the parent's thread; destroy it with <see cref="Destroy"/>.
    /// </summary>
    public static StreamWindow CreateChild(nint parent)
    {
        nint instance = Register();
        fixed (char* className = ClassName)
        {
            nint handle = CreateWindowExW(0, className, null, Win.WsChild | Win.WsVisible | Win.WsClipSiblings, 0, 0, 1, 1, parent, 0, instance, 0);
            return handle != 0 ? new StreamWindow(handle) : throw new COMException("CreateWindowExW failed", Marshal.GetHRForLastWin32Error());
        }
    }

    public void Destroy() => DestroyWindow(Handle);

    static nint Register()
    {
        nint instance = GetModuleHandleW(null);
        if (_registered)
        {
            return instance;
        }

        fixed (char* className = ClassName)
        {
            var windowClass = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                Style = Win.CsHRedrawVRedraw,
                WndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WindowProc,
                Instance = instance,
                Cursor = LoadCursorW(0, Win.IdcArrow),
                ClassName = (nint)className,
            };
            if (RegisterClassExW(&windowClass) == 0)
            {
                throw new COMException("RegisterClassExW failed", Marshal.GetHRForLastWin32Error());
            }
        }

        _registered = true;
        return instance;
    }

    /// <summary>Sets the title bar; safe from any thread while the message loop runs.</summary>
    public void SetTitle(string title)
    {
        fixed (char* text = title)
        {
            SetWindowTextW(Handle, text);
        }
    }

    /// <summary>Asks the window to close, as its close button does; safe from any thread.</summary>
    public void Close() => PostMessageW(Handle, Win.WmClose, 0, 0);

    /// <summary>Runs this thread's message loop until the window is closed.</summary>
    public static void RunMessageLoop()
    {
        byte* message = stackalloc byte[Win.MsgSize];
        while (GetMessageW(message, 0, 0, 0) > 0)
        {
            TranslateMessage(message);
            DispatchMessageW(message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        // Closing a top-level stream window ends its message loop; a child's parent owns the loop.
        if (message == Win.WmDestroy && GetParent(hwnd) == 0)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint SetProcessDpiAwarenessContext(nint context);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern nint GetModuleHandleW(char* name);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint LoadCursorW(nint instance, nint name);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    static extern ushort RegisterClassExW(WndClassEx* windowClass);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int AdjustWindowRect(Rect* rect, uint style, int menu);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    static extern nint CreateWindowExW(uint exStyle, char* className, char* title, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int SetWindowTextW(nint hwnd, char* text);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int GetMessageW(byte* message, nint hwnd, uint min, uint max);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int TranslateMessage(byte* message);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint DispatchMessageW(byte* message);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int DestroyWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int PostMessageW(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);
}
