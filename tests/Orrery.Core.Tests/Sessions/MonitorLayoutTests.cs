using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Sessions;

public class MonitorLayoutTests
{
    static IReadOnlyList<RemoteMonitor>? Arrange(params ScreenArea[] screens) => MonitorLayout.Arrange(screens, out _, out _);

    static string? WhyNot(params ScreenArea[] screens)
    {
        Assert.Null(MonitorLayout.Arrange(screens, out _, out var whyNot));
        return whyNot;
    }

    [Fact]
    public void One_display_is_not_spread()
    {
        Assert.Contains("only one display", WhyNot(new ScreenArea(0, 0, 1920, 1080, true)));
    }

    [Fact]
    public void Two_side_by_side_are_placed_from_the_primary()
    {
        var monitors = MonitorLayout.Arrange(
            [new ScreenArea(0, 0, 1920, 1080, true), new ScreenArea(1920, 0, 2560, 1440, false)], out var covered, out var whyNot);
        Assert.Null(whyNot);
        Assert.Equal([new RemoteMonitor(0, 0, 1920, 1080, true), new RemoteMonitor(1920, 0, 2560, 1440, false)], monitors);
        Assert.Equal(new ScreenArea(0, 0, 4480, 1440, false), covered);
    }

    [Fact]
    public void A_display_left_of_or_above_the_primary_sits_at_negative_offsets()
    {
        var monitors = MonitorLayout.Arrange(
            [new ScreenArea(-1280, -200, 1280, 1024, false), new ScreenArea(0, 0, 1920, 1080, true)], out var covered, out _);
        Assert.Equal([new RemoteMonitor(-1280, -200, 1280, 1024, false), new RemoteMonitor(0, 0, 1920, 1080, true)], monitors);
        Assert.Equal(new ScreenArea(-1280, -200, 3200, 1280, false), covered);
    }

    [Fact]
    public void One_above_another_is_spread()
    {
        Assert.NotNull(Arrange(new ScreenArea(0, 0, 1920, 1080, true), new ScreenArea(500, -900, 1600, 900, false)));
    }

    [Fact]
    public void Without_a_primary_the_first_display_is()
    {
        var monitors = Arrange(new ScreenArea(100, 0, 1920, 1080, false), new ScreenArea(2020, 0, 1920, 1080, false));
        Assert.Equal([new RemoteMonitor(0, 0, 1920, 1080, true), new RemoteMonitor(1920, 0, 1920, 1080, false)], monitors);
    }

    [Fact]
    public void A_gap_between_displays_is_refused()
    {
        Assert.Contains("doesn't sit against", WhyNot(new ScreenArea(0, 0, 1920, 1080, true), new ScreenArea(1930, 0, 1920, 1080, false)));
    }

    [Fact]
    public void Two_pairs_apart_from_each_other_are_refused()
    {
        Assert.Contains("doesn't sit against", WhyNot(
            new ScreenArea(0, 0, 1920, 1080, true), new ScreenArea(1920, 0, 1920, 1080, false),
            new ScreenArea(0, 2000, 1920, 1080, false), new ScreenArea(1920, 2000, 1920, 1080, false)));
    }

    [Fact]
    public void Three_in_a_row_are_spread()
    {
        Assert.Equal(3, Arrange(
            new ScreenArea(-1920, 0, 1920, 1080, false), new ScreenArea(0, 0, 1920, 1080, true), new ScreenArea(1920, 0, 1920, 1080, false))?.Count);
    }

    [Fact]
    public void Displays_that_only_meet_at_a_corner_are_refused()
    {
        Assert.Contains("doesn't sit against", WhyNot(new ScreenArea(0, 0, 1920, 1080, true), new ScreenArea(1920, 1080, 1920, 1080, false)));
    }

    [Fact]
    public void Mirrored_displays_are_refused()
    {
        Assert.Contains("overlap", WhyNot(new ScreenArea(0, 0, 1920, 1080, true), new ScreenArea(0, 0, 1920, 1080, false)));
    }

    [Fact]
    public void Too_small_or_too_large_is_refused()
    {
        Assert.Contains("smaller than", WhyNot(new ScreenArea(0, 0, 1920, 1080, true), new ScreenArea(1920, 0, 150, 1080, false)));
        Assert.Contains("larger than", WhyNot(
            new ScreenArea(0, 0, 3840, 2160, true), new ScreenArea(3840, 0, 3840, 2160, false), new ScreenArea(7680, 0, 3840, 2160, false)));
    }
}
