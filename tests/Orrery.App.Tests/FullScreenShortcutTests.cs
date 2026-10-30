using Avalonia.Input;
using Orrery.App.Input;
using Xunit;

namespace Orrery.App.Tests;

public class FullScreenShortcutTests
{
    const KeyModifiers Ctrl = KeyModifiers.Control, Alt = KeyModifiers.Alt, Shift = KeyModifiers.Shift, Meta = KeyModifiers.Meta;

    [Theory]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void The_default_passes_its_own_checks(ShortcutPlatform platform)
    {
        Assert.Null(FullScreenShortcut.Problem(FullScreenShortcut.DefaultFor(platform), platform));
    }

    [Fact]
    public void The_default_off_the_mac_is_ctrl_alt_enter_and_break_still_works_when_chosen()
    {
        Assert.Equal("Ctrl+Alt+Enter", FullScreenShortcut.Describe(FullScreenShortcut.DefaultFor(ShortcutPlatform.Windows), ShortcutPlatform.Windows));
        var ctrlAltBreak = FullScreenShortcut.FromKeyPress(Key.Cancel, Ctrl | Alt)!; // Windows reports Ctrl+Break as Cancel.
        Assert.Null(FullScreenShortcut.Problem(ctrlAltBreak, ShortcutPlatform.Windows));
        Assert.Equal("Ctrl+Alt+Break", FullScreenShortcut.Describe(ctrlAltBreak, ShortcutPlatform.Windows));
        Assert.True(FullScreenShortcut.Matches(ctrlAltBreak, Key.Pause, Ctrl | Alt));
    }

    [Theory]
    [InlineData(Key.F11, KeyModifiers.None)]          // Typed into sessions.
    [InlineData(Key.F11, KeyModifiers.Control)]       // One modifier.
    [InlineData(Key.Enter, KeyModifiers.Alt | KeyModifiers.Shift)] // No Ctrl.
    [InlineData(Key.F, KeyModifiers.Control | KeyModifiers.Meta)]  // Windows key.
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Alt)]   // AltGr+Q types @ on German keyboards.
    [InlineData(Key.D2, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(Key.Delete, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(Key.Escape, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(Key.B, KeyModifiers.Control | KeyModifiers.Alt)]   // Orrery's settings pane.
    [InlineData(Key.C, KeyModifiers.Control | KeyModifiers.Shift)] // Terminal copy.
    [InlineData(Key.LeftCtrl, KeyModifiers.Control | KeyModifiers.Alt)]
    public void Refuses_shortcuts_that_would_get_in_the_way_on_windows(Key key, KeyModifiers modifiers)
    {
        Assert.NotNull(FullScreenShortcut.Problem(new KeyGesture(key, modifiers), ShortcutPlatform.Windows));
    }

    [Theory]
    [InlineData(Key.Enter, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(Key.Home, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(Key.F11, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(Key.F, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)] // Shift takes it out of AltGr's way.
    [InlineData(Key.F11, KeyModifiers.Control | KeyModifiers.Shift)]
    public void Accepts_shortcuts_nothing_else_uses_on_windows(Key key, KeyModifiers modifiers)
    {
        Assert.Null(FullScreenShortcut.Problem(new KeyGesture(key, modifiers), ShortcutPlatform.Windows));
    }

    [Fact]
    public void Console_switching_keys_are_only_refused_on_linux()
    {
        var ctrlAltF2 = new KeyGesture(Key.F2, Ctrl | Alt);
        Assert.Null(FullScreenShortcut.Problem(ctrlAltF2, ShortcutPlatform.Windows));
        Assert.NotNull(FullScreenShortcut.Problem(ctrlAltF2, ShortcutPlatform.Linux));
    }

    [Fact]
    public void Mac_rules_allow_cmd_and_refuse_the_lock_screen_shortcut()
    {
        Assert.Null(FullScreenShortcut.Problem(new KeyGesture(Key.Enter, Ctrl | Meta), ShortcutPlatform.MacOS));
        Assert.NotNull(FullScreenShortcut.Problem(new KeyGesture(Key.Q, Ctrl | Meta), ShortcutPlatform.MacOS));
        Assert.NotNull(FullScreenShortcut.Problem(new KeyGesture(Key.F, Meta), ShortcutPlatform.MacOS));
    }

    [Fact]
    public void Settings_keep_a_chosen_shortcut_and_fall_back_to_the_default_for_anything_else()
    {
        var chosen = new KeyGesture(Key.F11, Ctrl | Shift);
        var stored = FullScreenShortcut.ToSettings(chosen, ShortcutPlatform.Windows);
        Assert.NotNull(stored);
        Assert.True(FullScreenShortcut.SameAs(chosen, FullScreenShortcut.FromSettings(stored, ShortcutPlatform.Windows)));

        Assert.Null(FullScreenShortcut.ToSettings(FullScreenShortcut.DefaultFor(ShortcutPlatform.Windows), ShortcutPlatform.Windows));

        var fallback = FullScreenShortcut.DefaultFor(ShortcutPlatform.Windows);
        Assert.True(FullScreenShortcut.SameAs(fallback, FullScreenShortcut.FromSettings(null, ShortcutPlatform.Windows)));
        Assert.True(FullScreenShortcut.SameAs(fallback, FullScreenShortcut.FromSettings("not a shortcut", ShortcutPlatform.Windows)));
        Assert.True(FullScreenShortcut.SameAs(fallback, FullScreenShortcut.FromSettings("Ctrl+Alt+Delete", ShortcutPlatform.Windows)));
    }
}
