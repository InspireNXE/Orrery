namespace Orrery.Core.Storage;

/// <summary>The outcome of resolving storage at startup.</summary>
public abstract record StartupStorage
{
    public sealed record Ready(DataLocation Location) : StartupStorage;

    /// <summary>Portable mode with no choice made yet: ask before opening the main window.</summary>
    public sealed record NeedsPortableChoice(string AppDirectory) : StartupStorage;
}

public static class DataLocationResolver
{
    public static StartupStorage Resolve(string appDirectory, PlatformFolders folders)
    {
        var marker = PortableMarker.TryLoad(appDirectory);
        if (marker is null)
        {
            var dataDir = InstalledLocationPointer.TryLoad(folders.Config)?.DataDir ?? folders.Config;
            return new StartupStorage.Ready(new DataLocation(StorageMode.Installed, dataDir, folders.Cache, folders.Logs));
        }

        return marker.Choice is { } choice
            ? new StartupStorage.Ready(ForPortable(appDirectory, folders, choice, marker.CustomPath))
            : new StartupStorage.NeedsPortableChoice(appDirectory);
    }

    /// <summary>In portable mode cache and logs live under the data folder, so nothing is left on the host.</summary>
    public static DataLocation ForPortable(string appDirectory, PlatformFolders folders, PortableDataChoice choice,
        string? customPath = null)
    {
        var dataDir = choice switch
        {
            PortableDataChoice.WithApp => Path.Combine(appDirectory, "data"),
            PortableDataChoice.UserProfile => Path.Combine(folders.Config, "Portable"),
            _ => Path.GetFullPath(customPath ?? throw new ArgumentNullException(nameof(customPath)), appDirectory),
        };

        return new DataLocation(StorageMode.Portable, dataDir, Path.Combine(dataDir, "cache"), Path.Combine(dataDir, "logs"));
    }

    /// <summary>
    /// Store a custom folder relative to the app folder when they share a drive, so the choice survives
    /// a USB stick getting a different drive letter or mount point.
    /// </summary>
    public static string ToStoredCustomPath(string appDirectory, string chosenFolder)
    {
        var full = Path.GetFullPath(chosenFolder);
        return string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(Path.GetFullPath(appDirectory)), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(appDirectory, full)
            : full;
    }

    /// <summary>
    /// The folder that holds the app. On macOS that's the folder holding the .app bundle, never the
    /// signed bundle itself.
    /// </summary>
    public static string AppDirectory(string baseDirectory)
    {
        var dir = Path.TrimEndingDirectorySeparator(baseDirectory);
        var macOs = Path.GetDirectoryName(dir);
        if (Path.GetFileName(dir) == "MacOS"
            && Path.GetFileName(macOs) == "Contents"
            && Path.GetDirectoryName(macOs) is { } bundle
            && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(bundle)!;
        }

        return dir;
    }

    /// <summary>
    /// Creates and deletes a probe file. Fails on read-only media, Program Files, and macOS App
    /// Translocation (an app run straight from a quarantined download).
    /// </summary>
    public static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".orrery-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
