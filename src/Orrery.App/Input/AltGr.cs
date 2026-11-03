using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Input;
using Orrery.App.Sessions;

namespace Orrery.App.Input;

/// <summary>
/// Whether Ctrl+Alt with a key types a character on the keyboard layout in use. Windows sends AltGr as Ctrl+Alt, so
/// AltGr+B, which types { on a Czech, Slovak, Hungarian or Croatian keyboard, arrives as Ctrl+Alt+B. A Ctrl+Alt shortcut
/// then leaves the key alone, so the character reaches the session or page, as <see cref="FullScreenShortcut"/> keeps
/// such keys out of the full-screen shortcut. Elsewhere AltGr is a key of its own, never Ctrl+Alt.
/// </summary>
public static class AltGr
{
    public static bool TypesCharacter(Key key) =>
        OperatingSystem.IsWindows() && FullScreenShortcut.TypesACharacter(key) && TypesCharacterOnWindows(key);

    [SupportedOSPlatform("windows")]
    static bool TypesCharacterOnWindows(Key key)
    {
        if (VirtualKeys.Value.GetValueOrDefault(key) is not (> 0 and var virtualKey))
        {
            return false;
        }

        var layout = GetKeyboardLayout(0);
        var state = new byte[256];
        state[VkControl] = state[VkLeftControl] = state[VkMenu] = state[VkRightMenu] = 0x80;
        var text = new char[8];

        // Asked without changing the keyboard's state, so a dead key pressed next still combines.
        int count = ToUnicodeEx(virtualKey, MapVirtualKeyEx(virtualKey, MapVkToScan, layout), state, text, text.Length, DontChangeKeyboardState, layout);

        // Negative for a dead key, which counts: it types its accent.
        return count < 0 || (count > 0 && !char.IsControl(text[0]));
    }

    /// <summary>Windows' virtual keys by Avalonia key, for the keys that can type a character.</summary>
    [SupportedOSPlatform("windows")]
    static readonly Lazy<Dictionary<Key, uint>> VirtualKeys = new(() =>
    {
        var keys = new Dictionary<Key, uint>();
        for (uint virtualKey = 1; virtualKey < 0xFF; virtualKey++)
        {
            if (WebView2Keys.KeyOf(virtualKey) is { } key)
            {
                keys.TryAdd(key, virtualKey);
            }
        }

        keys.TryAdd(Key.OemBackslash, 0xE2);
        return keys;
    });

    const int VkControl = 0x11, VkMenu = 0x12, VkLeftControl = 0xA2, VkRightMenu = 0xA5;
    const uint MapVkToScan = 0;
    const uint DontChangeKeyboardState = 0x4;

    [DllImport("user32.dll")]
    static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, [Out] char[] text, int size, uint flags, IntPtr layout);
}
