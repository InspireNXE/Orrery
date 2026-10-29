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

    void Save() => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new FileModel(1, _pins), JsonOptions));

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
