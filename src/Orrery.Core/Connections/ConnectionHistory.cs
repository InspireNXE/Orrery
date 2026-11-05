using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Storage;

namespace Orrery.Core.Connections;

/// <summary>When a connection last connected, and when it last failed to. Either is null if it never has.</summary>
public sealed record ConnectionUse(DateTimeOffset? Connected, DateTimeOffset? Failed)
{
    public static readonly ConnectionUse None = new(null, null);

    /// <summary>The failure, only while it's more recent than the last success.</summary>
    public DateTimeOffset? FailedSince => Failed is { } failed && (Connected is not { } connected || failed > connected) ? failed : null;
}

/// <summary>
/// When each saved connection last connected or failed to, by id, kept in <c>history.json</c> in the data folder.
/// It is local history rather than part of a connection, so it isn't in exports or the connections file, and it
/// follows a connection through renames and moves. A missing id means it has never connected.
/// </summary>
public sealed class ConnectionHistory(string path)
{
    public const string FileName = "history.json";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    Dictionary<Guid, ConnectionUse>? _uses;
    bool _enabled = true;

    public static ConnectionHistory In(DataLocation location) => new(ComputerFiles.PathFor(location, FileName));

    public event EventHandler? Changed;

    /// <summary>
    /// Whether connecting and failing are recorded. Turning it off deletes the file and every record, and nothing is
    /// recorded until it's turned on again, which starts with none.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            _uses = null;
            if (!value)
            {
                try
                {
                    AtomicFile.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.For("Storage").LogWarning("Couldn't delete connection history: {Message}", e.Message);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A missing or unreadable file is an empty history: losing it isn't worth refusing to start.</summary>
    Dictionary<Guid, ConnectionUse> Uses => _uses ??= _enabled ? Load() : [];

    public ConnectionUse Get(Guid id) => Uses.GetValueOrDefault(id) ?? ConnectionUse.None;

    /// <summary>
    /// A connection's own record; for a folder, the latest success and the latest failure of any connection in it
    /// (not necessarily the same one).
    /// </summary>
    public ConnectionUse Summary(ConnectionNode node)
    {
        if (node is not ConnectionFolder folder)
        {
            return Get(node.Id);
        }

        DateTimeOffset? connected = null, failed = null;
        foreach (var entry in folder.Descendants().OfType<ConnectionEntry>())
        {
            var use = Get(entry.Id);
            connected = Latest(connected, use.Connected);
            failed = Latest(failed, use.Failed);
        }

        return new ConnectionUse(connected, failed);
    }

    public void RecordConnected(Guid id, DateTimeOffset when) => Store(id, Get(id) with { Connected = when });

    public void RecordFailed(Guid id, DateTimeOffset when) => Store(id, Get(id) with { Failed = when });

    /// <summary>Drops the records of connections that were deleted.</summary>
    public void Forget(IEnumerable<Guid> ids)
    {
        bool any = false;
        foreach (var id in ids)
        {
            any |= Uses.Remove(id);
        }

        if (any)
        {
            Save();
        }
    }

    static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    void Store(Guid id, ConnectionUse use)
    {
        if (!_enabled)
        {
            return;
        }

        Uses[id] = use;
        Save();
    }

    void Save()
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new FileModel(1, Uses), JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("Storage").LogWarning("Couldn't save connection history: {Message}", e.Message);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    Dictionary<Guid, ConnectionUse> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions)?.Items?.Where(i => i.Value is not null).ToDictionary() ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    sealed record FileModel(int SchemaVersion, Dictionary<Guid, ConnectionUse>? Items);
}
