using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Orrery.App.Credentials;

/// <summary>
/// Puts a password on the clipboard for a short while. On Windows it's marked so clipboard history (Win+V), cloud
/// clipboard and clipboard managers that honor the marks leave it out; everywhere it's cleared after
/// <see cref="ClearAfter"/> unless something else has been copied since.
/// </summary>
static class SecretClipboard
{
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);

    /// <summary>Copies <paramref name="secret"/>; false when the clipboard couldn't be had.</summary>
    public static async Task<bool> CopyAsync(TopLevel owner, string secret)
    {
        if (OperatingSystem.IsWindows() && owner.TryGetPlatformHandle()?.Handle is { } window && window != IntPtr.Zero)
        {
            if (!CopyWindows(window, secret))
            {
                return false;
            }

            uint copied = GetClipboardSequenceNumber();
            _ = Task.Delay(ClearAfter).ContinueWith(_ =>
            {
                if (GetClipboardSequenceNumber() == copied && OpenClipboard(window))
                {
                    EmptyClipboard();
                    CloseClipboard();
                }
            }, TaskScheduler.Default);
            return true;
        }

        if (owner.Clipboard is not { } clipboard)
        {
            return false;
        }

        await clipboard.SetTextAsync(secret);
        _ = Task.Delay(ClearAfter).ContinueWith(async _ =>
        {
            if (await clipboard.TryGetTextAsync() == secret)
            {
                await clipboard.ClearAsync();
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return true;
    }

    [SupportedOSPlatform("windows")]
    static bool CopyWindows(IntPtr window, string secret)
    {
        if (!OpenClipboard(window))
        {
            return false;
        }

        try
        {
            EmptyClipboard();
            if (!Put(13 /* CF_UNICODETEXT */, MemoryOf(secret)))
            {
                return false;
            }

            // Each is a DWORD; zero says no.
            Put(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), MemoryOf(0));
            Put(RegisterClipboardFormat("CanIncludeInClipboardHistory"), MemoryOf(0));
            Put(RegisterClipboardFormat("CanUploadToCloudClipboard"), MemoryOf(0));
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Hands <paramref name="memory"/> to the clipboard, which owns it once this succeeds.</summary>
    static bool Put(uint format, IntPtr memory)
    {
        if (memory == IntPtr.Zero)
        {
            return false;
        }

        if (SetClipboardData(format, memory) == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }

        return true;
    }

    static IntPtr MemoryOf(string text)
    {
        var bytes = (text.Length + 1) * 2;
        var memory = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (UIntPtr)bytes);
        if (memory == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var target = GlobalLock(memory);
        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
        Marshal.WriteInt16(target, text.Length * 2, 0);
        GlobalUnlock(memory);
        return memory;
    }

    static IntPtr MemoryOf(int value)
    {
        var memory = GlobalAlloc(0x0002, 4);
        if (memory != IntPtr.Zero)
        {
            Marshal.WriteInt32(GlobalLock(memory), value);
            GlobalUnlock(memory);
        }

        return memory;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalFree(IntPtr memory);
}
