namespace Orrery.Protocols.Rdp;

/// <summary>A file or folder offered to the server: <paramref name="Path"/> here, and where it goes among the copied items.</summary>
/// <param name="RelativePath">The folder it is in among the copied items, '\' separated; empty for a copied item itself.</param>
sealed record LocalClipboardFile(string Path, string Name, string RelativePath, long Size, bool Directory, DateTime LastWriteUtc);

/// <summary>A file or folder the server copied. Its name and size are untrusted.</summary>
sealed record RdpRemoteFile(string Name, string RelativePath, long? Size, bool Directory);

/// <summary>What the server asks of an offered file: its size, or <paramref name="Length"/> bytes from <paramref name="Position"/>.</summary>
/// <param name="LockId">The clipDataId of the lock the request is made under, if any.</param>
sealed record RdpFileRequest(uint StreamId, int Index, bool SizeRequest, long Position, int Length, uint? LockId);

static class RdpClipboardFiles
{
    /// <summary>The most files and folders shared either way in one copy.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>
    /// The files and folders at <paramref name="paths"/> and everything in the folders, each folder before what it
    /// holds, or null when there are more than <see cref="MaxEntries"/>. Links are left out, so a link to a parent
    /// folder can't make it endless.
    /// </summary>
    public static List<LocalClipboardFile>? Collect(IReadOnlyList<string> paths)
    {
        var files = new List<LocalClipboardFile>();
        foreach (var path in paths)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            FileSystemInfo item = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            if (item.Exists && !Add(item, "", files))
            {
                return null;
            }
        }

        return files;
    }

    /// <returns>False when there are more than <see cref="MaxEntries"/>.</returns>
    static bool Add(FileSystemInfo item, string relativePath, List<LocalClipboardFile> files)
    {
        // A drive's root has no name of its own to give it.
        if (item.Attributes.HasFlag(FileAttributes.ReparsePoint) || item.Name.Length == 0 || item.Name.IndexOfAny(['/', '\\']) >= 0)
        {
            return true;
        }

        if (files.Count == MaxEntries)
        {
            return false;
        }

        if (item is FileInfo file)
        {
            files.Add(new(file.FullName, file.Name, relativePath, file.Length, false, file.LastWriteTimeUtc));
            return true;
        }

        var folder = (DirectoryInfo)item;
        files.Add(new(folder.FullName, folder.Name, relativePath, 0, true, folder.LastWriteTimeUtc));
        var inside = relativePath.Length == 0 ? folder.Name : relativePath + '\\' + folder.Name;
        FileSystemInfo[] children;
        try
        {
            children = folder.GetFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Shared empty, as the remote side would see a folder it can't open.
            return true;
        }

        foreach (var child in children)
        {
            if (!Add(child, inside, files))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Where a remote file goes under <paramref name="root"/>, or null when its name or folder isn't a plain name
    /// there: empty, "." or "..", rooted, or holding characters a file name can't have here.
    /// </summary>
    public static string? LocalPath(string root, RdpRemoteFile file)
    {
        var parts = new List<string>();
        if (file.RelativePath.Length > 0)
        {
            parts.AddRange(file.RelativePath.Split('\\'));
        }

        parts.Add(file.Name);
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.IndexOfAny(['/', '\\', ':', '\0']) >= 0
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Length > 255)
            {
                return null;
            }
        }

        var path = Path.GetFullPath(Path.Combine([root, .. parts]));
        var inside = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return path.StartsWith(inside, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? path : null;
    }

    /// <summary>A new folder only this user can open, for files the server copies.</summary>
    public static string CreatePrivateFolder()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Orrery", "Clipboard");
        var path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            // The user's temp folder is already private to them.
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>Deletes <paramref name="folder"/> and what it holds, quietly when it can't.</summary>
    public static void Delete(string? folder)
    {
        if (folder is null)
        {
            return;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file another app still has open; the temp folder is cleaned up eventually.
        }
    }

    /// <summary>A Windows file time as UTC, or null when it's 0 or out of range.</summary>
    public static DateTime? FromFileTime(ulong fileTime)
    {
        if (fileTime == 0 || fileTime > (ulong)DateTime.MaxValue.ToFileTimeUtc())
        {
            return null;
        }

        return DateTime.FromFileTimeUtc((long)fileTime);
    }
}
