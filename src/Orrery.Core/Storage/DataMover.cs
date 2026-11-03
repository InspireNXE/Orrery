using System.Security.Cryptography;

namespace Orrery.Core.Storage;

/// <summary>
/// Copies the data folder somewhere else for Settings › Move data (design doc §2.7). Only Orrery's own files
/// (<see cref="DataFolderFiles"/>) move, links are never followed, and each file is checked with SHA-256
/// before anything points at the copy. The old folder is only removed on request.
/// </summary>
public static class DataMover
{
    public const string NestedMessage = "Choose a folder that isn't inside the current data folder and doesn't contain it.";

    /// <summary>More links than this in one path is a loop, or near enough.</summary>
    const int MaxLinkDepth = 32;

    /// <summary>Why <paramref name="target"/> can't receive the data, or null if it can.</summary>
    public static string? CheckTarget(string source, string target)
    {
        var from = Resolve(source);
        var to = Resolve(target);
        if (IsRoot(from))
        {
            return "Orrery's data is at the top of a drive, so Move data can't keep it apart from everything else there. Move the files by hand instead.";
        }

        if (IsRoot(to))
        {
            return "Choose a folder, not the top of a drive.";
        }

        if (IsSame(from, to))
        {
            return "That's the folder Orrery already uses.";
        }

        if (IsInside(to, from) || IsInside(from, to))
        {
            return NestedMessage;
        }

        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
        {
            return "Choose an empty folder, so nothing already there is overwritten.";
        }

        return DataLocationResolver.IsWritable(to) ? null : "Orrery can't write to that folder.";
    }

    /// <summary>
    /// A warning to confirm before moving to <paramref name="target"/>, or null. Call it after <see cref="CheckTarget"/>,
    /// which creates a missing target closed to other users, so only a folder the user already had can be open.
    /// </summary>
    public static string? WarnAboutTarget(string target) =>
        FolderAccess.IsOpenToOthers(target) ? FolderAccess.OpenToOthersWarning : null;

    /// <summary>
    /// Copies and verifies Orrery's files, then records <paramref name="source"/> in the copy so the restart
    /// can offer to delete it. On any failure the partial copy is removed and the exception rethrown.
    /// </summary>
    /// <returns>The number of files copied.</returns>
    public static int Copy(string source, string target)
    {
        if (CheckTarget(source, target) is { } problem)
        {
            throw new IOException(problem);
        }

        int count = 0;
        try
        {
            foreach (var file in Sort(source, copying: true).Owned)
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                PrivateStorage.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
                if (!Hash(file).AsSpan().SequenceEqual(Hash(destination)))
                {
                    throw new IOException($"{relative} didn't copy correctly.");
                }

                count++;
            }

            PendingCleanup.Record(target, source);
        }
        catch
        {
            TryEmpty(target);
            throw;
        }

