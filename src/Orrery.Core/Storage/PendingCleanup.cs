using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orrery.Core.Storage;

/// <summary>
/// <c>pending-cleanup.json</c> in the new data folder, written by Move data. After the restart Orrery only
/// offers to delete the old copy when this names the same folder as <c>--moved-from</c>, so a command line
/// alone can't point the delete at an arbitrary folder.
/// </summary>
public sealed record PendingCleanup([property: JsonPropertyName("oldDataDir")] string OldDataDir)
{
    public const string FileName = "pending-cleanup.json";

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static PendingCleanup? TryLoad(string dataDir)
    {
        var path = Path.Combine(dataDir, FileName);
        try
        {
            return File.Exists(path)
                && JsonSerializer.Deserialize<PendingCleanup>(File.ReadAllText(path), JsonOptions) is { OldDataDir.Length: > 0 } pending
                ? pending
                : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Record(string dataDir, string oldDataDir) =>
        AtomicFile.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(new PendingCleanup(Path.GetFullPath(oldDataDir)), JsonOptions));

    public static void Clear(string dataDir)
    {
        try
        {
            File.Delete(Path.Combine(dataDir, FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
