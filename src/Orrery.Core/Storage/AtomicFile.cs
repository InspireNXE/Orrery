using System.Security.Cryptography;

namespace Orrery.Core.Storage;

public static class AtomicFile
{
    const string TempExtension = ".tmp";
    const int RandomBytes = 8;

    /// <summary>
    /// Writes to a temporary file, flushes it to disk, then moves it over the target, so a crash
    /// mid-write leaves either the old file or the new one, never half of each. The temp file has a
    /// random name and is created fresh (so a link or file already there is never followed or
    /// overwritten), and on Linux and macOS the file and any new folder are owner-only.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        PrivateStorage.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = $"{path}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(RandomBytes))}{TempExtension}";
        using var stream = new FileStream(temp, PrivateStorage.Private(new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        }));

        // Past this point the temp file is ours; if anything fails, don't leave it behind.
        try
        {
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write(contents);
                writer.Flush();
            }

            stream.Flush(flushToDisk: true);
            stream.Dispose();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            stream.Dispose();
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// The file a temp file named <paramref name="name"/> was being written for, or null if it isn't one
    /// of ours: <c>target.&lt;random hex&gt;.tmp</c> now, <c>target.tmp</c> in earlier versions.
    /// </summary>
    public static string? TargetOfTemp(string name)
    {
        if (!name.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var inner = name[..^TempExtension.Length];
        int dot = inner.LastIndexOf('.');
        return dot > 0 && inner.Length - dot - 1 == RandomBytes * 2 && inner[(dot + 1)..].All(Uri.IsHexDigit)
            ? inner[..dot]
            : inner;
    }
}
