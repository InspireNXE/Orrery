using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Settings;

namespace Orrery.App.Input;

/// <summary>A key the hook takes from Windows for a session.</summary>
enum HookedKey
{
    LeftWindows,
    RightWindows,

    /// <summary>Tab, with Alt held: Alt+Tab and Alt+Shift+Tab.</summary>
    Tab,

    /// <summary>Esc, with Alt or Ctrl held: Alt+Esc and Ctrl+Esc.</summary>
    Escape,
}

/// <summary>
/// Windows acts on some keys before any app sees them, so pressing the Windows key in a remote session also opened
/// the local Start menu, Win+E or Win+R ran locally, and Alt+Tab switched local windows. While a session has focus, a
/// low-level keyboard hook takes those keys away from Windows and hands them to the session instead, as mstsc does:
/// the two Windows keys always, and Alt+Tab, Alt+Esc and Ctrl+Esc as <see cref="Shortcuts"/> says (by default only
/// in full screen, so in a window Alt+Tab still leaves the session). Win+L and Ctrl+Alt+Del stay with this
/// computer: Windows handles them below any hook.
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
    const uint VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_CONTROL = 0x11;
    const uint LLKHF_ALTDOWN = 0x20;

    // Kept in a field: the hook only holds a function pointer, and a collected delegate would crash.
    static readonly LowLevelKeyboardProc Callback = OnKey;
    static readonly object Gate = new();

    // Keys whose press went to the session, so their release does too, whatever is held by then. Hook thread only.
    static readonly HashSet<uint> Taken = [];

    static Thread? _thread;
    static IntPtr _hook;

    // Read on the hook thread, set on the UI thread.
    static volatile Capturer? _capturer;
    static volatile int _shortcuts;

    sealed record Capturer(Action<HookedKey, bool> Target, IntPtr Window, bool FullScreen);

    /// <summary>Where Alt+Tab, Alt+Esc and Ctrl+Esc go while a session captures keys; from the app's settings.</summary>
    public static SystemShortcuts Shortcuts
    {
        get => (SystemShortcuts)_shortcuts;
        set => _shortcuts = (int)value;
    }

    /// <summary>
    /// Sends the hooked keys to <paramref name="target"/> (key, pressed?) on the UI thread, whenever
    /// <paramref name="window"/> is the foreground window, until <see cref="Release"/>. Call again when the window goes
    /// in or out of full screen.
    /// </summary>
    public static void Capture(Action<HookedKey, bool> target, IntPtr window, bool fullScreen)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _capturer = new Capturer(target, window, fullScreen);
        lock (Gate)
        {
            // Installed once, then left in place: an idle hook just passes keys along.
            _thread ??= StartHookThread();
        }
    }

    /// <summary>Stops capturing, if <paramref name="target"/> is the one capturing.</summary>
    public static void Release(Action<HookedKey, bool> target)
    {
        if (_capturer is { } current && current.Target == target)
        {
#pragma warning disable CS0420 // Interlocked works on the volatile field directly.
            Interlocked.CompareExchange(ref _capturer, null, current);
#pragma warning restore CS0420
        }
    }

    /// <summary>Whether Alt+Tab and the rest go to a session in a window or full screen as <paramref name="fullScreen"/> says.</summary>
    internal static bool TakesShortcuts(SystemShortcuts shortcuts, bool fullScreen) => shortcuts switch
    {
        SystemShortcuts.Remote => true,
        SystemShortcuts.Local => false,
        _ => fullScreen,
    };

    /// <summary>
    /// The key a press is for the session, or null to leave it to Windows. <paramref name="altDown"/> and
    /// <paramref name="controlDown"/> say what's held with it.
    /// </summary>
    internal static HookedKey? Take(uint virtualKey, bool altDown, bool controlDown, bool shortcuts) => virtualKey switch
    {
        VK_LWIN => HookedKey.LeftWindows,
        VK_RWIN => HookedKey.RightWindows,
        VK_TAB when shortcuts && altDown => HookedKey.Tab,
        VK_ESCAPE when shortcuts && (altDown || controlDown) => HookedKey.Escape,
        _ => null,
    };

    static HookedKey KeyOf(uint virtualKey) => virtualKey switch
    {
        VK_LWIN => HookedKey.LeftWindows,
        VK_RWIN => HookedKey.RightWindows,
        VK_TAB => HookedKey.Tab,
        _ => HookedKey.Escape,
    };

    static Thread StartHookThread()
    {
        var thread = new Thread(() =>
        {
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, Callback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                Log.For("Input").LogWarning("Couldn't capture the Windows key: {Message}", new Win32Exception(Marshal.GetLastWin32Error()).Message);
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
        bool? pressed = (int)message switch
        {
            WM_KEYDOWN or WM_SYSKEYDOWN => true,
            WM_KEYUP or WM_SYSKEYUP => false,
            _ => null,
        };

        if (code >= 0 && pressed is { } down)
        {
            uint virtualKey = (uint)Marshal.ReadInt32(data);
            var capturer = _capturer;

            // A key whose press the session got gets its release, even if Alt went up first or the window changed.
            if (!down && Taken.Remove(virtualKey))
            {
                if (capturer is not null)
                {
                    var key = KeyOf(virtualKey);
                    var target = capturer.Target;
                    Dispatcher.UIThread.Post(() => target(key, false), DispatcherPriority.Input);
                }

                return 1;
            }

            // Only while Orrery's window is in front: switching away doesn't always reach the view in time.
            if (down && capturer is not null && GetForegroundWindow() == capturer.Window)
            {
                bool altDown = ((uint)Marshal.ReadInt32(data, 8) & LLKHF_ALTDOWN) != 0;
                bool controlDown = (GetAsyncKeyState((int)VK_CONTROL) & 0x8000) != 0;
                if (Take(virtualKey, altDown, controlDown, TakesShortcuts(Shortcuts, capturer.FullScreen)) is { } key)
                {
                    Taken.Add(virtualKey);
                    var target = capturer.Target;
                    Dispatcher.UIThread.Post(() => target(key, true), DispatcherPriority.Input);
                    return 1; // Swallowed: Windows never sees it, so no Start menu, no Win+E and no local Alt+Tab.
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
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
}
