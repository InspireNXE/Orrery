using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Storage;

namespace Orrery.Core.Connections;

/// <summary>
/// Reads and writes <c>connections.json</c>. Holds no secrets: connections refer to saved credentials by id.
/// Before a save, the previous file is copied into <c>backups/</c> if the newest backup is older than
/// <paramref name="backupInterval"/>, so frequent small saves don't push useful backups out.
/// <para>
/// The data folder may be shared with other computers through a sync app, so the file can change under the open tree.
/// The store remembers the file as it last read or wrote it; a save, or <see cref="ReloadIfChanged"/>, finding it
/// different merges the file's changes into the tree first (<see cref="ConnectionMerge"/>), so neither computer's
/// changes are saved over. A save with nothing new writes nothing.
/// </para>
/// <para>
/// Fields that belong to one computer (<see cref="PerComputerFields"/>) are kept apart, in that computer's own file
/// at <paramref name="computerValuesPath"/>; none, and they're saved in connections.json like the rest.
/// </para>
/// </summary>
public sealed class ConnectionStore(string path, string backupDirectory, int backupsToKeep = 10, TimeSpan? backupInterval = null,
    string? computerValuesPath = null)
{
    public const string ComputerValuesFile = "connections.json";

    readonly PerComputerFields? _fields = computerValuesPath is null ? null : new PerComputerFields(new ComputerValues(computerValuesPath));

    static readonly ILogger Logger = Log.For("Storage");

    readonly TimeSpan _backupInterval = backupInterval ?? TimeSpan.FromMinutes(10);

    // The file as last read or written here. Null: there was none.
    string? _known;

    // The versions of the file this computer read or wrote lately, by VersionHash, oldest first. A merge measures both
    // sides against the newest of them the file on disk was saved from (FileModel.History): with a sync app, that isn't
    // always the one this computer saw last. When both computers saved at once, the app keeps one file under the real
    // name and makes the other a conflict copy, and the one kept was saved from what both had before.
    readonly Dictionary<string, string> _versions = [];
    readonly Queue<string> _versionOrder = [];
    const int VersionsKept = 20;

    // How many versions back a file says it was saved from.
    const int HistoryLength = 20;

    // 2 added hosts' guests; 3 added the serial and web protocols and the settings in InheritableSettings.AddedInSchema3;
    // 4 added saved commands and InheritableSettings.AddedInSchema4; 5 added commands sent when connected and answering a password prompt;
    // 6 added local shells as connections.
    // A file is written with the oldest schema that holds it, so older builds keep reading a tree that uses nothing
    // newer, and refuse one that does instead of loading it and dropping what they don't know on save.
    const int SchemaVersion = 6;
    const int SchemaVersionWithCommandsWhenConnected = 5;
    const int SchemaVersionWithCommands = 4;
    const int SchemaVersionWithSerialAndWeb = 3;
    const int SchemaVersionWithHosts = 2;
    const int SchemaVersionWithoutHosts = 1;

    /// <summary>The schema a file holding <paramref name="root"/> needs: the oldest that can hold it.</summary>
    internal static int SchemaFor(ConnectionFolder root) =>
        root.Descendants().Prepend(root).Any(n => n.Settings.NeedsSchema6()) ? SchemaVersion
        : root.Descendants().Prepend(root).Any(n => n.Commands?.Any(c => c.WhenConnected || c.Password != CommandPassword.None) == true) ? SchemaVersionWithCommandsWhenConnected
        : root.Descendants().Prepend(root).Any(n => n.Commands is { Count: > 0 } || n.Settings.NeedsSchema4()) ? SchemaVersionWithCommands
        : root.Descendants().Prepend(root).Any(n => n.Settings.NeedsSchema3()) ? SchemaVersionWithSerialAndWeb
        : root.Descendants().OfType<ConnectionEntry>().Any(e => e.IsHost) ? SchemaVersionWithHosts
        : SchemaVersionWithoutHosts;

    // Each folder level costs two JSON levels (the folder and its children), so the default of 64 stops at
    // about 30 folders. This leaves room for ConnectionTree.MaxFolderDepth with plenty to spare.
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 256,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ConnectionStore In(DataLocation location) =>
        new(location.FileIn("connections.json"), location.FileIn("backups"),
            computerValuesPath: Path.Combine(location.DataDir, ComputerFiles.FolderName, $"{ComputerFiles.Tag}-{ComputerValuesFile}"));

    public ConnectionTree Load()
    {
        if (!File.Exists(path))
        {
            Logger.LogInformation("No connections file yet at {Path}", path);
            return ConnectionTree.Empty();
        }

        var text = File.ReadAllText(path);
        var tree = new ConnectionTree(Parse(text));
        Remember(text);
        _known = text;
        _fields?.NoteShared(tree);
        _fields?.PutOwn(tree);
        _fields?.Prune(tree, all: true);

        // A number that isn't a choice (hand-edited) would throw once used, so it's dropped and the folder's value applies.
        foreach (var node in tree.Root.Descendants().Prepend(tree.Root))
        {
            if (node.Settings?.ForgetUndefinedChoices() is { Count: > 0 } names)
            {
                Logger.LogWarning("\"{Name}\" had values Orrery doesn't know for {Settings}; it inherits them instead", node.Name, string.Join(", ", names));
            }
        }

        LearnNames(tree.Root);
        Logger.LogInformation("Loaded {Connections} connections in {Folders} folders from {Path}", tree.Root.Descendants().OfType<ConnectionEntry>().Count(),
            tree.Root.Descendants().OfType<ConnectionFolder>().Count(), path);
        return tree;
    }

    // So the log hides these even where only a library's error message names them.
    static void LearnNames(ConnectionFolder root)
    {
        if (Log.Mask is null)
        {
            return;
        }

        foreach (var node in root.Descendants())
        {
            Log.Learn(LogMaskKind.Name, node.Name);
            Log.Learn(LogMaskKind.User, node.Settings?.Username);
            Log.Learn(LogMaskKind.Host, node.Settings?.Domain);
            if (node is ConnectionEntry entry)
            {
                if (entry.Settings?.Protocol is not (Protocol.Serial or Protocol.Local))
                {
                    Log.Learn(LogMaskKind.Host, entry.Host);
                }

                Log.Learn(LogMaskKind.Mac, entry.MacAddress);
                Log.Learn(LogMaskKind.Web, entry.ManagementUrl);
                foreach (var forward in entry.PortForwards ?? [])
                {
                    Log.Learn(LogMaskKind.Host, forward.DestinationHost);
                }
            }
        }
    }

    static ConnectionFolder Parse(string text) => ParseFile(text).Root;

    const string NewerFileKey = "Orrery.NewerConnectionsFile";

    /// <summary>
    /// Whether <paramref name="e"/> says connections.json was saved by a newer Orrery, with what this one doesn't know.
    /// Such a file is never saved over.
    /// </summary>
    public static bool IsNewerFile(Exception e) => e is InvalidDataException && e.Data.Contains(NewerFileKey);

    static FileModel ParseFile(string text)
    {
        var file = JsonSerializer.Deserialize<FileModel>(text, JsonOptions)
            ?? throw new InvalidDataException("connections.json is empty.");
        if (file.SchemaVersion > SchemaVersion)
        {
            var newer = new InvalidDataException("connections.json was written by a newer version of Orrery.");
            newer.Data[NewerFileKey] = true;
            throw newer;
        }

        return file.Root is null ? throw new InvalidDataException("connections.json has no connections in it.") : file;
    }

    /// <summary>A short hash of one version of the file, as the history in later versions names it.</summary>
    static string VersionHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    void Remember(string text)
    {
        var hash = VersionHash(text);
        if (!_versions.TryAdd(hash, text))
        {
            return;
        }

        _versionOrder.Enqueue(hash);
        while (_versionOrder.Count > VersionsKept)
        {
            _versions.Remove(_versionOrder.Dequeue());
        }
    }

    /// <summary>
    /// The history for a file saved from <paramref name="from"/>: it, then what it was saved from, newest first. Empty for
    /// one saved from no file at all.
    /// </summary>
    static List<string> HistoryAfter(string? from)
    {
        if (from is null)
        {
            return [];
        }

        IReadOnlyList<string> earlier = [];
        try
        {
            earlier = JsonSerializer.Deserialize<HistoryOnly>(from, JsonOptions)?.History ?? [];
        }
        catch (JsonException)
        {
        }

        return [VersionHash(from), .. earlier.Take(HistoryLength - 1)];
    }

    /// <summary>
    /// The version of the file the one on disk and this computer's tree both come from, which the merge measures each
    /// side's changes against: the newest this computer has seen of those the file says it was saved from. A file
    /// without a history was saved by an Orrery from before it, so as then: the version this computer saw last. Null
    /// when this computer has seen none of them, and then nothing counts as deleted, so nothing is lost.
    /// </summary>
    string? BaseFor(FileModel theirs)
    {
        if (theirs.History is null)
        {
            return _known;
        }

        foreach (var hash in theirs.History)
        {
            if (_versions.TryGetValue(hash, out var text))
            {
                return text;
            }
        }

        return null;
    }

    static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 256 };

    /// <summary>
    /// Whether two versions of the file hold the same tree, apart from their history, the root's id (each computer that
    /// started without the file made its own) and how the text is laid out (line ends differ between systems). Saving
    /// on such a difference made two computers rewrite the file at each other for as long as both were open.
    /// </summary>
    internal static bool SameContent(string a, string b)
    {
        if (a == b)
        {
            return true;
        }

        try
        {
            return JsonNode.DeepEquals(Comparable(a), Comparable(b));
        }
        catch (JsonException)
        {
            return false;
        }

        static JsonNode? Comparable(string text)
        {
            var node = JsonNode.Parse(text, documentOptions: ReadOptions);
            if (node is JsonObject file)
            {
                file.Remove("history");
                (file["root"] as JsonObject)?.Remove("id");
            }

            return node;
        }
    }

    static string? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    static string Serialize(ConnectionTree tree, IReadOnlyList<string>? history) =>
        JsonSerializer.Serialize(new FileModel(SchemaFor(tree.Root), history, tree.Root), JsonOptions);

    /// <summary>
    /// Saves the tree, first merging in what another computer saved to the file since this one last read or wrote it.
    /// Returns what that merge changed in the tree, or null when the file hadn't changed.
    /// </summary>
    /// <exception cref="InvalidDataException">A newer Orrery wrote the file since; it's left as it is.</exception>
    public ConnectionMergeReport? Save(ConnectionTree tree) => WithSharedValues(tree, () => SaveShared(tree));

    // Runs a merge or a save with connections.json's values of this computer's own fields in the tree, then puts this
    // computer's back.
    T WithSharedValues<T>(ConnectionTree tree, Func<T> work)
    {
        if (_fields is null)
        {
            return work();
        }

        _fields.TakeOwn(tree);
        try
        {
            return work();
        }
        finally
        {
            _fields.NoteShared(tree);
            _fields.PutOwn(tree);
            _fields.Prune(tree);
            _fields.Save();
        }
    }

    ConnectionMergeReport? SaveShared(ConnectionTree tree)
    {
        var disk = ReadOrNull(path);
        ConnectionMergeReport? report = null;
        if (disk is not null && disk != _known)
        {
            report = MergeFrom(tree, disk, saving: true);
        }

        // The tree now holds _known: the file as it is, merged, or as last seen here when what's there can't be read.
        var text = Serialize(tree, HistoryAfter(_known));
        if (disk is not null && SameContent(text, disk))
        {
            _known = disk;
            return report;
        }

        if (disk is not null)
        {
            Backup();
        }

        AtomicFile.WriteAllText(path, text);
        Remember(text);
        _known = text;
        Logger.LogDebug("Saved {Connections} connections", tree.Root.Descendants().OfType<ConnectionEntry>().Count());
        return report;
    }

    /// <summary>
    /// Merges in what another computer saved to the file since this one last read or wrote it. Null when there's nothing
    /// new, or the file can't be read yet (a sync app part way through writing it): the next try, or the next save, takes it.
    /// <see cref="NeedsSave"/> says afterwards whether the tree holds changes the file doesn't.
    /// </summary>
    public ConnectionMergeReport? ReloadIfChanged(ConnectionTree tree) => WithSharedValues(tree, () => ReloadShared(tree));

    ConnectionMergeReport? ReloadShared(ConnectionTree tree)
    {
        string? disk;
        try
        {
            disk = ReadOrNull(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (disk is null || disk == _known)
        {
            return null;
        }

        try
        {
            var report = MergeFrom(tree, disk, saving: false);
            NeedsSave = !SameContent(Serialize(tree, history: null), disk);
            return report;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            Logger.LogWarning("connections.json changed but can't be read yet: {Problem}", e.Message);
            return null;
        }
    }

    /// <summary>The file isn't as this computer last read or wrote it: another computer saved it since.</summary>
    public bool ChangedElsewhere()
    {
        try
        {
            return ReadOrNull(path) is { } disk && disk != _known;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>After <see cref="ReloadIfChanged"/>: the tree has changes of this computer's the file doesn't have yet.</summary>
    public bool NeedsSave { get; private set; }

    ConnectionMergeReport? MergeFrom(ConnectionTree tree, string disk, bool saving)
    {
        FileModel theirs;
        try
        {
            theirs = ParseFile(disk);
        }
        catch (JsonException e) when (saving)
        {
            // Damaged, or half written by a sync app: saved over, the old file kept in backups/ first.
            Logger.LogWarning("connections.json changed on disk but isn't readable ({Problem}); saving this computer's", e.Message);
            return null;
        }

        Remember(disk);
        ConnectionFolder? baseline = null;
        if (BaseFor(theirs) is { } baseText)
        {
            try
            {
                baseline = Parse(baseText);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            {
            }
        }
        else if (_known is not null)
        {
            Logger.LogInformation("connections.json was saved from a version this computer hasn't seen, as after a sync app's conflict; nothing in it counts as deleted");
        }

        var report = ConnectionMerge.Merge(tree, baseline, theirs.Root);
        _known = disk;
        LearnNames(tree.Root);
        Logger.LogInformation("Took in changes to connections.json from another computer: {Added} added, {Removed} removed, {Updated} changed, {Conflicts} changed on both",
            report.Added, report.Removed, report.Updated, report.Conflicts.Count);
        return report;
    }

    void Backup()
    {
        PrivateStorage.CreateDirectory(backupDirectory);

        // Names sort by time, so the oldest come first.
        var backups = Directory.GetFiles(backupDirectory, "connections-*.json").Order(StringComparer.Ordinal).ToList();
        if (backups.Count > 0 && DateTime.UtcNow - File.GetLastWriteTimeUtc(backups[^1]) < _backupInterval)
        {
            return;
        }

        var name = $"connections-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json";
        var backup = Path.Combine(backupDirectory, name);
        File.Copy(path, backup, overwrite: true);
        PrivateStorage.RestrictFile(backup);
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow);
        backups.Add(backup);
        Logger.LogInformation("Backed up connections to {Backup}", name);

        foreach (var old in backups.Take(Math.Max(0, backups.Count - backupsToKeep)))
        {
            File.Delete(old);
            Logger.LogDebug("Removed old backup {Backup}", Path.GetFileName(old));
        }
    }

    /// <param name="History">
    /// The versions this one was saved from, newest first, each by <see cref="VersionHash"/>; empty for one saved from no
    /// file. Null in a file from an Orrery from before it, which ignores it.
    /// </param>
    sealed record FileModel(int SchemaVersion, IReadOnlyList<string>? History, ConnectionFolder Root);

    sealed record HistoryOnly(IReadOnlyList<string>? History);
}
