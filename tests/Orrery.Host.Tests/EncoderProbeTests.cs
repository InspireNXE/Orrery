using Xunit;

namespace Orrery.Host.Tests;

public class EncoderProbeTests
{
    [Fact]
    public void The_probe_reports_without_throwing_on_any_machine()
    {
        // CI runners have no GPU and no NVIDIA driver: the probe should say so, not fail. On Windows this also runs the
        // DXGI vtable calls for real.
        var output = new StringWriter();

        int exit = EncoderProbe.Run(output);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(0, exit);
            Assert.StartsWith("NVENC: ", output.ToString());
        }
        else
        {
            Assert.Equal(1, exit);
        }
    }
}
