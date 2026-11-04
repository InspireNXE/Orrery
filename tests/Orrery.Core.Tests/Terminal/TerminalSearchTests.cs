using Orrery.Core.Terminal;
using Xunit;

namespace Orrery.Core.Tests.Terminal;

public class TerminalSearchTests
{
    static TerminalEmulator Term(int columns = 10, int rows = 4, int scrollback = 100) => new(columns, rows, scrollback);

    [Fact]
    public void Finds_every_match_ignoring_case_oldest_first()
    {
        var t = Term(columns: 20);
        t.Feed("nginx ok\r\nNGINX again nginx");
        var found = TerminalSearch.Find(t, "nginx", 100);
        Assert.Equal([(0, 0, 0, 4), (1, 0, 1, 4), (1, 12, 1, 16)], found.Select(m => (m.Row, m.Column, m.EndRow, m.EndColumn)));
    }

    [Fact]
    public void A_match_runs_across_a_wrapped_line()
    {
        var t = Term(columns: 5);
        t.Feed("xxxabcdef");
        var match = Assert.Single(TerminalSearch.Find(t, "abcd", 100));
        Assert.Equal((0, 3, 1, 1), (match.Row, match.Column, match.EndRow, match.EndColumn));
        Assert.True(match.Covers(0, 4));
        Assert.True(match.Covers(1, 0));
        Assert.False(match.Covers(1, 2));
    }

    [Fact]
    public void Doesnt_join_lines_that_ended_with_a_newline()
    {
        var t = Term(columns: 10);
        t.Feed("abc\r\ndef");
        Assert.Empty(TerminalSearch.Find(t, "cd", 100));
    }

    [Fact]
    public void Searches_the_scrollback_too()
    {
        var t = Term(columns: 10, rows: 2);
        t.Feed("needle\r\none\r\ntwo\r\nthree");
        var match = Assert.Single(TerminalSearch.Find(t, "needle", 100));
        Assert.Equal(0, match.Row);
        Assert.True(t.ScrollbackCount > 0);
        Assert.Same(t.LineAt(0), match.Line);
    }

    [Fact]
    public void A_wide_character_covers_both_its_cells()
    {
        var t = Term(columns: 10);
        t.Feed("a中b");
        var match = Assert.Single(TerminalSearch.Find(t, "中", 100));
        Assert.Equal((0, 1, 0, 2), (match.Row, match.Column, match.EndRow, match.EndColumn));

        var after = Assert.Single(TerminalSearch.Find(t, "中b", 100));
        Assert.Equal((1, 3), (after.Column, after.EndColumn));
    }

    [Fact]
    public void Stops_at_the_limit_and_finds_nothing_for_an_empty_query()
    {
        var t = Term(columns: 20);
        t.Feed("aaaaaaaaaa");
        Assert.Equal(3, TerminalSearch.Find(t, "a", 3).Count);
        Assert.Empty(TerminalSearch.Find(t, "", 100));
    }

    [Fact]
    public void Matches_dont_overlap()
    {
        var t = Term(columns: 20);
        t.Feed("aaaa");
        Assert.Equal([0, 2], TerminalSearch.Find(t, "aa", 100).Select(m => m.Column));
    }
}
