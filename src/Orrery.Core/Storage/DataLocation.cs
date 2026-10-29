namespace Orrery.Core.Storage;

public enum StorageMode
{
    Installed,
    Portable,
}

/// <summary>
/// Where Orrery keeps its files, decided once at startup. Nothing else computes paths.
/// See docs/design-storage-vault-import.md §2.
/// </summary>
public sealed record DataLocation(StorageMode Mode, string DataDir, string CacheDir, string LogDir)
{
    /// <summary>Portable mode can't rely on the OS keychain, which doesn't travel with the app.</summary>
    public bool UsesOsKeychain => Mode == StorageMode.Installed;

    public string FileIn(string name) => Path.Combine(DataDir, name);
}

/// <summary>The per-user base folders for the current OS. Tests supply their own.</summary>
public sealed record PlatformFolders(string Config, string Cache, string Logs)
{
    const string AppName = "Orrery";
    const string LinuxName = "orrery";

    public static PlatformFolders ForCurrentOs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName),
                Path.Combine(local, AppName, "Cache"),
                Path.Combine(local, AppName, "Logs"));
        }

        if (OperatingSystem.IsMacOS())
        {
            var library = Path.Combine(home, "Library");
            return new(
                Path.Combine(library, "Application Support", AppName),
                Path.Combine(library, "Caches", AppName),
                Path.Combine(library, "Logs", AppName));
        }

        return new(
            Path.Combine(XdgDir("XDG_CONFIG_HOME", home, ".config"), LinuxName),
            Path.Combine(XdgDir("XDG_CACHE_HOME", home, ".cache"), LinuxName),
            Path.Combine(XdgDir("XDG_STATE_HOME", home, Path.Combine(".local", "state")), LinuxName, "logs"));
    }

    static string XdgDir(string variable, string home, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dir && Path.IsPathRooted(dir)
            ? dir
            : Path.Combine(home, fallback);
}
