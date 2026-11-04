using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orrery.Host.Platform.Windows;

/// <summary>
/// Finds the window OrreryStreamViewer presents into, by its window class, whether it's a top-level window or a child
/// inside another (an Avalonia window's native control host), and where its client area is on the desktop in physical
/// pixels. Used by <c>--measure</c> to read frame markers off the screen.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe class ViewerWindow
{
    /// <summary>The class OrreryStreamViewer registers for the surface it presents into.</summary>
    public const string ClassName = "OrreryStreamWindow";

    [ThreadStatic]
    static nint _found;

    /// <summary>Makes this process's window coordinates physical pixels, as Desktop Duplication's are.</summary>
    public static void UsePhysicalPixels() => SetProcessDpiAwarenessContext(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

    /// <summary>The viewer's client area on the virtual desktop, or false when no visible viewer surface exists.</summary>
    public static bool TryFindClientArea(out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        _found = 0;
        EnumWindows(&VisitTopLevel, 0);
        nint hwnd = _found;
        if (hwnd == 0 || IsWindowVisible(hwnd) == 0)
        {
            return false;
        }

        Rect client;
        if (GetClientRect(hwnd, &client) == 0)
        {
            return false;
        }

        var origin = new Point();
        if (ClientToScreen(hwnd, &origin) == 0)
        {
            return false;
        }

        (left, top, width, height) = (origin.X, origin.Y, client.Right, client.Bottom);
        return width > 0 && height > 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int VisitTopLevel(nint hwnd, nint parameter)
    {
        if (IsViewer(hwnd))
        {
            _found = hwnd;
            return 0;
        }

        EnumChildWindows(hwnd, &VisitChild, 0);
        return _found == 0 ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static int VisitChild(nint hwnd, nint parameter)
    {
        if (IsViewer(hwnd))
        {
            _found = hwnd;
            return 0;
        }

        return 1;
    }

    static bool IsViewer(nint hwnd)
    {
        char* name = stackalloc char[64];
        int length = GetClassNameW(hwnd, name, 64);
        return new ReadOnlySpan<char>(name, length).SequenceEqual(ClassName);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern nint SetProcessDpiAwarenessContext(nint context);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int EnumChildWindows(nint parent, delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int GetClassNameW(nint hwnd, char* name, int capacity);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int GetClientRect(nint hwnd, Rect* rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int ClientToScreen(nint hwnd, Point* point);
}
