using Xunit;

namespace Orrery.Protocols.Rdp.Tests;

public class RdpeiValuesTests
{
    [Theory]
    [InlineData(0, 0u)]
    [InlineData(0.5, 512u)]
    [InlineData(1, 1024u)]
    [InlineData(1.5, 1024u)]
    [InlineData(-0.2, 0u)]
    [InlineData(double.NaN, 0u)]
    public void Pressure_runs_from_0_to_1024(double pressure, uint expected)
    {
        Assert.Equal(expected, RdpeiValues.Pressure(pressure));
    }

    [Theory]
    [InlineData(45.4, (short)45)]
    [InlineData(-120, (short)-90)]
    [InlineData(120, (short)90)]
    [InlineData(double.NaN, (short)0)]
    public void Tilt_stays_within_a_right_angle(double degrees, short expected)
    {
        Assert.Equal(expected, RdpeiValues.Tilt(degrees));
    }

    [Theory]
    [InlineData(0, (ushort)0)]
    [InlineData(359.4, (ushort)359)]
    [InlineData(360, (ushort)0)]
    [InlineData(-90, (ushort)270)]
    [InlineData(725, (ushort)5)]
    [InlineData(double.PositiveInfinity, (ushort)0)]
    public void Rotation_wraps_into_one_turn(double degrees, ushort expected)
    {
        Assert.Equal(expected, RdpeiValues.Rotation(degrees));
    }
}
