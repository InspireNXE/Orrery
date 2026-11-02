using System.IO.Enumeration;
using Orrery.Core.Connections;
using Orrery.Core.Settings;

namespace Orrery.Core.Storage;

/// <summary>
/// The files Orrery itself writes in a data folder (design doc §2.4). Move data copies and deletes only
/// these, so anything else in the folder (the user's own files, a nested <c>Portable</c> folder, a link to
/// somewhere else) is left alone. Keep this in step with every <c>location.FileIn(…)</c> and
/// <c>AtomicFile.WriteAllText</c> that writes into the data folder.
/// </summary>
public static class DataFolderFiles
{
    /// <summary>Names (or patterns) at the top of the folder that are copied, and deleted with an old copy.</summary>
    public static readonly IReadOnlyList<string> Files =
    [
        "connections.json",
        "connections.json.unreadable-*", // set aside at startup when connections.json can't be read
        AppSettingsStore.FileName,
        RecentConnections.FileName,
        ConnectionHistory.FileName,
        WorkspaceStore.FileName,
        "vault.frv",
        "credentials.json",
        "known-hosts.json",
        "trusted-certificates.json",
    ];

    /// <summary>Folders Orrery writes, and the only files in them it touches. Nothing deeper.</summary>
    public static readonly IReadOnlyList<(string Name, string Pattern)> Folders =
    [
        ("backups", "connections-*.json"),
        ("import", "mremoteng-*.md"),
        ("recordings", "*.orrec"),
    ];

    /// <summary>
    /// Orrery's, but about the folder they sit in: deleted with an old copy, never copied. Interrupted
    /// <see cref="AtomicFile"/> writes (<c>name.&lt;random&gt;.tmp</c>, or <c>name.tmp</c> from earlier versions) count too.
    /// </summary>
    public static readonly IReadOnlyList<string> DeleteOnly = [PendingCleanup.FileName];

    /// <summary>Belong to the running app or the install rather than the data: never copied or deleted.</summary>
    public static readonly IReadOnlyList<string> Untouched = [".lock", InstalledLocationPointer.FileName];

    public static bool IsCopied(string name) => MatchesAny(Files, name);

    public static bool IsDeleted(string name) =>
        IsCopied(name) || MatchesAny(DeleteOnly, name) || IsTempOf(name, IsCopied) || IsTempOf(name, n => MatchesAny(DeleteOnly, n));

    public static bool IsUntouched(string name) => MatchesAny(Untouched, name);

    /// <summary>The pattern for files inside <paramref name="folder"/>, or null if Orrery doesn't own that folder.</summary>
    public static string? PatternFor(string folder) =>
        Folders.FirstOrDefault(f => Matches(f.Name, folder)) is { Name: not null } match ? match.Pattern : null;

    public static bool IsCopiedFrom(string pattern, string name) => Matches(pattern, name);

    public static bool IsDeletedFrom(string pattern, string name) =>
        Matches(pattern, name) || IsTempOf(name, n => Matches(pattern, n));

    static bool IsTempOf(string name, Func<string, bool> owned) =>
        AtomicFile.TargetOfTemp(name) is { } target && owned(target);

    static bool MatchesAny(IEnumerable<string> patterns, string name) => patterns.Any(p => Matches(p, name));

    static bool Matches(string pattern, string name) =>
        FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: !OperatingSystem.IsLinux());
}
