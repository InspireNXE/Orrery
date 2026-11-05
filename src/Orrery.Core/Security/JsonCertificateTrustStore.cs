using System.Text.Json;
using Orrery.Core.Storage;

namespace Orrery.Core.Security;

/// <summary>
/// Keeps pinned certificates in a small JSON file, keyed by "host:port". Saves are atomic.
/// </summary>
public sealed class JsonCertificateTrustStore : ICertificateTrustStore
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly string _path;
    readonly object _lock = new();
    Dictionary<string, PinnedCertificate> _pins;
    (long Length, DateTime Written) _stamp;

    public JsonCertificateTrustStore(string path)
    {
        _path = path;
        _pins = Load(path);
        _stamp = Stamp(path);
    }

    public PinnedCertificate? Get(string host, int port)
    {
        lock (_lock)
        {
            ReloadIfChanged();
            return _pins.GetValueOrDefault(Key(host, port));
        }
    }

    public void Pin(string host, int port, PinnedCertificate certificate)
    {
        lock (_lock)
        {
            // Another computer sharing the data folder may have trusted others since; theirs stay.
            ReloadIfChanged();
            _pins[Key(host, port)] = certificate;
            Save();
        }
    }

    static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    void Save()
    {
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new FileModel(1, _pins), JsonOptions));
        _stamp = Stamp(_path);
    }

    // Caller holds _lock. The file changed on disk (a sync app bringing another computer's): read again. Nothing is
    // ever unpinned, so what's read is everything; a file that can't be read yet is tried next time.
    void ReloadIfChanged()
    {
        var stamp = Stamp(_path);
        if (stamp == _stamp)
        {
            return;
        }

        try
        {
            var pins = Load(_path);
            foreach (var (key, pin) in _pins.Where(p => !pins.ContainsKey(p.Key)))
            {
                pins[key] = pin;
            }

            _pins = pins;
            _stamp = stamp;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
        }
    }

    static (long, DateTime) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (-1, default);
        }
    }

    static Dictionary<string, PinnedCertificate> Load(string path)
    {
        if (!File.Exists(path))
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path));
        return new(model?.Hosts ?? [], StringComparer.OrdinalIgnoreCase);
    }

    sealed record FileModel(int SchemaVersion, Dictionary<string, PinnedCertificate> Hosts);
}
