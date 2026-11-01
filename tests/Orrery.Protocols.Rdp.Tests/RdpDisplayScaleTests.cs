using Xunit;

namespace Orrery.Protocols.Rdp.Tests;

public class RdpDisplayScaleTests
{
    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(1.25, 125)]
    [InlineData(1.5, 150)]
    [InlineData(1.749, 175)]
    [InlineData(0.75, 100)]
    [InlineData(6.0, 500)]
    [InlineData(double.NaN, 100)]
    public void Display_scaling_becomes_the_percentage_rdp_takes(double renderScaling, int expected)
    {
        Assert.Equal(expected, RdpSession.ScalePercent(renderScaling));
    }
}
