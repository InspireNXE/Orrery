using System.Runtime.InteropServices;
using Orrery.Core.Storage;
using Orrery.Core.Updates;
using Xunit;

namespace Orrery.Core.Tests.Updates;

public class ThisCopyTests
{
    static readonly ReleaseVersion V = ReleaseVersion.Parse("0.2.0-preview.1");

    [Theory]
    [InlineData(CopyKind.WindowsSetup, Architecture.X64, "Orrery-Setup-0.2.0-preview.1.exe")]
    [InlineData(CopyKind.WindowsSetup, Architecture.Arm64, "Orrery-Setup-0.2.0-preview.1.exe")]
    [InlineData(CopyKind.WindowsPortable, Architecture.X64, "Orrery-0.2.0-preview.1-win-x64-portable.zip")]
    [InlineData(CopyKind.WindowsPortable, Architecture.Arm64, "Orrery-0.2.0-preview.1-win-arm64-portable.zip")]
    [InlineData(CopyKind.LinuxPortable, Architecture.X64, "Orrery-0.2.0-preview.1-linux-x64-portable.zip")]
    [InlineData(CopyKind.LinuxPortable, Architecture.Arm64, "Orrery-0.2.0-preview.1-linux-arm64-portable.zip")]
    [InlineData(CopyKind.AppImage, Architecture.X64, "Orrery-0.2.0-preview.1-x86_64.AppImage")]
    [InlineData(CopyKind.AppImage, Architecture.Arm64, "Orrery-0.2.0-preview.1-aarch64.AppImage")]
    public void Takes_the_file_the_build_scripts_name(CopyKind kind, Architecture architecture, string expected)
    {
        var copy = new ThisCopy(kind, architecture);
        Assert.Equal(expected, copy.AssetName(V));
        Assert.True(copy.UpdatesItself);
    }

    [Theory]
    [InlineData(CopyKind.LinuxPackage, Architecture.X64)]
    [InlineData(CopyKind.MacOS, Architecture.Arm64)]
    [InlineData(CopyKind.Development, Architecture.X64)]
    [InlineData(CopyKind.WindowsPortable, Architecture.X86)]
    public void Copies_that_dont_update_themselves_take_no_file(CopyKind kind, Architecture architecture)
    {
        var copy = new ThisCopy(kind, architecture);
        Assert.Null(copy.AssetName(V));
        Assert.False(copy.UpdatesItself);
    }

    [Fact]
    public void Tells_each_kind_of_copy_apart()
    {
        var x64 = Architecture.X64;
        var windowsExe = Path.Combine(Path.GetTempPath(), "app", "Orrery.exe");
        var linuxExe = Path.Combine(Path.GetTempPath(), "app", "Orrery");
        var appImage = Path.Combine(Path.GetTempPath(), "Orrery-0.1.0-x86_64.AppImage");
        var dotnet = Path.Combine(Path.GetTempPath(), "dotnet");

        Assert.Equal(new ThisCopy(CopyKind.WindowsSetup, x64), ThisCopy.Detect(StorageMode.Installed, OSPlatform.Windows, x64, windowsExe, null));
        Assert.Equal(new ThisCopy(CopyKind.WindowsPortable, x64, windowsExe), ThisCopy.Detect(StorageMode.Portable, OSPlatform.Windows, x64, windowsExe, null));
        Assert.Equal(new ThisCopy(CopyKind.LinuxPortable, x64, linuxExe), ThisCopy.Detect(StorageMode.Portable, OSPlatform.Linux, x64, linuxExe, null));
        Assert.Equal(new ThisCopy(CopyKind.LinuxPackage, x64), ThisCopy.Detect(StorageMode.Installed, OSPlatform.Linux, x64, "/opt/orrery/Orrery", null));
        Assert.Equal(new ThisCopy(CopyKind.AppImage, x64, appImage), ThisCopy.Detect(StorageMode.Installed, OSPlatform.Linux, x64, "/tmp/.mount_x/usr/lib/orrery/Orrery", appImage));
        Assert.Equal(CopyKind.MacOS, ThisCopy.Detect(StorageMode.Portable, OSPlatform.OSX, Architecture.Arm64, "/Applications/Orrery.app/Contents/MacOS/Orrery", null).Kind);
        Assert.Equal(CopyKind.Development, ThisCopy.Detect(StorageMode.Installed, OSPlatform.Windows, x64, dotnet + ".exe", null).Kind);
        Assert.Equal(CopyKind.Development, ThisCopy.Detect(StorageMode.Portable, OSPlatform.Linux, x64, dotnet, null).Kind);
        Assert.Equal(CopyKind.Development, ThisCopy.Detect(StorageMode.Installed, OSPlatform.Windows, x64, null, null).Kind);
        // A relative $APPIMAGE isn't one an AppImage runtime sets.
        Assert.Equal(CopyKind.LinuxPortable, ThisCopy.Detect(StorageMode.Portable, OSPlatform.Linux, x64, linuxExe, "Orrery.AppImage").Kind);
    }
}
