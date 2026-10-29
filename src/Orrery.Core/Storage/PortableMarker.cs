using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orrery.Core.Storage;

/// <summary>Where a portable copy keeps its data.</summary>
public enum PortableDataChoice
{
    /// <summary>In a <c>data</c> folder next to the app, so it travels on a USB stick.</summary>
    WithApp,

    /// <summary>In the user profile on this machine, separate from any installed copy.</summary>
    UserProfile,

    /// <summary>A folder the user picked, stored relative to the app folder when possible.</summary>
    Custom,
}

/// <summary>
/// <c>orrery.portable.json</c>, next to the app. Its presence switches Orrery to portable mode;
/// a missing <see cref="DataLocation"/> means the user hasn't chosen yet.
/// </summary>
public sealed record PortableMarker(
    [property: JsonPropertyName("version")] int Version = 1,
    [property: JsonPropertyName("dataLocation")] string? DataLocation = null,
    [property: JsonPropertyName("customPath")] string? CustomPath = null)
{
    public const string FileName = "orrery.portable.json";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public PortableDataChoice? Choice => DataLocation switch
    {
        "app" => PortableDataChoice.WithApp,
        "user" => PortableDataChoice.UserProfile,
        "custom" when !string.IsNullOrWhiteSpace(CustomPath) => PortableDataChoice.Custom,
        _ => null,
    };

    public static PortableMarker For(PortableDataChoice choice, string? customPath = null) => choice switch
    {
        PortableDataChoice.WithApp => new(DataLocation: "app"),
        PortableDataChoice.UserProfile => new(DataLocation: "user"),
        _ => new(DataLocation: "custom", CustomPath: customPath),
    };

    public static PortableMarker? TryLoad(string appDirectory)
    {
        var path = Path.Combine(appDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);

        // An empty marker is how the portable download ships: portable, choice still to make.
        return string.IsNullOrWhiteSpace(text) ? new PortableMarker() : JsonSerializer.Deserialize<PortableMarker>(text, JsonOptions);
    }

    /// <returns>False if the marker can't be written (read-only media); the choice then lasts one session.</returns>
    public bool TrySave(string appDirectory)
    {
        try
        {
            AtomicFile.WriteAllText(Path.Combine(appDirectory, FileName), JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
