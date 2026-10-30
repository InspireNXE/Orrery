using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Orrery.App.Input;

/// <summary>
/// Windows acts on the Windows key before any app sees it, so pressing it in a remote session also opened
/// the local Start menu, and Win+E or Win+R ran locally. While a session has focus, a low-level keyboard hook
/// takes the two Windows keys away from Windows and hands them to the session instead, as mstsc does.
/// Win+L still locks this computer: Windows handles it below any hook.
/// <para>
/// Windows gives a hook only a fraction of a second. A slow one is skipped, so the key reaches Windows after
/// all, and repeated timeouts remove the hook. So the hook runs on its own thread with its own message loop,
/// never waits for the UI or a session, and passes keys on through the dispatcher.
/// </para>
/// </summary>
static class WindowsKeyHook
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    // Kept in a field: the hook only holds a function pointer, and a collected delegate would crash.
    static readonly LowLevelKeyboardProc Callback = OnKey;
    static readonly object Gate = new();

    static Thread? _thread;
    static IntPtr _hook;

    // Read on the hook thread, set on the UI thread.
    static volatile Action<bool, bool>? _target;

    /// <summary>Sends the Windows keys to <paramref name="target"/> (right key?, pressed?) on the UI thread until <see cref="Release"/>.</summary>
    public static void Capture(Action<bool, bool> target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _target = target;
        lock (Gate)
        {
            // Installed once, then left in place: an idle hook just passes keys along.
            _thread ??= StartHookThread();
        }
    }

    /// <summary>Stops capturing, if <paramref name="target"/> is the one capturing.</summary>
    public static void Release(Action<bool, bool> target)
    {
#pragma warning disable CS0420 // Interlocked works on the volatile field directly.
        Interlocked.CompareExchange(ref _target, null, target);
#pragma warning restore CS0420
    }

    static Thread StartHookThread()
    {
        var thread = new Thread(() =>
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, Callback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Trace.TraceWarning($"Couldn't capture the Windows key: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                return;
            }

            // A low-level hook is called through this thread's message loop.
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        })
        {
            IsBackground = true,
            Name = "Windows key hook",
            Priority = ThreadPriority.Highest,
        };
        thread.Start();
        return thread;
    }

    static IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _target is { } target)
        {
            uint virtualKey = (uint)Marshal.ReadInt32(data);
            if (virtualKey is VK_LWIN or VK_RWIN)
            {
                bool? pressed = (int)message switch
                {
                    WM_KEYDOWN or WM_SYSKEYDOWN => true,
                    WM_KEYUP or WM_SYSKEYUP => false,
                    _ => null,
                };

                if (pressed is { } down)
                {
                    bool right = virtualKey == VK_RWIN;
                    Dispatcher.UIThread.Post(() => target(right, down), DispatcherPriority.Input);
                    return 1; // Swallowed: Windows never sees it, so no Start menu and no Win+E.
                }
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }

    delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    struct Message
    {
        public IntPtr Window;
        public uint Id;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
}
