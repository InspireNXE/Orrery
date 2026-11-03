using Avalonia.Input;
using Orrery.App.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class WebView2KeysTests
{
    [Fact]
    public void Windows_keys_become_the_keys_orrery_shortcuts_are_made_of()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(Key.K, WebView2Keys.KeyOf(0x4B));
        Assert.Equal(Key.D1, WebView2Keys.KeyOf(0x31));
        Assert.Equal(Key.F11, WebView2Keys.KeyOf(0x7A));
        Assert.Equal(Key.OemComma, WebView2Keys.KeyOf(0xBC));
        Assert.Equal(Key.Enter, WebView2Keys.KeyOf(0x0D));
        Assert.Equal(Key.Tab, WebView2Keys.KeyOf(0x09));
        // Ctrl on its own isn't a shortcut.
        Assert.Null(WebView2Keys.KeyOf(0x11));
    }
}
