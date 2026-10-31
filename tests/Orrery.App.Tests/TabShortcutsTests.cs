using Avalonia.Input;
using Orrery.App.Input;
using Xunit;

namespace Orrery.App.Tests;

public class TabShortcutsTests
{
    const KeyModifiers Ctrl = KeyModifiers.Control;

    static TabCommand? Match(Key key, KeyModifiers modifiers, FocusPlace place = FocusPlace.App, bool mac = false) =>
        TabShortcuts.Match(key, modifiers, place, mac);

    [Fact]
    public void Ctrl_alt_page_keys_switch_tabs_even_from_a_remote_desktop()
    {
        Assert.Equal(new TabCommand(TabCommandKind.Next), Match(Key.PageDown, Ctrl | KeyModifiers.Alt, FocusPlace.Desktop));
        Assert.Equal(new TabCommand(TabCommandKind.Previous), Match(Key.PageUp, Ctrl | KeyModifiers.Alt, FocusPlace.Desktop));
    }

    [Theory]
    [InlineData(Key.Tab, Ctrl)]
    [InlineData(Key.Tab, Ctrl | KeyModifiers.Shift)]
    [InlineData(Key.PageDown, Ctrl)]
    [InlineData(Key.D1, Ctrl)]
    [InlineData(Key.F4, Ctrl)]
    [InlineData(Key.W, Ctrl)]
    public void A_remote_desktop_keeps_the_browser_style_keys(Key key, KeyModifiers modifiers)
    {
        Assert.Null(Match(key, modifiers, FocusPlace.Desktop));
        Assert.NotNull(Match(key, modifiers, FocusPlace.App));
    }

    [Fact]
    public void A_terminal_gives_up_ctrl_tab_but_keeps_ctrl_w_for_the_shell()
    {
        Assert.Equal(new TabCommand(TabCommandKind.Next), Match(Key.Tab, Ctrl, FocusPlace.Terminal));
        Assert.Equal(new TabCommand(TabCommandKind.Close), Match(Key.F4, Ctrl, FocusPlace.Terminal));
        Assert.Null(Match(Key.W, Ctrl, FocusPlace.Terminal));
        Assert.Equal(new TabCommand(TabCommandKind.Close), Match(Key.W, Ctrl, FocusPlace.App));
    }

    [Fact]
    public void Number_keys_pick_a_tab_and_nine_is_the_last()
    {
        Assert.Equal(new TabCommand(TabCommandKind.Select, 0), Match(Key.D1, Ctrl));
        Assert.Equal(new TabCommand(TabCommandKind.Select, 7), Match(Key.D8, Ctrl));
        Assert.Equal(new TabCommand(TabCommandKind.Select, -1), Match(Key.D9, Ctrl));
        Assert.Null(Match(Key.D0, Ctrl));
        Assert.Null(Match(Key.D1, Ctrl | KeyModifiers.Shift));
    }

    [Fact]
    public void On_macos_cmd_stands_in_for_ctrl_except_in_ctrl_tab()
    {
        Assert.Equal(new TabCommand(TabCommandKind.Select, 1), Match(Key.D2, KeyModifiers.Meta, mac: true));
        Assert.Null(Match(Key.D2, Ctrl, mac: true));
        Assert.Equal(new TabCommand(TabCommandKind.Next), Match(Key.Tab, Ctrl, mac: true));
    }

    [Fact]
    public void Next_and_previous_wrap_around_and_missing_tabs_go_nowhere()
    {
        Assert.Equal(0, TabShortcuts.Target(new TabCommand(TabCommandKind.Next), 2, 3));
        Assert.Equal(2, TabShortcuts.Target(new TabCommand(TabCommandKind.Previous), 0, 3));
        Assert.Equal(2, TabShortcuts.Target(new TabCommand(TabCommandKind.Select, -1), 0, 3));
        Assert.Null(TabShortcuts.Target(new TabCommand(TabCommandKind.Select, 5), 0, 3));
        Assert.Null(TabShortcuts.Target(new TabCommand(TabCommandKind.Next), 0, 0));
    }

    [Theory]
    [InlineData(0, 10, 0)]   // Dragged left of everything.
    [InlineData(0, 160, 1)]  // First tab dropped past the second's middle.
    [InlineData(0, 500, 2)]  // Past the end.
    [InlineData(2, 10, 0)]
    [InlineData(1, 120, 1)]  // Barely moved: stays.
    public void A_dragged_tab_lands_after_the_tabs_whose_middle_it_passed(int dragged, double x, int expected)
    {
        double[] middles = [50, 150, 250];
        Assert.Equal(expected, TabShortcuts.DropIndex(middles, dragged, x));
    }
}
