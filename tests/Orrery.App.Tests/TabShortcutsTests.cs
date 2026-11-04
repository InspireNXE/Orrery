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
    public void Ctrl_shift_t_opens_a_local_terminal_except_from_a_remote_desktop()
    {
        Assert.True(TabShortcuts.OpensLocalTerminal(Key.T, Ctrl | KeyModifiers.Shift, FocusPlace.App, mac: false));
        Assert.True(TabShortcuts.OpensLocalTerminal(Key.T, Ctrl | KeyModifiers.Shift, FocusPlace.Terminal, mac: false));
        Assert.False(TabShortcuts.OpensLocalTerminal(Key.T, Ctrl | KeyModifiers.Shift, FocusPlace.Desktop, mac: false));
        Assert.False(TabShortcuts.OpensLocalTerminal(Key.T, Ctrl, FocusPlace.App, mac: false)); // Shells use Ctrl+T.
        Assert.True(TabShortcuts.OpensLocalTerminal(Key.T, KeyModifiers.Meta | KeyModifiers.Shift, FocusPlace.Terminal, mac: true));
        Assert.False(TabShortcuts.OpensLocalTerminal(Key.T, Ctrl | KeyModifiers.Shift, FocusPlace.App, mac: true));
    }

    [Fact]
    public void Ctrl_shift_o_pops_a_tab_out_except_from_a_remote_desktop()
    {
        Assert.True(TabShortcuts.PopsOut(Key.O, Ctrl | KeyModifiers.Shift, FocusPlace.App, mac: false));
        Assert.True(TabShortcuts.PopsOut(Key.O, Ctrl | KeyModifiers.Shift, FocusPlace.Terminal, mac: false));
        Assert.False(TabShortcuts.PopsOut(Key.O, Ctrl | KeyModifiers.Shift, FocusPlace.Desktop, mac: false));
        Assert.False(TabShortcuts.PopsOut(Key.O, Ctrl, FocusPlace.App, mac: false)); // Shells use Ctrl+O.
        Assert.True(TabShortcuts.PopsOut(Key.O, KeyModifiers.Meta | KeyModifiers.Shift, FocusPlace.Terminal, mac: true));
        Assert.Equal("Cmd+Shift+O", TabShortcuts.PopOutDescription(mac: true));
    }

    [Fact]
    public void Ctrl_shift_k_opens_quick_connect_from_a_terminal_but_not_a_remote_desktop()
    {
        Assert.True(TabShortcuts.OpensQuickConnect(Key.K, Ctrl | KeyModifiers.Shift, FocusPlace.Terminal, mac: false));
        Assert.True(TabShortcuts.OpensQuickConnect(Key.K, Ctrl | KeyModifiers.Shift, FocusPlace.App, mac: false));
        Assert.False(TabShortcuts.OpensQuickConnect(Key.K, Ctrl | KeyModifiers.Shift, FocusPlace.Desktop, mac: false));
        Assert.False(TabShortcuts.OpensQuickConnect(Key.K, Ctrl, FocusPlace.Terminal, mac: false)); // The shell's: delete to the end of the line.
        Assert.True(TabShortcuts.OpensQuickConnect(Key.K, KeyModifiers.Meta | KeyModifiers.Shift, FocusPlace.Terminal, mac: true));
    }

    [Fact]
    public void Ctrl_shift_e_splits_except_from_a_remote_desktop()
    {
        Assert.True(TabShortcuts.Splits(Key.E, Ctrl | KeyModifiers.Shift, FocusPlace.App, mac: false));
        Assert.True(TabShortcuts.Splits(Key.E, Ctrl | KeyModifiers.Shift, FocusPlace.Terminal, mac: false));
        Assert.False(TabShortcuts.Splits(Key.E, Ctrl | KeyModifiers.Shift, FocusPlace.Desktop, mac: false));
        Assert.False(TabShortcuts.Splits(Key.E, Ctrl, FocusPlace.Terminal, mac: false)); // Ctrl+E is end of line in shells.
        Assert.True(TabShortcuts.Splits(Key.E, KeyModifiers.Meta | KeyModifiers.Shift, FocusPlace.Terminal, mac: true));
        Assert.Equal("Ctrl+Shift+E", TabShortcuts.SplitDescription(mac: false));
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(-30, false)]
    [InlineData(-41, true)]
    [InlineData(36 + 30, false)]
    [InlineData(36 + 41, true)]
    public void A_tab_pulled_well_off_the_strip_tears_off(double y, bool tears)
    {
        Assert.Equal(tears, TabShortcuts.TearsOff(y, stripHeight: 36));
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

    [Fact]
    public void Tabs_a_dragged_tab_passes_step_into_its_old_place()
    {
        // Tab 1 of five held over place 3: tabs 2 and 3 step left, the rest stay.
        Assert.Equal([0, 0, -1, -1, 0], Enumerable.Range(0, 5).Select(i => TabShortcuts.Shift(i, 1, 3)));

        // Tab 3 held over place 0: tabs 0 to 2 step right.
        Assert.Equal([1, 1, 1, 0, 0], Enumerable.Range(0, 5).Select(i => TabShortcuts.Shift(i, 3, 0)));

        // Not moved yet: nothing steps aside.
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal(0, TabShortcuts.Shift(i, 2, 2)));
    }
}
