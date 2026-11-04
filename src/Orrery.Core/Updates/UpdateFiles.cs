using System.IO.Compression;
using System.Reflection;

namespace Orrery.Core.Updates;

/// <summary>
/// The file side of applying an update to a portable copy or an AppImage: the running file is renamed aside (which
/// Windows and Unix both allow for a running executable), the new one takes its name, and the one set aside is deleted
/// at the next start. Nothing else in the app folder is touched: not <c>orrery.portable.json</c>, not the data folder.
/// </summary>
static class UpdateFiles
{
    /// <summary>Files beside a portable executable that come from the release zip too.</summary>
    static readonly string[] Alongside = ["LICENSE", "THIRD-PARTY-NOTICES.txt"];

    /// <summary>The most a file unpacked from a release zip may hold.</summary>
    const long MaxUnpackedBytes = 1024L * 1024 * 1024;

    const UnixFileMode Readable = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    /// <summary>Where the running file goes while the new one takes its place: Orrery.old.exe, Orrery.old.</summary>
    public static string KeptAsideName(string path) => WithTag(path, "old");

    /// <summary>The new file beside the old one, before the rename that swaps them: Orrery.new.exe, Orrery.new.</summary>
    public static string IncomingName(string path) => WithTag(path, "new");

    /// <summary>
    /// Unpacks the executable, LICENSE and THIRD-PARTY-NOTICES.txt from a portable zip into
    /// <paramref name="stagingDir"/>, then puts them in place of those beside <paramref name="executablePath"/>.
    /// </summary>
    public static void ApplyPortable(string zipPath, string executablePath, string stagingDir)
    {
        var executable = executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? "Orrery.exe" : "Orrery";
        var unpacked = Path.Combine(stagingDir, "unpacked");
        try
        {
            if (Directory.Exists(unpacked))
            {
                Directory.Delete(unpacked, recursive: true);
            }

            Directory.CreateDirectory(unpacked);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var name in Alongside.Prepend(executable))
            {
                // Top level only: GetEntry matches the whole entry name.
                if (zip.GetEntry(name) is not { } entry)
                {
                    if (name == executable)
                    {
                        throw new UpdateException($"The download has no {executable} in it.");
                    }

                    continue;
                }

                if (entry.Length > MaxUnpackedBytes)
                {
                    throw new UpdateException($"{name} in the download is larger than expected.");
                }

                entry.ExtractToFile(Path.Combine(unpacked, name));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new UpdateException($"The download couldn't be unpacked: {e.Message}", e);
        }

        var folder = Path.GetDirectoryName(executablePath)!;
        Replace(executablePath, Path.Combine(unpacked, executable),
            Alongside.Select(name => Path.Combine(unpacked, name)).Where(File.Exists).Select(from => (from, Path.Combine(folder, Path.GetFileName(from)))));
    }

    /// <summary>
    /// <paramref name="target"/> is this process's own single-file executable. Such an app reads an assembly it hasn't
    /// loaded yet from whatever file is at its path, so once a new build is there it can't load one: starting the new
    /// Orrery, or opening a folder, failed with a FileNotFoundException for System.Diagnostics.Process.
    /// </summary>
    static bool RunsFromBundle(string target) =>
        string.IsNullOrEmpty(typeof(UpdateFiles).Assembly.Location) && Environment.ProcessPath is { } running
        && string.Equals(Path.GetFullPath(running), Path.GetFullPath(target), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Loads every assembly the app refers to, and those they refer to, while they're still in the running file, so
    /// Orrery can start the new one and carry on until it does (or until it quits, if the update waits for that).
    /// </summary>
    internal static void LoadEveryAssembly()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>(AppDomain.CurrentDomain.GetAssemblies());
        while (queue.TryDequeue(out var assembly))
        {
            foreach (var name in assembly.GetReferencedAssemblies())
            {
                if (!seen.Add(name.FullName))
                {
                    continue;
                }

                try
                {
                    queue.Enqueue(Assembly.Load(name));
                }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    // Referred to but never shipped (a facade for another platform, say); nothing will ask for it.
                }
            }
        }
    }

    /// <summary>
    /// Puts <paramref name="replacement"/> in place of <paramref name="target"/>, then copies each of
    /// <paramref name="alongside"/> over its destination. The replacement is first copied beside the target, so the
    /// swap itself is two renames in one folder. If any step fails, the old target is put back.
    /// </summary>
    public static void Replace(string target, string replacement, IEnumerable<(string From, string To)> alongside)
    {
        var aside = KeptAsideName(target);
        var incoming = IncomingName(target);
        try
        {
            File.Delete(incoming);
            File.Delete(aside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"{Path.GetFileName(aside)} from an earlier update is still in use. Restart Orrery and try again.", e);
        }

        bool setAside = false;
        try
        {
            File.Copy(replacement, incoming);
            if (RunsFromBundle(target))
            {
                LoadEveryAssembly();
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(incoming, Executable(File.GetUnixFileMode(target)));
            }

            File.Move(target, aside);
            setAside = true;
            File.Move(incoming, target);
            foreach (var (from, to) in alongside)
            {
                File.Copy(from, to, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var name = Path.GetFileName(target);
            try
            {
                if (setAside)
                {
                    File.Delete(target);
                    File.Move(aside, target);
                }

                File.Delete(incoming);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException($"Couldn't replace {name} ({e.Message}), nor put it back: the old one is {aside}.", e);
            }

            throw new UpdateException($"Couldn't replace {name}: {e.Message} Nothing was changed.", e);
        }
    }

    /// <summary>Deletes a file set aside or left half way by an earlier update. Best effort: it's tried again next start.</summary>
    public static void RemoveLeftovers(string target)
    {
        foreach (var path in new[] { KeptAsideName(target), IncomingName(target) })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still running, most likely: the previous Orrery hasn't finished exiting.
            }
        }
    }

    /// <summary>The mode with execute added wherever read is allowed, so the new file runs for whoever ran the old one.</summary>
    static UnixFileMode Executable(UnixFileMode mode)
    {
        mode |= UnixFileMode.UserRead | UnixFileMode.UserExecute;
        if ((mode & UnixFileMode.GroupRead) != 0)
        {
            mode |= UnixFileMode.GroupExecute;
        }

        if ((mode & UnixFileMode.OtherRead) != 0)
        {
            mode |= UnixFileMode.OtherExecute;
        }

        return mode & (Readable | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    // Orrery.exe → Orrery.old.exe; Orrery → Orrery.old; Orrery-0.1.0-x86_64.AppImage → Orrery-0.1.0-x86_64.old.AppImage.
    static string WithTag(string path, string tag) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.{tag}{Path.GetExtension(path)}");
}
