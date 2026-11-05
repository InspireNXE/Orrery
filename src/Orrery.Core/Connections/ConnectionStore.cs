using System.Text.Json;
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

    // The file as last read or written here: the base the next merge measures both sides against. Null: there was none.
    string? _known;

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
        _known = text;
        _fields?.NoteShared(tree);
        _fields?.PutOwn(tree);

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

    static ConnectionFolder Parse(string text)
    {
        var file = JsonSerializer.Deserialize<FileModel>(text, JsonOptions)
            ?? throw new InvalidDataException("connections.json is empty.");
        if (file.SchemaVersion > SchemaVersion)
        {
            throw new InvalidDataException("connections.json was written by a newer version of Orrery.");
        }

        return file.Root ?? throw new InvalidDataException("connections.json has no connections in it.");
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

    string Serialize(ConnectionTree tree) => JsonSerializer.Serialize(new FileModel(SchemaFor(tree.Root), tree.Root), JsonOptions);

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

        var text = Serialize(tree);
        if (text == disk)
        {
            _known = disk;
            return report;
        }

        if (disk is not null)
        {
            Backup();
        }

        AtomicFile.WriteAllText(path, text);
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
            NeedsSave = Serialize(tree) != disk;
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
        ConnectionFolder theirs;
        try
        {
            theirs = Parse(disk);
        }
        catch (JsonException e) when (saving)
        {
            // Damaged, or half written by a sync app: saved over, the old file kept in backups/ first.
            Logger.LogWarning("connections.json changed on disk but isn't readable ({Problem}); saving this computer's", e.Message);
            return null;
        }

        ConnectionFolder? baseline = null;
        if (_known is not null)
        {
            try
            {
                baseline = Parse(_known);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            {
            }
        }

        var report = ConnectionMerge.Merge(tree, baseline, theirs);
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

    sealed record FileModel(int SchemaVersion, ConnectionFolder Root);
}
