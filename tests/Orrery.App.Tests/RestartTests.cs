using Xunit;

namespace Orrery.App.Tests;

public class RestartTests
{
    [Fact]
    public void Restarts_this_executable_outside_an_AppImage()
    {
        Assert.Equal("/opt/orrery/Orrery", App.RestartExecutable("/opt/orrery/Orrery", null));
        Assert.Equal("/opt/orrery/Orrery", App.RestartExecutable("/opt/orrery/Orrery", ""));
    }

    [Fact]
    public void Restarts_the_AppImage_file_rather_than_its_mount()
    {
        Assert.Equal("/home/me/Orrery.AppImage", App.RestartExecutable("/tmp/.mount_OrreryX/usr/lib/orrery/Orrery", "/home/me/Orrery.AppImage"));
    }

    [Fact]
    public void Ignores_a_relative_APPIMAGE()
    {
        Assert.Equal("/opt/orrery/Orrery", App.RestartExecutable("/opt/orrery/Orrery", "Orrery.AppImage"));
    }
}
