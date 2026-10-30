using Orrery.Core.Terminal;
using Xunit;

namespace Orrery.Core.Tests.Terminal;

public class TerminalEmulatorTests
{
    const string Esc = "\u001b";

    static TerminalEmulator Term(int columns = 10, int rows = 4, int scrollback = 100) => new(columns, rows, scrollback);

    static string Row(TerminalEmulator t, int row) => t.ScreenLine(row).Text();

    static string[] Screen(TerminalEmulator t) => Enumerable.Range(0, t.Rows).Select(r => Row(t, r)).ToArray();

    [Fact]
    public void Prints_text_and_handles_cr_lf()
    {
        var t = Term();
        t.Feed("hello\r\nworld");
        Assert.Equal(["hello", "world", "", ""], Screen(t));
        Assert.Equal((5, 1), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Wraps_at_the_edge_and_marks_the_line_wrapped()
    {
        var t = Term(columns: 5);
        t.Feed("abcdefg");
        Assert.Equal(["abcde", "fg", "", ""], Screen(t));
        Assert.True(t.ScreenLine(0).Wrapped);
        Assert.Equal("abcdefg", t.GetText(0, 0, 1, 4));
    }

    [Fact]
    public void Writing_the_last_column_waits_to_wrap()
    {
        var t = Term(columns: 5);
        t.Feed("abcde\r\nx");
        Assert.Equal(["abcde", "x", "", ""], Screen(t));
    }

    [Fact]
    public void Scrolling_off_the_top_fills_scrollback()
    {
        var t = Term(rows: 2);
        t.Feed("one\r\ntwo\r\nthree\r\nfour");
        Assert.Equal(["three", "four"], Screen(t));
        Assert.Equal(2, t.ScrollbackCount);
        Assert.Equal("one", t.LineAt(0).Text());
    }

    [Fact]
    public void Cursor_positioning_and_erasing()
    {
        var t = Term();
        t.Feed("aaaaaaaaaa\r\nbbbbbbbbbb\r\ncccccccccc");
        t.Feed($"{Esc}[2;3H{Esc}[K");
        Assert.Equal("bb", Row(t, 1));
        t.Feed($"{Esc}[1J");
        Assert.Equal(["", "", "cccccccccc", ""], Screen(t));
        t.Feed($"{Esc}[2J");
        Assert.All(Screen(t), r => Assert.Equal("", r));
    }

    [Fact]
    public void Insert_and_delete_characters()
    {
        var t = Term();
        t.Feed($"abcdef{Esc}[1;3H{Esc}[2@XY");
        Assert.Equal("abXYcdef", Row(t, 0));
        t.Feed($"{Esc}[1;3H{Esc}[2P");
        Assert.Equal("abcdef", Row(t, 0));
    }

    [Fact]
    public void Scroll_region_keeps_lines_outside_it_still()
    {
        var t = Term(rows: 4);
        t.Feed("head\r\nb\r\nc\r\nfoot");
        t.Feed($"{Esc}[2;3r{Esc}[3;1H\nnew");
        Assert.Equal(["head", "c", "new", "foot"], Screen(t));
        Assert.Equal(0, t.ScrollbackCount);
    }

    [Fact]
    public void Insert_and_delete_lines_within_the_region()
    {
        var t = Term(rows: 4);
        t.Feed("1\r\n2\r\n3\r\n4");
        t.Feed($"{Esc}[2;1H{Esc}[L");
        Assert.Equal(["1", "", "2", "3"], Screen(t));
        t.Feed($"{Esc}[2M");
        Assert.Equal(["1", "3", "", ""], Screen(t));
    }

    [Fact]
    public void Alternate_screen_is_separate_and_restores_the_main_screen()
    {
        var t = Term();
        t.Feed("shell$ vim");
        t.Feed($"{Esc}[?1049h{Esc}[Hediting");
        Assert.True(t.AlternateScreen);
        Assert.Equal("editing", Row(t, 0));

        t.Feed($"{Esc}[?1049l");
        Assert.False(t.AlternateScreen);
        Assert.Equal("shell$ vim", Row(t, 0));
        Assert.Equal((10 - 1, 0), (t.CursorX, t.CursorY));
    }

    [Fact]
    public void Sgr_sets_16_256_and_truecolor()
    {
        var t = Term();
        t.Feed($"{Esc}[1;31mA{Esc}[38;5;208mB{Esc}[48;2;10;20;30mC{Esc}[38:2::1:2:3mD{Esc}[0mE");
        var cells = t.ScreenLine(0).Cells;

        Assert.Equal(TerminalColor.Palette(1), cells[0].Style.Foreground);
        Assert.True(cells[0].Style.Flags.HasFlag(CellFlags.Bold));
        Assert.Equal(TerminalColor.Palette(208), cells[1].Style.Foreground);
        Assert.Equal(TerminalColor.Rgb(10, 20, 30), cells[2].Style.Background);
        Assert.Equal(TerminalColor.Rgb(1, 2, 3), cells[3].Style.Foreground);
        Assert.Equal(CellStyle.Default, cells[4].Style);
    }

    [Fact]
    public void Erase_uses_the_current_background()
    {
        var t = Term();
        t.Feed($"{Esc}[44m{Esc}[2J");
        Assert.Equal(TerminalColor.Palette(4), t.ScreenLine(2).Cells[5].Style.Background);
    }

    [Fact]
    public void Reports_cursor_position_and_device_attributes()
    {
        var t = Term();
        var replies = new List<string>();
        t.Reply += replies.Add;

        t.Feed($"{Esc}[3;4H{Esc}[6n{Esc}[c");
        Assert.Equal(["\u001b[3;4R", "\u001b[?62;22c"], replies);
    }

    [Fact]
    public void Osc_sets_the_title_with_either_terminator()
    {
        var t = Term();
        string? title = null;
        t.TitleChanged += s => title = s;

        t.Feed($"{Esc}]0;user@host: ~\u0007");
        Assert.Equal("user@host: ~", title);
        t.Feed($"{Esc}]2;second{Esc}\\after");
        Assert.Equal("second", t.Title);
        Assert.Equal("after", Row(t, 0));
    }

    [Fact]
    public void Line_drawing_charset_draws_boxes()
    {
        var t = Term();
        t.Feed($"{Esc}(0lqk{Esc}(Bq");
        Assert.Equal("┌─┐q", Row(t, 0));
    }

    [Fact]
    public void Wide_characters_take_two_cells()
    {
        var t = Term(columns: 6);
        t.Feed("a日本b");
        Assert.Equal("a日本b", Row(t, 0));
        Assert.Equal(TerminalCell.WideContinuation, t.ScreenLine(0).Cells[2].CodePoint);
        Assert.Equal(6, t.CursorX + 1);
    }

    [Fact]
    public void Utf8_split_across_feeds_is_decoded()
    {
        var t = Term();
        var bytes = System.Text.Encoding.UTF8.GetBytes("é😀");
        foreach (var b in bytes)
        {
            t.Feed([b]);
        }

        Assert.Equal("é😀", Row(t, 0));
    }

    [Fact]
    public void Private_modes_track_cursor_keys_and_bracketed_paste()
    {
        var t = Term();
        t.Feed($"{Esc}[?1h{Esc}[?2004h{Esc}[?25l");
        Assert.True(t.CursorKeysApplication);
        Assert.True(t.BracketedPaste);
        Assert.False(t.CursorVisible);
    }

    [Fact]
    public void Unknown_and_string_sequences_are_ignored()
    {
        var t = Term();
        t.Feed($"a{Esc}P1$tignored{Esc}\\b{Esc}[?9999hc{Esc}[5 qd");
        Assert.Equal("abcd", Row(t, 0));
    }

    [Fact]
    public void Tabs_stop_every_eight_columns()
    {
        var t = Term(columns: 20);
        t.Feed("a\tb\tc");
        Assert.Equal(16, t.CursorX - 1);
        Assert.Equal('b', t.ScreenLine(0).Cells[8].CodePoint);
    }

    [Fact]
    public void Shrinking_keeps_the_cursor_line_and_growing_pulls_scrollback_back()
    {
        var t = Term(columns: 10, rows: 4);
        t.Feed("1\r\n2\r\n3\r\n4");
        t.Resize(10, 2);
        Assert.Equal(["3", "4"], Screen(t));
        Assert.Equal(1, t.CursorY);

        t.Resize(10, 4);
        Assert.Equal(["1", "2", "3", "4"], Screen(t));
        Assert.Equal(3, t.CursorY);
    }

    [Fact]
    public void Narrowing_truncates_and_clamps_the_cursor()
    {
        var t = Term(columns: 10);
        t.Feed("abcdefghij");
        t.Resize(4, 4);
        Assert.Equal("abcd", Row(t, 0));
        Assert.Equal(3, t.CursorX);
    }

    [Fact]
    public void Full_reset_clears_everything()
    {
        var t = Term(rows: 2);
        t.Feed($"a\r\nb\r\nc{Esc}[31m{Esc}c");
        Assert.Equal(["", ""], Screen(t));
        Assert.Equal(0, t.ScrollbackCount);
    }

    [Theory]
    [InlineData(TerminalKey.Up, TerminalModifiers.None, false, "\u001b[A")]
    [InlineData(TerminalKey.Up, TerminalModifiers.None, true, "\u001bOA")]
    [InlineData(TerminalKey.Right, TerminalModifiers.Control, false, "\u001b[1;5C")]
    [InlineData(TerminalKey.Delete, TerminalModifiers.None, false, "\u001b[3~")]
    [InlineData(TerminalKey.F5, TerminalModifiers.Shift, false, "\u001b[15;2~")]
    [InlineData(TerminalKey.Tab, TerminalModifiers.Shift, false, "\u001b[Z")]
    [InlineData(TerminalKey.Backspace, TerminalModifiers.None, false, "\u007f")]
    public void Keys_encode_like_xterm(TerminalKey key, TerminalModifiers modifiers, bool application, string expected)
    {
        Assert.Equal(expected, TerminalKeys.Encode(key, modifiers, application));
    }

    [Fact]
    public void Control_letters_and_paste()
    {
        Assert.Equal("\u0003", TerminalKeys.ControlCharacter('c'));
        Assert.Equal("\u001b[200~a\rb\u001b[201~", TerminalKeys.Paste("a\r\nb", bracketed: true));
        Assert.Equal("x", TerminalKeys.Paste("x\u001b[201~", bracketed: true)[6..7]);
    }

    [Theory]
    [InlineData(TerminalKey.Left, true, "b")]
    [InlineData(TerminalKey.Right, true, "f")]
    [InlineData(TerminalKey.Backspace, true, "")]
    [InlineData(TerminalKey.Delete, true, "d")]
    [InlineData(TerminalKey.Left, false, "[1;5D")]   // Full-screen programs: xterm's sequence.
    [InlineData(TerminalKey.Right, false, "[1;5C")]
    [InlineData(TerminalKey.Backspace, false, "")]   // ^W: delete the word before the cursor (vim).
    [InlineData(TerminalKey.Delete, false, "[3;5~")]
    public void Ctrl_word_keys_suit_the_shell_prompt_or_the_full_screen_program(TerminalKey key, bool shellPrompt, string expected)
    {
        Assert.Equal(expected, TerminalKeys.Encode(key, TerminalModifiers.Control, cursorKeysApplication: false, shellPrompt));
    }

    [Fact]
    public void Ctrl_shift_arrows_keep_the_xterm_sequence_at_the_prompt()
    {
        Assert.Equal("[1;6D", TerminalKeys.Encode(TerminalKey.Left, TerminalModifiers.Control | TerminalModifiers.Shift, false, shellPrompt: true));
    }
}
