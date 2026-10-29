using System.Text.Json;
using System.Text.Json.Serialization;
using Orrery.Core.Storage;

namespace Orrery.Core.Connections;

/// <summary>
/// Reads and writes <c>connections.json</c>. Holds no secrets: connections refer to saved credentials by id.
/// Before a save, the previous file is copied into <c>backups/</c> if the newest backup is older than
/// <paramref name="backupInterval"/>, so frequent small saves don't push useful backups out.
/// </summary>
public sealed class ConnectionStore(string path, string backupDirectory, int backupsToKeep = 10, TimeSpan? backupInterval = null)
{
    readonly TimeSpan _backupInterval = backupInterval ?? TimeSpan.FromMinutes(10);

    const int SchemaVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
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
            return ConnectionTree.Empty();
        }

        var file = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("connections.json is empty.");
        if (file.SchemaVersion > SchemaVersion)
        {
            throw new InvalidDataException("connections.json was written by a newer version of Orrery.");
        }

        return new ConnectionTree(file.Root);
    }

    public void Save(ConnectionTree tree)
    {
        if (File.Exists(path))
        {
            Backup();
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new FileModel(SchemaVersion, tree.Root), JsonOptions));
    }

    void Backup()
    {
        Directory.CreateDirectory(backupDirectory);

        // Names sort by time, so the oldest come first.
        var backups = Directory.GetFiles(backupDirectory, "connections-*.json").Order(StringComparer.Ordinal).ToList();
        if (backups.Count > 0 && DateTime.UtcNow - File.GetLastWriteTimeUtc(backups[^1]) < _backupInterval)
        {
            return;
        }

        var name = $"connections-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json";
        var backup = Path.Combine(backupDirectory, name);
        File.Copy(path, backup, overwrite: true);
        File.SetLastWriteTimeUtc(backup, DateTime.UtcNow);
        backups.Add(backup);

        foreach (var old in backups.Take(Math.Max(0, backups.Count - backupsToKeep)))
        {
            File.Delete(old);
        }
    }

    sealed record FileModel(int SchemaVersion, ConnectionFolder Root);
}
