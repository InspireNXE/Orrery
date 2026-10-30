using System.Security.Cryptography;

namespace Orrery.Core.Storage;

/// <summary>
/// Copies the data folder somewhere else for Settings › Move data (design doc §2.7). Each file is checked
/// with SHA-256 before anything points at the copy, and the old folder is only removed on request.
/// </summary>
public static class DataMover
{
    /// <summary>Files in the top folder that belong to the running copy of Orrery, not to the data.</summary>
    static readonly string[] Skipped = [".lock", InstalledLocationPointer.FileName];

    /// <summary>Why <paramref name="target"/> can't receive the data, or null if it can.</summary>
    public static string? CheckTarget(string source, string target)
    {
        var from = Normalize(source);
        var to = Normalize(target);
        if (SamePath(from, to))
        {
            return "That's the folder Orrery already uses.";
        }

        if (IsInside(to, from) || IsInside(from, to))
        {
            return "Choose a folder that isn't inside the current data folder and doesn't contain it.";
        }

        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
        {
            return "Choose an empty folder, so nothing already there is overwritten.";
        }

        return DataLocationResolver.IsWritable(to) ? null : "Orrery can't write to that folder.";
    }

    /// <summary>Copies and verifies. On any failure the partial copy is removed and the exception rethrown.</summary>
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
            foreach (var file in DataFiles(source))
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
                if (!Hash(file).AsSpan().SequenceEqual(Hash(destination)))
                {
                    throw new IOException($"{relative} didn't copy correctly.");
                }

                count++;
            }
        }
        catch
        {
            TryEmpty(target);
            throw;
        }

        return count;
    }

    /// <summary>
    /// Deletes the data files from the old folder, then any folders left empty. Anything that isn't
    /// Orrery's (the installed-mode pointer, files the user added) stays.
    /// </summary>
    public static void DeleteOldCopy(string oldDataDir)
    {
        foreach (var file in DataFiles(oldDataDir).ToList())
        {
            File.Delete(file);
        }

        foreach (var dir in Directory.EnumerateDirectories(oldDataDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }

        if (!Directory.EnumerateFileSystemEntries(oldDataDir).Any())
        {
            Directory.Delete(oldDataDir);
        }
    }

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), PathComparison);

    static IEnumerable<string> DataFiles(string dir)
    {
        var top = Normalize(dir);
        return Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories)
            .Where(f => !(Path.GetDirectoryName(f) == top && Skipped.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)));
    }

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

    static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);
}
