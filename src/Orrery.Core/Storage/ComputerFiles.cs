using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orrery.Core.Storage;

/// <summary>
/// Files in the data folder that belong to one computer, for a folder a sync app shares between several: what's open and
/// where the window was, recent connections and when each connected, and the values of shared files that only make sense
/// on one computer (a key file's path, which displays full screen uses). They're in <see cref="FolderName"/>, named with
/// the computer's tag (<see cref="DataFolderPresence.ComputerTag"/>), so no two computers write the same file.
/// </summary>
public static class ComputerFiles
{
    public const string FolderName = "computers";

    /// <summary>This computer's tag, as its files in the data folder are named.</summary>
    public static string Tag { get; } = DataFolderPresence.ComputerTag(Environment.MachineName);

    /// <summary>
    /// This computer's copy of <paramref name="name"/> in <paramref name="dataDir"/>. The first time, a file of that name
    /// at the top of the folder (from before files were per computer) is copied to it, so nothing is lost.
    /// </summary>
    public static string PathFor(string dataDir, string name)
    {
        var own = Path.Combine(dataDir, FolderName, $"{Tag}-{name}");
        var shared = Path.Combine(dataDir, name);
        try
        {
            if (!File.Exists(own) && File.Exists(shared))
            {
                PrivateStorage.CreateDirectory(Path.GetDirectoryName(own)!);
                File.Copy(shared, own);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read from the old place's copy next time; this one starts empty.
        }

        return own;
    }

    public static string PathFor(DataLocation location, string name) => PathFor(location.DataDir, name);

    /// <summary>
    /// At start: update downloads a portable copy left in the shared cache folder, from before each computer had its
    /// own (<c>cache/&lt;tag&gt;</c>), so a sync app stops copying them about. Never throws.
    /// </summary>
    public static void TidyOldCache(DataLocation location)
    {
        var old = Path.Combine(location.DataDir, "cache", "updates");
        try
        {
            if (location.Mode == StorageMode.Portable && Directory.Exists(old) && new DirectoryInfo(old).LinkTarget is null
                && !string.Equals(Path.GetFullPath(location.CacheDir), Path.GetFullPath(Path.Combine(location.DataDir, "cache")), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(old, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// This computer's own values for fields of a shared file, kept in a per-computer file beside it
/// (<see cref="ComputerFiles"/>): <c>{ "&lt;key&gt;": { "&lt;field&gt;": value } }</c>, a key per connection, or one for
/// the whole file. A field set here wins over the shared file's on this computer only; a field not set here takes the
/// shared file's, which is what the first computer to save it had.
/// </summary>
public sealed class ComputerValues(string path)
{
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    JsonObject _values = Read(path);
    string _written = ReadText(path);

    public string FilePath => path;

    static string ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    static JsonObject Read(string path)
    {
        try
        {
            return JsonNode.Parse(ReadText(path)) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The fields set for <paramref name="key"/>, by name; empty for none. A null value is set, to nothing.</summary>
    public IReadOnlyDictionary<string, JsonNode?> For(string key) =>
        _values[key] is JsonObject fields ? fields.ToDictionary(f => f.Key, f => f.Value) : new Dictionary<string, JsonNode?>();

    public bool Has(string key, string field) => _values[key] is JsonObject fields && fields.ContainsKey(field);

    public void Set(string key, string field, JsonNode? value)
    {
        if (_values[key] is not JsonObject fields)
        {
            _values[key] = fields = [];
        }

        fields[field] = value?.DeepClone();
    }

    public void Clear(string key, string field)
    {
        if (_values[key] is JsonObject fields)
        {
            fields.Remove(field);
            if (fields.Count == 0)
            {
                _values.Remove(key);
            }
        }
    }

    /// <summary>Drops every key <paramref name="keep"/> says no to: connections since deleted.</summary>
    public void Keep(Func<string, bool> keep)
    {
        foreach (var key in _values.Select(v => v.Key).Where(k => !keep(k)).ToList())
        {
            _values.Remove(key);
        }
    }

    /// <summary>Writes the file if anything changed since it was read or written; deleted when nothing's set.</summary>
    public void Save()
    {
        var text = _values.Count == 0 ? "" : _values.ToJsonString(WriteOptions);
        if (text == _written)
        {
            return;
        }

        if (text.Length == 0)
        {
            File.Delete(path);
        }
        else
        {
            PrivateStorage.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, text);
        }

        _written = text;
    }
}
