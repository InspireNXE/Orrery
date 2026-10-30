using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Orrery.App.Input;

/// <summary>
/// Windows acts on the Windows key before any app sees it, so pressing it in a remote session also opened
/// the local Start menu. While a session has focus, a low-level keyboard hook takes the two Windows keys
/// away from Windows and hands them to the session instead, as mstsc does. Win+L still locks this computer:
/// Windows handles it below any hook.
/// </summary>
static class WindowsKeyHook
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    // Kept in a field: the hook only holds a function pointer, and a collected delegate would crash.
    static readonly LowLevelKeyboardProc Callback = OnKey;

    static IntPtr _hook;
    static Action<bool, bool>? _target;

    /// <summary>
    /// Sends the Windows keys to <paramref name="target"/> (right key?, pressed?) until <see cref="Release"/>.
    /// Call on the UI thread, which runs the message loop the hook needs.
    /// </summary>
    public static void Capture(Action<bool, bool> target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _target = target;
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, Callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Trace.TraceWarning($"Couldn't capture the Windows key: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }

    /// <summary>Stops capturing, if <paramref name="target"/> is the one capturing.</summary>
    public static void Release(Action<bool, bool> target)
    {
        if (_target != target)
        {
            return;
        }

        _target = null;
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    static IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _target is { } target)
        {
            var key = Marshal.PtrToStructure<KeyboardHookData>(data);
            if (key.VirtualKey is VK_LWIN or VK_RWIN)
            {
                bool? pressed = (int)message switch
                {
                    WM_KEYDOWN or WM_SYSKEYDOWN => true,
                    WM_KEYUP or WM_SYSKEYUP => false,
                    _ => null,
                };

                if (pressed is { } down)
                {
                    target(key.VirtualKey == VK_RWIN, down);
                    return 1; // Swallowed: Windows doesn't see it, so no Start menu.
                }
            }
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
}
