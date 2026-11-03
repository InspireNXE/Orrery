namespace Orrery.Core.Storage;

/// <summary>
/// The web tabs' browser profile in <see cref="FolderName"/> under the data folder: cookies, sign-ins, cache and site
/// storage, kept by the page engine. It isn't copied by Move data, only deleted with the old copy, since it's a deep
/// tree the engine owns; and it can be cleared from Settings. Only the folders the engines write are removed, never
/// through a link.
/// </summary>
public static class WebData
{
    public const string FolderName = "web";

    /// <summary>Left in the folder when it can't be cleared at once because a page engine has it open.</summary>
    public const string ClearMarker = "clear-at-start";

    /// <summary>What each engine writes there: WebView2 (Windows), and WebKitGTK's data and cache (Linux).</summary>
    static readonly string[] EngineFolders = ["EBWebView", "data", "cache"];

    public static string FolderIn(string dataDir) => Path.Combine(dataDir, FolderName);

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
        var web = new DirectoryInfo(FolderIn(dataDir));
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
