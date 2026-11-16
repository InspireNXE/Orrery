using Xunit;

namespace Orrery.Protocols.Rdp.Tests;

public class RedrawCoverageTests
{
    [Fact]
    public void One_small_piece_is_not_the_desktop_redrawn()
    {
        var redraw = new RedrawCoverage();
        redraw.Start(1920, 1080);

        // The first update after a resize can be a single tile; timing to it made a slow redraw look fast.
        Assert.False(redraw.Add(0, 0, 63, 63));
        Assert.True(redraw.Active);
    }

    [Fact]
    public void The_whole_desktop_in_one_update_is_redrawn()
    {
        var redraw = new RedrawCoverage();
        redraw.Start(1920, 1080);

        Assert.True(redraw.Add(0, 0, 1919, 1079));
        Assert.False(redraw.Active);
    }

    [Fact]
    public void Redrawn_once_ninety_percent_is_drawn_and_said_only_once()
    {
        var redraw = new RedrawCoverage();
        redraw.Start(640, 640); // 10 by 10 tiles

        Assert.False(redraw.Add(0, 0, 639, 63 + 64 * 7)); // 8 rows
        Assert.True(redraw.Add(0, 512, 639, 575)); // the 9th: 90 of 100
        Assert.False(redraw.Add(0, 576, 639, 639));
    }

    [Fact]
    public void Drawing_the_same_part_again_doesnt_count_twice()
    {
        var redraw = new RedrawCoverage();
        redraw.Start(640, 640);

        for (int i = 0; i < 200; i++)
        {
            Assert.False(redraw.Add(0, 0, 639, 63));
        }
    }

    [Fact]
    public void A_tile_counts_once_an_update_covers_its_middle()
    {
        var redraw = new RedrawCoverage();
        redraw.Start(64, 64); // one tile

        Assert.False(redraw.Add(0, 0, 30, 63)); // short of the middle pixel, 31
        Assert.True(redraw.Add(31, 31, 31, 31));
    }

    [Fact]
    public void A_short_last_tile_counts_by_its_own_middle()
    {
        var redraw = new RedrawCoverage();
        redraw.Start(70, 64); // a full tile, then one 6 pixels wide (64 to 69, middle 66)

        Assert.False(redraw.Add(0, 0, 63, 63));
        Assert.True(redraw.Add(65, 0, 67, 63));
    }

    [Fact]
    public void Updates_past_the_desktop_are_clipped()
    {
        var redraw = new RedrawCoverage();
        redraw.Start(100, 100);

        Assert.True(redraw.Add(-50, -50, 5000, 5000));
    }

    [Fact]
    public void Starting_again_forgets_what_was_drawn_and_follows_the_new_size()
    {
        var redraw = new RedrawCoverage();
        redraw.Start(1920, 1080);
        Assert.False(redraw.Add(0, 0, 1919, 539));

        redraw.Start(1280, 720);
        Assert.False(redraw.Add(0, 0, 1279, 63));
        Assert.True(redraw.Add(0, 0, 1279, 719));

        redraw.Start(2560, 1440); // more tiles than before
        Assert.False(redraw.Add(0, 0, 1279, 719));
        Assert.True(redraw.Add(0, 0, 2559, 1439));
    }

    [Fact]
    public void Nothing_counts_before_a_start_or_after_a_stop()
    {
        var redraw = new RedrawCoverage();
        Assert.False(redraw.Add(0, 0, 1919, 1079));

        redraw.Start(1920, 1080);
        redraw.Stop();
        Assert.False(redraw.Active);
        Assert.False(redraw.Add(0, 0, 1919, 1079));
    }
}
