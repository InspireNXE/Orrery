namespace Orrery.Core.Storage;

public static class AtomicFile
{
    /// <summary>
    /// Writes to a temporary file, flushes it to disk, then moves it over the target, so a crash
    /// mid-write leaves either the old file or the new one, never half of each.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
