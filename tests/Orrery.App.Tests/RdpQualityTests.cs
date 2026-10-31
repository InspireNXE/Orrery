using Devolutions.IronRdp;
using Orrery.Core.Sessions;
using Orrery.Protocols.Rdp;
using Xunit;

namespace Orrery.App.Tests;

public class RdpQualityTests
{
    [Fact]
    public void Balanced_asks_for_what_ironrdp_asks_for_by_default()
    {
        // IronRDP's default: no window contents while dragging, no menu animations, font smoothing.
        Assert.Equal(
            [PerformanceFlagsType.DisableFullWindowDrag, PerformanceFlagsType.DisableMenuAnimations, PerformanceFlagsType.EnableFontSmoothing],
            RdpSession.VisualEffects(DisplayQuality.Balanced).Order());
    }

    [Fact]
    public void Best_disables_nothing_and_adds_desktop_composition()
    {
        var flags = RdpSession.VisualEffects(DisplayQuality.Best);
        Assert.Contains(PerformanceFlagsType.EnableDesktopComposition, flags);
        Assert.Contains(PerformanceFlagsType.EnableFontSmoothing, flags);
        Assert.DoesNotContain(flags, f => f.ToString().StartsWith("Disable", StringComparison.Ordinal));
    }

    [Fact]
    public void Fastest_drops_wallpaper_themes_and_smoothing()
    {
        var flags = RdpSession.VisualEffects(DisplayQuality.Fastest);
        Assert.Contains(PerformanceFlagsType.DisableWallpaper, flags);
        Assert.Contains(PerformanceFlagsType.DisableTheming, flags);
        Assert.DoesNotContain(PerformanceFlagsType.EnableFontSmoothing, flags);
        Assert.DoesNotContain(PerformanceFlagsType.EnableDesktopComposition, flags);
    }
}
