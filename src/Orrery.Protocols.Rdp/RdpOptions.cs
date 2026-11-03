namespace Orrery.Protocols.Rdp;

/// <summary>
/// What a connection asks of the server beyond the desktop: where its sound goes, folders of this computer to show
/// there as drives, and whether to join the console (administrative) session.
/// </summary>
public sealed record RdpOptions
{
    /// <summary>Play the remote computer's sound here; when false the server is asked not to send any.</summary>
    public bool PlayAudio { get; init; } = true;

    /// <summary>Folders shown on the remote computer as drives; see <see cref="RdpDrives.Plan"/>.</summary>
    public IReadOnlyList<SharedDrive> Drives { get; init; } = [];

    /// <summary>Join the console session, as <c>mstsc /admin</c> does.</summary>
    public bool AdminSession { get; init; }
}

/// <summary>A folder of this computer, shown on the remote computer as a drive called <paramref name="Name"/>.</summary>
public readonly record struct SharedDrive(string Name, string Path);

/// <summary>
/// Turns a connection's drive settings into the folders to share, each as its own drive. An IronRDP before
/// <c>2026.10.29-orrery.10</c> shares only one on Linux and macOS, so there the first is shared and the rest are reported.
/// </summary>
public static class RdpDrives
{
    /// <summary>The folders to share on this computer, with what couldn't be shared added to <paramref name="problems"/>.</summary>
    public static IReadOnlyList<SharedDrive> ForThisComputer(bool shareAll, IReadOnlyList<string> folders, List<string> problems)
    {
        if (!shareAll && folders.Count == 0)
        {
            return [];
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool multiple = OperatingSystem.IsWindows() || LibrarySharesSeveral();
        // Off Windows, every drive is the home folder: the rest of the file system isn't the user's to share.
        IReadOnlyList<string> roots = OperatingSystem.IsWindows() ? LocalDriveRoots() : [home];
        return Plan(shareAll, folders, home, roots, multiple, Directory.Exists, problems);
    }

    /// <summary>Whether the IronRDP library loaded can share more than one folder here.</summary>
    static bool LibrarySharesSeveral()
    {
        try
        {
            return Devolutions.IronRdp.DriveRedirection.SupportsMultipleDrives();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <param name="shareAll">
    /// Share every drive: each of <paramref name="driveRoots"/> where several can be shared, the home folder where
    /// only one can. The folders aren't shared as well, since they're on those drives.
    /// </param>
    /// <param name="folders">Folders as set, where a leading ~ is the home folder.</param>
    /// <param name="multiple">Whether more than one folder can be shared.</param>
    public static IReadOnlyList<SharedDrive> Plan(bool shareAll, IReadOnlyList<string> folders, string home, IReadOnlyList<string> driveRoots,
        bool multiple, Func<string, bool> folderExists, List<string> problems)
    {
        var paths = shareAll
            ? multiple ? driveRoots.ToList() : [home]
            : folders.Select(f => Expand(f, home)).ToList();

        var drives = new List<SharedDrive>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                problems.Add($"{path} is on the network, and only folders on this computer's own drives can be shared.");
                continue;
            }

            if (!System.IO.Path.IsPathFullyQualified(path) || !folderExists(path))
            {
                problems.Add($"{path} isn't a folder on this computer, so it isn't shared.");
                continue;
            }

            if (!multiple && drives.Count == 1)
            {
                problems.Add($"Only one folder can be shared from this computer, so {path} isn't (only {drives[0].Path} is).");
                continue;
            }

            drives.Add(new SharedDrive(UniqueName(NameFor(path), names), path));
        }

        return drives;
    }

    /// <summary>A leading ~ as the home folder: "~", "~/Shared" or "~\Shared".</summary>
    internal static string Expand(string folder, string home)
    {
        var trimmed = folder.Trim();
        if (trimmed == "~")
        {
            return home;
        }

        return trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith(@"~\", StringComparison.Ordinal)
            ? System.IO.Path.Combine(home, trimmed[2..])
            : trimmed;
    }

    /// <summary>What the remote computer calls the drive: its letter for a drive's root ("C"), else the folder's own name.</summary>
    internal static string NameFor(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]))
        {
            return char.ToUpperInvariant(trimmed[0]).ToString();
        }

        int cut = trimmed.LastIndexOfAny(['/', '\\']);
        var name = (cut < 0 ? trimmed : trimmed[(cut + 1)..]).Replace("\0", string.Empty);
        return name.Length == 0 ? "Root" : name;
    }

    /// <summary>Names must differ, ignoring case: a second "Shared" is "Shared 2".</summary>
    static string UniqueName(string name, HashSet<string> taken)
    {
        var unique = name;
        for (int n = 2; !taken.Add(unique); n++)
        {
            unique = $"{name} {n}";
        }

        return unique;
    }

    /// <summary>The roots of this computer's drives that are ready, local and fixed or removable: C:\, D:\.</summary>
    static IReadOnlyList<string> LocalDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
