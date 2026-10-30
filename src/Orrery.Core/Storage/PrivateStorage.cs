namespace Orrery.Core.Storage;

/// <summary>
/// Owner-only modes for what Orrery writes on Linux and macOS (0600 files, 0700 folders), so another
/// user on the machine can't read the vault, the saved credentials or the connection list. .NET would
/// otherwise create files 0666 and folders 0777 before the umask. Windows has no Unix modes; every
/// method here does nothing there (folder permissions on Windows are a separate concern).
/// </summary>
public static class PrivateStorage
{
    const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode FolderMode = FileMode | UnixFileMode.UserExecute;
    const UnixFileMode GroupOtherAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Makes a file opened with <paramref name="options"/> owner-only if it has to be created.</summary>
    public static FileStreamOptions Private(FileStreamOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }

        return options;
    }

    /// <summary>Creates <paramref name="path"/> and any missing parents, owner-only on Linux and macOS.</summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, FolderMode);
        }
    }

    /// <summary>For a file made another way (a copy keeps the original's mode).</summary>
    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FileMode);
        }
    }

    /// <summary>
    /// Closes an existing folder to group and others. Best effort: a folder the user doesn't own can't be
    /// changed, and that mustn't stop Orrery starting. Files inside are left alone; new saves set their own.
    /// </summary>
    public static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if ((File.GetUnixFileMode(path) & GroupOtherAccess) != 0)
            {
                File.SetUnixFileMode(path, FolderMode);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left as it is.
        }
    }
}
