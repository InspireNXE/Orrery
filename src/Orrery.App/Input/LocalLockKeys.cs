using System.Runtime.InteropServices;
using Orrery.Core.Sessions;

namespace Orrery.App.Input;

/// <summary>
/// Which lock keys are on at this computer, for setting a remote desktop's to match. Windows and macOS say; Linux has no
/// way for an app to ask under Wayland, so it's null there and the remote's lock keys are left as they are.
/// </summary>
static class LocalLockKeys
{
    const int VkCapital = 0x14, VkNumLock = 0x90, VkScroll = 0x91;

    // kCGEventSourceStateCombinedSessionState and kCGEventFlagMaskAlphaShift.
    const int CombinedSessionState = 0;
    const ulong AlphaShift = 0x00010000;

    public static LockKeys? Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var keys = LockKeys.None;
                if ((GetKeyState(VkCapital) & 1) != 0) keys |= LockKeys.CapsLock;
                if ((GetKeyState(VkNumLock) & 1) != 0) keys |= LockKeys.NumLock;
                if ((GetKeyState(VkScroll) & 1) != 0) keys |= LockKeys.ScrollLock;
                return keys;
            }

            if (OperatingSystem.IsMacOS())
            {
                // A Mac has no Num Lock: its keypad types digits, which on Windows is Num Lock on.
                return LockKeys.NumLock | ((CGEventSourceFlagsState(CombinedSessionState) & AlphaShift) != 0 ? LockKeys.CapsLock : LockKeys.None);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        return null;
    }

    [DllImport("user32.dll")]
    static extern short GetKeyState(int virtualKey);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    static extern ulong CGEventSourceFlagsState(int stateId);
}