        return count;
    }

    /// <summary>
    /// Deletes Orrery's files (<see cref="DataFolderFiles"/>) and the web tabs' data (<see cref="WebData"/>, which
    /// isn't copied) from the old folder, then its own folders and the old folder itself if they're left empty.
    /// Everything else stays: the installed-mode pointer, files and folders the user added, a nested
    /// <c>Portable</c> folder, and any link, which is never followed.
    /// </summary>
    /// <returns>What was left in place that isn't Orrery's, relative to <paramref name="oldDataDir"/>.</returns>
    public static IReadOnlyList<string> DeleteOldCopy(string oldDataDir)
    {
        if (IsRoot(Resolve(oldDataDir)))
        {
            throw new IOException("Orrery won't delete files from the top of a drive.");
        }

        // The old copy's cookies and sign-ins would otherwise stay behind, wherever that folder goes.
        WebData.Clear(oldDataDir);
        var (owned, others) = Sort(oldDataDir, copying: false);
        foreach (var file in owned)
        {
            File.Delete(file);
        }

        foreach (var (name, _) in DataFolderFiles.Folders)
        {
            var dir = new DirectoryInfo(Path.Combine(oldDataDir, name));
            if (dir.Exists && !IsLink(dir) && !dir.EnumerateFileSystemInfos().Any())
            {
                dir.Delete();
            }
        }

        if (!Directory.EnumerateFileSystemEntries(oldDataDir).Any())
        {
            Directory.Delete(oldDataDir);
        }

        return others;
    }

    /// <summary>Why the old copy mustn't be deleted now that Orrery uses <paramref name="currentDataDir"/>, or null if it can be.</summary>
    public static string? CheckOldCopy(string oldDataDir, string currentDataDir)
    {
        if (!Directory.Exists(oldDataDir))
        {
            return "The old folder isn't there any more.";
        }

        var old = Resolve(oldDataDir);
        var current = Resolve(currentDataDir);
        if (IsRoot(old))
        {
            return "The old folder is the top of a drive, so Orrery won't delete anything there.";
        }

        return IsSame(old, current) || IsInside(old, current) || IsInside(current, old)
            ? "The old folder is, contains, or is inside the folder Orrery uses now."
            : null;
    }

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary>
    /// Splits the top level of <paramref name="dir"/> into Orrery's files (full paths) and everything else
    /// (relative paths), looking one level into Orrery's own folders and never through a link. When copying,
    /// a link where one of Orrery's files belongs stops the move rather than leaving that file behind.
    /// </summary>
    static (List<string> Owned, List<string> Others) Sort(string dir, bool copying)
    {
        var owned = new List<string>();
        var others = new List<string>();
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (DataFolderFiles.IsUntouched(entry.Name))
            {
                continue;
            }

            if (entry is DirectoryInfo folder && DataFolderFiles.PatternFor(folder.Name) is { } pattern)
            {
                if (IsLink(folder))
                {
                    Keep(folder.Name);
                    continue;
                }

                foreach (var child in folder.EnumerateFileSystemInfos())
                {
                    var relative = Path.Combine(folder.Name, child.Name);
                    bool ours = child is FileInfo && (copying ? DataFolderFiles.IsCopiedFrom(pattern, child.Name) : DataFolderFiles.IsDeletedFrom(pattern, child.Name));
                    Place(child, relative, ours);
                }
            }
            else
            {
                bool ours = entry is FileInfo && (copying ? DataFolderFiles.IsCopied(entry.Name) : DataFolderFiles.IsDeleted(entry.Name));
                if (!ours && copying && DataFolderFiles.IsDeleted(entry.Name))
                {
                    continue; // Orrery's, but it stays with this folder
                }

                Place(entry, entry.Name, ours);
            }
        }

        return (owned, others);

        void Place(FileSystemInfo entry, string relative, bool ours)
        {
            if (!ours)
            {
                others.Add(relative);
            }
            else if (IsLink(entry))
            {
                Keep(relative);
            }
            else
            {
                owned.Add(entry.FullName);
            }
        }

        void Keep(string relative)
        {
            if (copying)
            {
                throw new IOException($"{relative} is a link to somewhere else, so Orrery won't move it. Replace it with the real files, then try again.");
            }

            others.Add(relative);
        }
    }

    /// <summary>A symbolic link or junction. Other reparse points (cloud placeholders, dedup) are ordinary files here.</summary>
    static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null;

    static byte[] Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return SHA256.HashData(stream);
    }

    /// <summary>The target was empty (or new) before the copy, so everything in it is ours to remove.</summary>
    static void TryEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// The full path with every existing link along it followed to its final target, so a junction or symlink
    /// alias spells a folder the same way as the folder itself. Parts that don't exist yet stay as written.
    /// </summary>
    static string Resolve(string path, int depth = 0)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var parts = Parts(full);
        var current = root;
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (depth < MaxLinkDepth && FinalTarget(current) is { } target)
            {
                return Resolve(Path.Combine([target, .. parts[(i + 1)..]]), depth + 1);
            }
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    static string? FinalTarget(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The root (a drive, share or <c>/</c>) followed by each folder name, for comparing segment by segment.</summary>
    static string[] Segments(string resolved) =>
        [Path.TrimEndingDirectorySeparator(Path.GetPathRoot(resolved)!), .. Parts(resolved)];

    static string[] Parts(string full) =>
        full[Path.GetPathRoot(full)!.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    static bool IsRoot(string resolved) => Segments(resolved).Length == 1;

    static bool IsSame(string a, string b) => Segments(a).SequenceEqual(Segments(b), PathComparer);

    /// <summary>True when <paramref name="path"/> is somewhere below <paramref name="folder"/>. A root counts as a folder.</summary>
    static bool IsInside(string path, string folder)
    {
        var inner = Segments(path);
        var outer = Segments(folder);
        return inner.Length > outer.Length && inner.Take(outer.Length).SequenceEqual(outer, PathComparer);
    }

    static StringComparer PathComparer => StringComparer.FromComparison(PathComparison);
}
