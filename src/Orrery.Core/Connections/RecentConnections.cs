using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Orrery.Core.Diagnostics;
using Orrery.Core.Storage;

namespace Orrery.Core.Connections;

/// <summary>
/// A connection that was opened: a saved one by id, or a host typed into quick connect. Holds no secrets.
/// </summary>
public sealed record RecentConnection(Guid? EntryId, string Title, Protocol Protocol, string Host, int Port, string? Username, DateTimeOffset OpenedAt)
{
    /// <summary>Two recents are the same connection if they're the same saved entry, or the same direct address.</summary>
    public bool SameAs(RecentConnection other) => EntryId is { } id
        ? id == other.EntryId
        : other.EntryId is null && Protocol == other.Protocol && Port == other.Port
          && string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase)
          && string.Equals(Username, other.Username, StringComparison.Ordinal);
}

/// <summary>
/// The most recently opened connections, newest first, kept in <c>recent.json</c> in the data folder.
/// Opening a connection again moves it to the top rather than adding a second row.
/// </summary>
public sealed class RecentConnections(string path, int limit = 20)
{
    public const string FileName = "recent.json";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    List<RecentConnection>? _items;
    bool _enabled = true;

    public static RecentConnections In(DataLocation location) => new(location.FileIn(FileName));

    /// <summary>A missing or unreadable file is an empty history: losing it isn't worth refusing to start.</summary>
    public IReadOnlyList<RecentConnection> Items => _enabled ? _items ??= Load() : [];

    public event EventHandler? Changed;

    /// <summary>
    /// Whether connections are remembered. Turning it off deletes the file and the list, and nothing is added
    /// until it's turned on again, which starts from an empty list.
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
            _items = null;
            if (!value)
            {
                try
                {
                    AtomicFile.Delete(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.For("Storage").LogWarning("Couldn't delete recent connections: {Message}", e.Message);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Add(RecentConnection recent)
    {
        if (!_enabled)
        {
            return;
        }

        var items = Items.Where(r => !r.SameAs(recent)).Prepend(recent).Take(limit).ToList();
        Store(items);
    }

    public void Remove(RecentConnection recent) => Store(Items.Where(r => !r.SameAs(recent)).ToList());

    public void Clear() => Store([]);

    void Store(List<RecentConnection> items)
    {
        if (!_enabled)
        {
            return;
        }

        _items = items;
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new FileModel(1, items), JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("Storage").LogWarning("Couldn't save recent connections: {Message}", e.Message);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    List<RecentConnection> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions)?.Items.Take(limit).ToList() ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    sealed record FileModel(int SchemaVersion, List<RecentConnection> Items);
}
