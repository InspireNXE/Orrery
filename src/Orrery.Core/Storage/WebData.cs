namespace Orrery.Core.Storage;

/// <summary>
/// The web tabs' browser profile in <see cref="FolderName"/> under the data folder: cookies, sign-ins, cache and site
/// storage, kept by the page engine. It isn't copied by Move data, only deleted with the old copy, since it's a deep
/// tree the engine owns; and it can be cleared from Settings. Only the folders the engines write are removed, never
/// through a link.
/// <para>
/// Each computer has its own, in <c>web/&lt;tag&gt;</c> (<see cref="ComputerFiles.Tag"/>): a data folder a sync app
/// shares between computers would otherwise have two page engines in one profile, and WebView2 encrypts cookies with a
/// key only the computer that wrote them can read.
/// </para>
/// </summary>
public static class WebData
{
    public const string FolderName = "web";

    /// <summary>Left in the folder when it can't be cleared at once because a page engine has it open.</summary>
    public const string ClearMarker = "clear-at-start";

    /// <summary>What each engine writes there: WebView2 (Windows), and WebKitGTK's data and cache (Linux).</summary>
    static readonly string[] EngineFolders = ["EBWebView", "data", "cache"];

    /// <summary>This computer's profile in <paramref name="dataDir"/>.</summary>
    public static string FolderIn(string dataDir) => Path.Combine(dataDir, FolderName, ComputerFiles.Tag);

    /// <summary>
    /// At start, before any page engine opens it: a profile from before profiles were per computer, at the top of the
    /// web folder, becomes this computer's, as this is most likely the computer that made it. Never throws.
    /// </summary>
    public static void MoveOldProfile(string dataDir)
    {
        var top = Path.Combine(dataDir, FolderName);
        var own = FolderIn(dataDir);
        try
        {
            if (!Directory.Exists(top) || new DirectoryInfo(top).LinkTarget is not null)
            {
                return;
            }

            foreach (var name in EngineFolders.Append(ClearMarker))
            {
                var from = Path.Combine(top, name);
                var to = Path.Combine(own, name);
                if ((Directory.Exists(from) || File.Exists(from)) && !Directory.Exists(to) && !File.Exists(to))
                {
                    PrivateStorage.CreateDirectory(own);
                    if (Directory.Exists(from))
                    {
                        Directory.Move(from, to);
                    }
                    else
                    {
                        File.Move(from, to);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Whether <paramref name="dataDir"/> has web data to clear.</summary>
    public static bool Exists(string dataDir)
    {
        var web = new DirectoryInfo(FolderIn(dataDir));
        return web.Exists && web.LinkTarget is null && EngineFolders.Any(name => Directory.Exists(Path.Combine(web.FullName, name)));
    }

    /// <summary>
    /// Deletes the engines' folders from <paramref name="dataDir"/>'s web folder, then the folder itself if nothing
    /// else is in it. A link is deleted as a link, never followed.
    /// </summary>
    /// <exception cref="IOException">A page engine still has its files open.</exception>
    public static void Clear(string dataDir)
    {
        ClearFolder(FolderIn(dataDir));
        DeleteIfEmpty(Path.Combine(dataDir, FolderName));
    }

    /// <summary>Clears every computer's profile in <paramref name="dataDir"/>, and one from before they were per computer: for an old copy deleted.</summary>
    /// <exception cref="IOException">A page engine still has its files open.</exception>
    public static void ClearAll(string dataDir)
    {
        var top = new DirectoryInfo(Path.Combine(dataDir, FolderName));
        if (!top.Exists || top.LinkTarget is not null)
        {
            return;
        }

        foreach (var computer in top.EnumerateDirectories().Where(d => d.Name.Length == 6 && d.Name.All(char.IsAsciiHexDigitLower)))
        {
            ClearFolder(computer.FullName);
        }

        ClearFolder(top.FullName);
    }

    static void DeleteIfEmpty(string folder)
    {
        var info = new DirectoryInfo(folder);
        if (info.Exists && info.LinkTarget is null && !info.EnumerateFileSystemInfos().Any())
        {
            info.Delete();
        }
    }

    static void ClearFolder(string path)
    {
        var web = new DirectoryInfo(path);
        if (!web.Exists || web.LinkTarget is not null)
        {
            return;
        }

        foreach (var name in EngineFolders)
        {
            var folder = new DirectoryInfo(Path.Combine(web.FullName, name));
            if (!folder.Exists)
            {
                continue;
            }

            if (folder.LinkTarget is not null)
            {
                folder.Delete();
            }
            else
            {
                folder.Delete(recursive: true);
            }
        }

        File.Delete(Path.Combine(web.FullName, ClearMarker));
        if (!web.EnumerateFileSystemInfos().Any())
        {
            web.Delete();
        }
    }

    /// <summary>Clears the web data at the next start, before any page engine opens it.</summary>
    public static void ClearAtStart(string dataDir)
    {
        PrivateStorage.CreateDirectory(FolderIn(dataDir));
        File.WriteAllText(Path.Combine(FolderIn(dataDir), ClearMarker), string.Empty);
    }

    /// <summary>At start: clears the web data if <see cref="ClearAtStart"/> asked for it. Never throws.</summary>
    public static bool ClearIfAsked(string dataDir)
    {
        if (!File.Exists(Path.Combine(FolderIn(dataDir), ClearMarker)))
        {
            return false;
        }

        try
        {
            Clear(dataDir);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
