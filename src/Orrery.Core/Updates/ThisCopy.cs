using System.Runtime.InteropServices;
using Orrery.Core.Storage;

namespace Orrery.Core.Updates;

/// <summary>How this copy of Orrery was installed, which decides how it updates.</summary>
public enum CopyKind
{
    /// <summary>Installed by Orrery Setup: updates by running the new setup.</summary>
    WindowsSetup,

    /// <summary>The Windows portable zip: updates by swapping in the new Orrery.exe.</summary>
    WindowsPortable,

    /// <summary>The Linux portable zip: updates by swapping in the new Orrery.</summary>
    LinuxPortable,

    /// <summary>A Linux AppImage: updates by swapping in the new .AppImage file.</summary>
    AppImage,

    /// <summary>Installed from the .deb or .rpm: the package manager updates it, so Orrery only says there's a release.</summary>
    LinuxPackage,

    /// <summary>macOS: the app isn't signed yet, so Orrery only says there's a release.</summary>
    MacOS,

    /// <summary>Run through <c>dotnet</c> from a build, or anything else that isn't a package: Orrery only says there's a release.</summary>
    Development,
}

/// <summary>
/// What this copy of Orrery is, and the file an update replaces (<see cref="TargetPath"/>: the executable of a
/// portable copy, the AppImage file of an AppImage; null otherwise).
/// </summary>
public sealed record ThisCopy(CopyKind Kind, Architecture Architecture, string? TargetPath = null)
{
    /// <summary>The variable an AppImage's runtime sets to the path of the .AppImage file.</summary>
    public const string AppImageVariable = "APPIMAGE";

    /// <summary>Whether Orrery can download and apply an update itself, rather than only say there is one.</summary>
    public bool UpdatesItself => AssetName(new ReleaseVersion(0, 0, 0)) is not null;

    /// <summary>The running copy, from its storage mode, the OS, the process and <c>$APPIMAGE</c>.</summary>
    public static ThisCopy Detect(DataLocation location)
    {
        var os = OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsLinux() ? OSPlatform.Linux : OSPlatform.OSX;
        return Detect(location.Mode, os, RuntimeInformation.ProcessArchitecture, Environment.ProcessPath,
            Environment.GetEnvironmentVariable(AppImageVariable));
    }

    public static ThisCopy Detect(StorageMode mode, OSPlatform os, Architecture architecture, string? processPath, string? appImage)
    {
        if (os == OSPlatform.OSX)
        {
            return new(CopyKind.MacOS, architecture);
        }

        if (os == OSPlatform.Linux && appImage is { Length: > 0 } && Path.IsPathRooted(appImage))
        {
            return new(CopyKind.AppImage, architecture, appImage);
        }

        var executable = os == OSPlatform.Windows ? "Orrery.exe" : "Orrery";
        if (processPath is not { Length: > 0 } || !string.Equals(Path.GetFileName(processPath), executable,
                os == OSPlatform.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return new(CopyKind.Development, architecture);
        }

        return (os == OSPlatform.Windows, mode) switch
        {
            (true, StorageMode.Installed) => new(CopyKind.WindowsSetup, architecture),
            (true, StorageMode.Portable) => new(CopyKind.WindowsPortable, architecture, processPath),
            (false, StorageMode.Installed) => new(CopyKind.LinuxPackage, architecture),
            _ => new(CopyKind.LinuxPortable, architecture, processPath),
        };
    }

    /// <summary>
    /// The release file this copy updates from, exactly as the build scripts name it, or null when it doesn't update
    /// itself (or no package is built for its processor).
    /// </summary>
    public string? AssetName(ReleaseVersion version) => Kind switch
    {
        // The setup is x64 only; Windows on Arm runs it, and the app, emulated.
        CopyKind.WindowsSetup => $"Orrery-Setup-{version}.exe",
        CopyKind.WindowsPortable when Rid("win") is { } rid => $"Orrery-{version}-{rid}-portable.zip",
        CopyKind.LinuxPortable when Rid("linux") is { } rid => $"Orrery-{version}-{rid}-portable.zip",
        CopyKind.AppImage when Architecture == Architecture.X64 => $"Orrery-{version}-x86_64.AppImage",
        CopyKind.AppImage when Architecture == Architecture.Arm64 => $"Orrery-{version}-aarch64.AppImage",
        _ => null,
    };

    string? Rid(string os) => Architecture switch
    {
        Architecture.X64 => $"{os}-x64",
        Architecture.Arm64 => $"{os}-arm64",
        _ => null,
    };
}
