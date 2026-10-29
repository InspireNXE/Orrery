using System.Text.Json;

namespace Orrery.Core.Security;

/// <summary>
/// Keeps pinned certificates in a small JSON file, keyed by "host:port". Saves are atomic:
/// the file is written to a temporary name and then moved over the original.
/// </summary>
public sealed class JsonCertificateTrustStore : ICertificateTrustStore
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    readonly string _path;
    readonly object _lock = new();
    readonly Dictionary<string, PinnedCertificate> _pins;

    public JsonCertificateTrustStore(string path)
    {
        _path = path;
        _pins = Load(path);
    }

    public PinnedCertificate? Get(string host, int port)
    {
        lock (_lock)
        {
            return _pins.GetValueOrDefault(Key(host, port));
        }
    }

    public void Pin(string host, int port, PinnedCertificate certificate)
    {
        lock (_lock)
        {
            _pins[Key(host, port)] = certificate;
            Save();
        }
    }

    static string Key(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileModel(1, _pins), JsonOptions));
        File.Move(temp, _path, overwrite: true);
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
