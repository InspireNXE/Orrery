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
/// </summary>
public sealed class ConnectionStore(string path, string backupDirectory, int backupsToKeep = 10, TimeSpan? backupInterval = null)
{
    static readonly ILogger Logger = Log.For("Storage");

    readonly TimeSpan _backupInterval = backupInterval ?? TimeSpan.FromMinutes(10);

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
        new(location.FileIn("connections.json"), location.FileIn("backups"));

    public ConnectionTree Load()
    {
        if (!File.Exists(path))
        {
            Logger.LogInformation("No connections file yet at {Path}", path);
            return ConnectionTree.Empty();
        }

        var file = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("connections.json is empty.");
        if (file.SchemaVersion > SchemaVersion)
        {
            throw new InvalidDataException("connections.json was written by a newer version of Orrery.");
        }

        var tree = new ConnectionTree(file.Root);

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

    public void Save(ConnectionTree tree)
    {
        if (File.Exists(path))
        {
            Backup();
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new FileModel(SchemaFor(tree.Root), tree.Root), JsonOptions));
        Logger.LogDebug("Saved {Connections} connections", tree.Root.Descendants().OfType<ConnectionEntry>().Count());
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
