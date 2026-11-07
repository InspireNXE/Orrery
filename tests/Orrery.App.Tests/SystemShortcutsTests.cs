using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orrery.App.Input;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Orrery.Core.Settings;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Alt+Tab and Windows' other shortcuts in a remote desktop: full screen only by default, as mstsc does, or as set.</summary>
public class SystemShortcutsTests
{
    const uint Tab = 0x09, Escape = 0x1B, LeftWindows = 0x5B, RightWindows = 0x5C, LetterA = 0x41;

    [Fact]
    public void By_default_the_shortcuts_go_to_the_remote_only_in_full_screen()
    {
        Assert.Equal(SystemShortcuts.FullScreen, new AppSettings().SystemShortcutsGo);
        Assert.True(WindowsKeyHook.TakesShortcuts(SystemShortcuts.FullScreen, fullScreen: true));
        Assert.False(WindowsKeyHook.TakesShortcuts(SystemShortcuts.FullScreen, fullScreen: false));
        Assert.True(WindowsKeyHook.TakesShortcuts(SystemShortcuts.Remote, fullScreen: false));
        Assert.False(WindowsKeyHook.TakesShortcuts(SystemShortcuts.Local, fullScreen: true));
    }

    [Fact]
    public void The_hook_takes_alt_tab_alt_esc_and_ctrl_esc_only_when_shortcuts_go_to_the_remote()
    {
        Assert.Equal(HookedKey.Tab, WindowsKeyHook.Take(Tab, altDown: true, controlDown: false, shortcuts: true));
        Assert.Equal(HookedKey.Escape, WindowsKeyHook.Take(Escape, altDown: true, controlDown: false, shortcuts: true));
        Assert.Equal(HookedKey.Escape, WindowsKeyHook.Take(Escape, altDown: false, controlDown: true, shortcuts: true));
        Assert.Null(WindowsKeyHook.Take(Tab, altDown: true, controlDown: false, shortcuts: false));
        Assert.Null(WindowsKeyHook.Take(Escape, altDown: false, controlDown: true, shortcuts: false));

        // Tab and Esc on their own are ordinary keys, and so is anything else with Alt.
        Assert.Null(WindowsKeyHook.Take(Tab, altDown: false, controlDown: false, shortcuts: true));
        Assert.Null(WindowsKeyHook.Take(Escape, altDown: false, controlDown: false, shortcuts: true));
        Assert.Null(WindowsKeyHook.Take(LetterA, altDown: true, controlDown: true, shortcuts: true));
    }

    [Fact]
    public void The_windows_keys_always_go_to_the_remote()
    {
        Assert.Equal(HookedKey.LeftWindows, WindowsKeyHook.Take(LeftWindows, altDown: false, controlDown: false, shortcuts: false));
        Assert.Equal(HookedKey.RightWindows, WindowsKeyHook.Take(RightWindows, altDown: false, controlDown: false, shortcuts: false));
    }

    [Fact]
    public void The_choice_is_saved_and_read_back()
    {
        var saved = new AppSettings { RemoteSystemShortcuts = SystemShortcuts.Remote };
        var read = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved));
        Assert.Equal(SystemShortcuts.Remote, read!.SystemShortcutsGo);
    }

    sealed class Desktop : IFrameBufferSession
    {
        public List<(ushort Code, bool Pressed)> Keys { get; } = [];
        public ConnectionTarget Target { get; } = new("desk.example", 3389, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<FrameUpdatedEventArgs>? FrameUpdated { add { } remove { } }
        public int Width => 800;
        public int Height => 600;
        public void CopyRegion(FrameRect region, IntPtr destination, int destinationStride) { }
        public void SendKey(ushort scancode, bool pressed) => Keys.Add((scancode, pressed));
        public void ReleaseAllKeys() { }
        public void SendPointerMove(int x, int y) { }
        public void SendPointerButton(PointerButton button, bool pressed) { }
        public void SendWheel(int units, bool horizontal) { }
        public void RequestResize(int width, int height) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public void A_hooked_key_reaches_the_session_as_its_scancode()
    {
        var desktop = new Desktop();
        var view = new RemoteFrameView(desktop);
        var window = new Window { Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        view.OnHookedKey(HookedKey.Tab, true);
        view.OnHookedKey(HookedKey.Tab, false);
        view.OnHookedKey(HookedKey.Escape, true);
        view.OnHookedKey(HookedKey.LeftWindows, true);
        view.OnHookedKey(HookedKey.RightWindows, true);

        Assert.Equal([(0x0F, true), (0x0F, false), (0x01, true), (0xE05B, true), (0xE05C, true)], desktop.Keys);
        window.Close();
    }

    [AvaloniaFact]
    public void A_choice_made_in_the_session_lasts_for_it_and_otherwise_the_saved_one_is_followed()
    {
        var saved = SystemShortcuts.FullScreen;
        var view = new RemoteFrameView(new Desktop()) { Shortcuts = () => saved };
        Assert.Equal(SystemShortcuts.FullScreen, view.SessionShortcuts);

        // Chosen for this session only: the saved choice changing doesn't undo it.
        view.SessionShortcuts = SystemShortcuts.Remote;
        Assert.Equal(SystemShortcuts.Remote, view.SessionShortcuts);
        saved = SystemShortcuts.Local;
        Assert.Equal(SystemShortcuts.Remote, view.SessionShortcuts);
        Assert.Equal(SystemShortcuts.Local, view.SavedShortcuts);

        // Back to what's saved: it follows the saved choice again.
        view.SessionShortcuts = SystemShortcuts.Local;
        saved = SystemShortcuts.FullScreen;
        Assert.Equal(SystemShortcuts.FullScreen, view.SessionShortcuts);
    }
}
