using System.Text;
using Orrery.Core.Terminal;
using Xunit;

namespace Orrery.Core.Tests.Terminal;

public class TerminalMouseTests
{
    static TerminalEmulator Feed(string text)
    {
        var terminal = new TerminalEmulator(80, 24);
        terminal.Feed(Encoding.UTF8.GetBytes(text));
        return terminal;
    }

    [Theory]
    [InlineData("\u001b[?9h", MouseTracking.Press)]
    [InlineData("\u001b[?1000h", MouseTracking.Normal)]
    [InlineData("\u001b[?1002h", MouseTracking.ButtonMotion)]
    [InlineData("\u001b[?1003h", MouseTracking.AnyMotion)]
    [InlineData("\u001b[?1000h\u001b[?1002h", MouseTracking.ButtonMotion)]          // The later mode wins.
    [InlineData("\u001b[?1002h\u001b[?1000l", MouseTracking.ButtonMotion)]          // Clearing another mode leaves it.
    [InlineData("\u001b[?1002h\u001b[?1002l", MouseTracking.None)]
    [InlineData("\u001b[?1000;1006h\u001bc", MouseTracking.None)]                   // Full reset.
    public void Programs_turn_mouse_tracking_on_and_off(string sequence, MouseTracking expected)
    {
        Assert.Equal(expected, Feed(sequence).MouseTracking);
    }

    [Fact]
    public void Sgr_encoding_is_its_own_mode()
    {
        var terminal = Feed("\u001b[?1000;1006h");
        Assert.True(terminal.MouseSgr);
        terminal.Feed("\u001b[?1006l"u8);
        Assert.False(terminal.MouseSgr);
        Assert.Equal(MouseTracking.Normal, terminal.MouseTracking);
    }

    static string Sgr(TerminalMouseEvent kind, TerminalMouseButton button, int column, int row, TerminalModifiers mods = TerminalModifiers.None) =>
        Encoding.ASCII.GetString(TerminalMouse.Encode(kind, button, column, row, mods, sgr: true)!);

    [Fact]
    public void Sgr_reports_one_based_positions_and_the_released_button()
    {
        Assert.Equal("\u001b[<0;1;1M", Sgr(TerminalMouseEvent.Press, TerminalMouseButton.Left, 0, 0));
        Assert.Equal("\u001b[<2;10;5m", Sgr(TerminalMouseEvent.Release, TerminalMouseButton.Right, 9, 4));
        Assert.Equal("\u001b[<32;3;3M", Sgr(TerminalMouseEvent.Motion, TerminalMouseButton.Left, 2, 2));
        Assert.Equal("\u001b[<35;3;3M", Sgr(TerminalMouseEvent.Motion, TerminalMouseButton.None, 2, 2));
        Assert.Equal("\u001b[<64;1;1M", Sgr(TerminalMouseEvent.Press, TerminalMouseButton.WheelUp, 0, 0));
        Assert.Equal("\u001b[<65;1;1M", Sgr(TerminalMouseEvent.Press, TerminalMouseButton.WheelDown, 0, 0));
        Assert.Equal("\u001b[<20;1;1M", Sgr(TerminalMouseEvent.Press, TerminalMouseButton.Left, 0, 0, TerminalModifiers.Control | TerminalModifiers.Shift));
        Assert.Equal("\u001b[<0;300;1M", Sgr(TerminalMouseEvent.Press, TerminalMouseButton.Left, 299, 0)); // No limit.
    }

    [Fact]
    public void Classic_encoding_offsets_by_32_and_hides_which_button_was_released()
    {
        Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 32, 33, 33 }, TerminalMouse.Encode(TerminalMouseEvent.Press, TerminalMouseButton.Left, 0, 0, TerminalModifiers.None, sgr: false));
        Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 32 + 3, 32 + 10, 32 + 5 }, TerminalMouse.Encode(TerminalMouseEvent.Release, TerminalMouseButton.Right, 9, 4, TerminalModifiers.None, sgr: false));
        Assert.Null(TerminalMouse.Encode(TerminalMouseEvent.Press, TerminalMouseButton.Left, 300, 0, TerminalModifiers.None, sgr: false));
        Assert.Equal(32, TerminalMouse.Encode(TerminalMouseEvent.Press, TerminalMouseButton.Left, 0, 0, TerminalModifiers.Control, sgr: false, pressOnly: true)![3]);
    }

    [Theory]
    [InlineData(MouseTracking.None, TerminalMouseEvent.Press, TerminalMouseButton.Left, false)]
    [InlineData(MouseTracking.Press, TerminalMouseEvent.Press, TerminalMouseButton.Left, true)]
    [InlineData(MouseTracking.Press, TerminalMouseEvent.Release, TerminalMouseButton.Left, false)]
    [InlineData(MouseTracking.Normal, TerminalMouseEvent.Release, TerminalMouseButton.Left, true)]
    [InlineData(MouseTracking.Normal, TerminalMouseEvent.Release, TerminalMouseButton.WheelUp, false)]
    [InlineData(MouseTracking.Normal, TerminalMouseEvent.Motion, TerminalMouseButton.Left, false)]
    [InlineData(MouseTracking.ButtonMotion, TerminalMouseEvent.Motion, TerminalMouseButton.Left, true)]
    [InlineData(MouseTracking.ButtonMotion, TerminalMouseEvent.Motion, TerminalMouseButton.None, false)]
    [InlineData(MouseTracking.AnyMotion, TerminalMouseEvent.Motion, TerminalMouseButton.None, true)]
    public void Each_mode_asks_for_its_own_events(MouseTracking mode, TerminalMouseEvent kind, TerminalMouseButton button, bool wanted)
    {
        Assert.Equal(wanted, TerminalMouse.Wants(mode, kind, button));
    }
}
