using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.Core.Tests.Sessions;

public class DesktopLimitsTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(8192, true)]
    [InlineData(8193, false)]
    [InlineData(65535, false)]
    public void A_side_must_be_from_1_to_8192_pixels(int side, bool supported)
    {
        Assert.Equal(supported, DesktopLimits.IsSupported(side, 600));
        Assert.Equal(supported, DesktopLimits.IsSupported(800, side));
        Assert.Equal(supported, DesktopLimits.IsSupported(side, side));
    }

    [Fact]
    public void The_refusal_names_the_size_the_server_asked_for()
    {
        Assert.Equal("The server asked for a 32768x32768 desktop, which is larger than Orrery supports.",
            DesktopLimits.RefusalMessage(32768, 32768));
        Assert.Contains("0x600", DesktopLimits.RefusalMessage(0, 600));
    }
}
