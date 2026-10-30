using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orrery.Core.Storage;

/// <summary>
/// <c>data-location.json</c> in the default config folder. Installed mode has no marker file, so after
/// Move data this is how Orrery finds the data next time. Cache and logs stay in their usual places.
/// </summary>
public sealed record InstalledLocationPointer([property: JsonPropertyName("dataDir")] string DataDir)
{
    public const string FileName = "data-location.json";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static InstalledLocationPointer? TryLoad(string configDir)
    {
        var path = Path.Combine(configDir, FileName);
        try
        {
            return File.Exists(path)
                && JsonSerializer.Deserialize<InstalledLocationPointer>(File.ReadAllText(path), JsonOptions) is { DataDir.Length: > 0 } pointer
                ? pointer
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Points at <paramref name="dataDir"/>, or removes the pointer when that is the default folder.</summary>
    public static void Set(string configDir, string dataDir)
    {
        var path = Path.Combine(configDir, FileName);
        if (DataMover.SamePath(dataDir, configDir))
        {
            File.Delete(path);
            return;
        }

        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new InstalledLocationPointer(Path.GetFullPath(dataDir)), JsonOptions));
    }
}
