using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;

namespace Orrery.Core.Recording;

/// <summary>
/// Deletes old recordings (docs/design-session-recording.md §4.3): those past the age limit, then the oldest until
/// what's left fits the size limit. Only <c>.orrec</c> files at the top of the folder are touched, never one being
/// written or played.
/// </summary>
public static class RecordingRetention
{
    static readonly ILogger Logger = Log.For("Recording");

    /// <summary>The recordings in <paramref name="folder"/>, newest first.</summary>
    public static IReadOnlyList<FileInfo> List(string folder)
    {
        var directory = new DirectoryInfo(folder);
        if (!directory.Exists)
        {
            return [];
        }

        try
        {
            return directory.EnumerateFiles("*" + RecordingFile.Extension, SearchOption.TopDirectoryOnly)
                .Where(f => f.Extension.Equals(RecordingFile.Extension, StringComparison.OrdinalIgnoreCase) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <returns>The files deleted.</returns>
    public static IReadOnlyList<string> Apply(string folder, TimeSpan? keepFor, long maxTotalBytes, DateTime nowUtc, IReadOnlySet<string> inUse)
    {
        var deleted = new List<string>();
        var kept = new List<FileInfo>();
        foreach (var file in List(folder))
        {
            if (keepFor is { } age && nowUtc - file.LastWriteTimeUtc > age && !InUse(file, inUse))
            {
                if (TryDelete(file))
                {
                    deleted.Add(file.FullName);
                    continue;
                }
            }

            kept.Add(file);
        }

        long total = kept.Sum(f => f.Length);
        // Oldest first, until the rest fits.
        foreach (var file in kept.AsEnumerable().Reverse())
        {
            if (total <= maxTotalBytes)
            {
                break;
            }

            long length = file.Length;
            if (!InUse(file, inUse) && TryDelete(file))
            {
                deleted.Add(file.FullName);
                total -= length;
            }
        }

        if (deleted.Count > 0)
        {
            Logger.LogInformation("Deleted {Count} old recordings", deleted.Count);
        }

        return deleted;
    }

    static bool InUse(FileInfo file, IReadOnlySet<string> inUse) =>
        inUse.Contains(file.FullName) || inUse.Any(p => string.Equals(Path.GetFullPath(p), file.FullName, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));

    static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Open somewhere else, or not ours to delete: try again next time.
            return false;
        }
    }
}
