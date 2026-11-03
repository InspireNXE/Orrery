using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Avalonia.Input;

namespace Orrery.App.Sessions;

/// <summary>
/// Orrery's shortcuts while a web page has the keyboard, on Windows. WebView2 takes keys straight from Windows, so
/// Avalonia never sees them; its AcceleratorKeyPressed event, raised for keys with Ctrl or Alt and function keys before
/// the page gets them, is the one place to catch them. Avalonia.Controls.WebView doesn't offer it, so it's reached
/// through the controller the package hands over. Each such key is offered to Orrery as an ordinary key press, and kept
/// from the page only when Orrery used it: Ctrl+C, Ctrl+F and the rest still reach the page.
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe partial class WebView2Keys
{
    static readonly Guid ControllerId = new("4d00c0d1-9434-4eb6-8078-8697a560334f");
    static readonly StrategyBasedComWrappers Wrappers = new();

    // ICoreWebView2Controller: IUnknown's three, then IsVisible, Bounds, ZoomFactor, ZoomFactorChanged,
    // SetBoundsAndZoomFactor, MoveFocus, MoveFocusRequested, GotFocus and LostFocus come before it.
    const int AddAcceleratorKeyPressed = 19;
    const int RemoveAcceleratorKeyPressed = 20;

    // ICoreWebView2AcceleratorKeyPressedEventArgs.
    const int GetKeyEventKind = 3;
    const int GetVirtualKey = 4;
    const int PutHandled = 8;

    const int KeyDown = 0;
    const int SystemKeyDown = 2;

    /// <summary>
    /// Hands each shortcut key to <paramref name="offer"/>, which says whether Orrery used it. Disposing stops it.
    /// Null when the controller can't be reached.
    /// </summary>
    public static IDisposable? Watch(IntPtr controller, Func<Key, KeyModifiers, bool> offer)
    {
        if (controller == IntPtr.Zero || Marshal.QueryInterface(controller, in ControllerId, out var keyed) != 0)
        {
            return null;
        }

        var handler = new Handler(offer);
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
        try
        {
            var id = typeof(IAcceleratorKeyPressedHandler).GUID;
            if (Marshal.QueryInterface(unknown, in id, out var callback) != 0)
            {
                Marshal.Release(keyed);
                return null;
            }

            long token;
            int result = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(keyed, AddAcceleratorKeyPressed))(keyed, callback, &token);
            Marshal.Release(callback);
            if (result != 0)
            {
                Marshal.Release(keyed);
                return null;
            }

            return new Subscription(keyed, token);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    static void* Slot(IntPtr instance, int index) => (*(void***)instance)[index];

    sealed class Subscription(IntPtr controller, long token) : IDisposable
    {
        IntPtr _controller = controller;

        public void Dispose()
        {
            if (_controller == IntPtr.Zero)
            {
                return;
            }

            // The controller may already be closed with its page; the call then fails, which is fine.
            ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(_controller, RemoveAcceleratorKeyPressed))(_controller, token);
            Marshal.Release(_controller);
            _controller = IntPtr.Zero;
        }
    }

    [GeneratedComClass]
    sealed partial class Handler(Func<Key, KeyModifiers, bool> offer) : IAcceleratorKeyPressedHandler
    {
        public int Invoke(IntPtr sender, IntPtr args)
        {
            int kind;
            uint virtualKey;
            if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, GetKeyEventKind))(args, &kind) != 0
                || kind is not (KeyDown or SystemKeyDown)
                || ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(args, GetVirtualKey))(args, &virtualKey) != 0
                || KeyOf(virtualKey) is not { } key)
            {
                return 0;
            }

            if (offer(key, Modifiers()))
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(args, PutHandled))(args, 1);
            }

            return 0;
        }
    }

    static KeyModifiers Modifiers()
    {
        var modifiers = KeyModifiers.None;
        if (Down(0x11)) modifiers |= KeyModifiers.Control;
        if (Down(0x10)) modifiers |= KeyModifiers.Shift;
        if (Down(0x12)) modifiers |= KeyModifiers.Alt;
        if (Down(0x5B) || Down(0x5C)) modifiers |= KeyModifiers.Meta;
        return modifiers;
    }

    static bool Down(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>The Windows virtual key as Avalonia's key, for the keys a shortcut can be made of.</summary>
    internal static Key? KeyOf(uint virtualKey) => virtualKey switch
    {
        >= 0x41 and <= 0x5A => Key.A + (int)(virtualKey - 0x41),
        >= 0x30 and <= 0x39 => Key.D0 + (int)(virtualKey - 0x30),
        >= 0x60 and <= 0x69 => Key.NumPad0 + (int)(virtualKey - 0x60),
        >= 0x70 and <= 0x87 => Key.F1 + (int)(virtualKey - 0x70),
        0x08 => Key.Back,
        0x09 => Key.Tab,
        0x0D => Key.Enter,
        0x1B => Key.Escape,
        0x20 => Key.Space,
        0x21 => Key.PageUp,
        0x22 => Key.PageDown,
        0x23 => Key.End,
        0x24 => Key.Home,
        0x25 => Key.Left,
        0x26 => Key.Up,
        0x27 => Key.Right,
        0x28 => Key.Down,
        0x2D => Key.Insert,
        0x2E => Key.Delete,
        0x6B => Key.Add,
        0x6D => Key.Subtract,
        0xBA => Key.OemSemicolon,
        0xBB => Key.OemPlus,
        0xBC => Key.OemComma,
        0xBD => Key.OemMinus,
        0xBE => Key.OemPeriod,
        0xBF => Key.OemQuestion,
        0xC0 => Key.OemTilde,
        0xDB => Key.OemOpenBrackets,
        0xDC => Key.OemPipe,
        0xDD => Key.OemCloseBrackets,
        0xDE => Key.OemQuotes,
        _ => null,
    };

    [DllImport("user32.dll")]
    static extern short GetKeyState(int virtualKey);
}

/// <summary>ICoreWebView2AcceleratorKeyPressedEventHandler.</summary>
[GeneratedComInterface]
[Guid("b29c7e28-fa79-41a8-8e44-65811c76dcb2")]
partial interface IAcceleratorKeyPressedHandler
{
    [PreserveSig]
    int Invoke(IntPtr sender, IntPtr args);
}
