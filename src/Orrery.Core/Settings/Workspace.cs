using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Orrery.Core.Connections;
using Orrery.Core.Diagnostics;
using Orrery.Core.Storage;

namespace Orrery.Core.Settings;

/// <summary>What to do at start-up with the sessions that were open when Orrery last closed.</summary>
public enum RestoreSessionsMode
{
    /// <summary>Offer, in a bar at the top of the window.</summary>
    Ask,
    Always,
    Never,
}

/// <summary>
/// One tab to bring back: a saved connection by id, a host typed into quick connect, or a local terminal by the id
/// of its shell (<paramref name="LocalShell"/>, such as "pwsh" or "wsl:Ubuntu"; Host and Port are unused then).
/// Holds no login of any kind, so reopening a typed host asks for one again. <paramref name="Popout"/> is where its
/// window was when it was popped out of the tabs; null for a tab.
/// </summary>
public sealed record WorkspaceTab(Guid? EntryId, string? Host, int Port, Protocol Protocol, bool Files = false, string? LocalShell = null,
    WindowPlacement? Popout = null);

/// <summary>
/// Where the window was: its normal (not maximized) position in screen pixels and size in device-independent
/// units, and whether it was maximized.
/// </summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>
/// The tabs that were open, in order, and which one was showing, plus where the window was. A session popped out into
/// a window of its own is among the tabs, in the place it had in the strip, with where its window was. Kept in
/// <c>workspace.json</c> in the data folder. Which panes were showing lives in <see cref="AppSettings"/>.
/// </summary>
public sealed record Workspace
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>More tabs than this in the file are ignored; nobody has this many open.</summary>
    public const int MaxTabs = 50;

    const int MaxHostLength = 1024;
    const int MaxShellIdLength = 256;
    const double MaxWindowSize = 100_000;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public WindowPlacement? Window { get; init; }

    public List<WorkspaceTab> Tabs { get; init; } = [];

    /// <summary>Index into <see cref="Tabs"/> of the tab that was showing; -1 for none.</summary>
    public int Active { get; init; } = -1;

    /// <summary>
    /// Orrery closed to finish an update: the next start reopens <see cref="Tabs"/> without asking, once. Null
    /// otherwise.
    /// </summary>
    public bool? ReopenAtStart { get; init; }

    /// <summary>
    /// The tabs that can still be opened: a saved connection must still exist (and have a host), and a typed one
    /// needs a host. <see cref="Active"/> follows its tab, or the nearest one left if that was deleted.
    /// </summary>
    public Workspace Surviving(ConnectionTree tree)
    {
        return Keep(tab => tab.LocalShell is not null
            || (tab.EntryId is { } id
                ? tree.Find(id) is ConnectionEntry { Host: { } host } && !string.IsNullOrWhiteSpace(host)
                : !string.IsNullOrWhiteSpace(tab.Host)));
    }

    /// <summary>The window alone, for when the open tabs aren't to be remembered.</summary>
    public Workspace WithoutTabs() => this with { Tabs = [], Active = -1 };

    /// <summary>Drops whatever in a loaded file can't be used, so nothing downstream has to check.</summary>
    internal Workspace Sanitized()
    {
        var kept = Keep(t => Enum.IsDefined(t.Protocol)
            && (t.LocalShell is { Length: > 0 and <= MaxShellIdLength } || t.EntryId is not null
                || (t.Host is { Length: > 0 and <= MaxHostLength } && t.Port is >= 1 and <= 65535)));
        return kept with
        {
            Window = SizeOk(Window) ? Window : null,
            Tabs = [.. kept.Tabs.Take(MaxTabs).Select(t => t.LocalShell is { Length: > 0 }
                ? t with { EntryId = null, Host = null, Port = 0, Files = false }
                : t.EntryId is null ? t with { LocalShell = null } : t with { Host = null, Port = 0, LocalShell = null })
                .Select(t => SizeOk(t.Popout) ? t : t with { Popout = null })],
            Active = kept.Active < MaxTabs ? kept.Active : -1,
        };
    }

    static bool SizeOk(WindowPlacement? window) =>
        window is { Width: > 0 and < MaxWindowSize, Height: > 0 and < MaxWindowSize } w && double.IsFinite(w.Width) && double.IsFinite(w.Height);

    /// <summary>
    /// The tabs passing <paramref name="keep"/>, with <see cref="Active"/> moved to follow its tab, or to the
    /// last one kept before it (the first, if there is none) when that one is dropped.
    /// </summary>
    Workspace Keep(Func<WorkspaceTab, bool> keep)
    {
        var kept = new List<WorkspaceTab>();
        int active = -1;
        for (int i = 0; i < (Tabs?.Count ?? 0); i++)
        {
            if (Tabs![i] is { } tab && keep(tab))
            {
                kept.Add(tab);
                if (i <= Active)
                {
                    active = kept.Count - 1;
                }
            }
        }

        return this with { Tabs = kept, Active = kept.Count == 0 || Active < 0 ? -1 : Math.Max(active, 0) };
    }
}

public sealed class WorkspaceStore(string path)
{
    public const string FileName = "workspace.json";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static WorkspaceStore In(DataLocation location) => new(ComputerFiles.PathFor(location, FileName));

    /// <summary>A missing or unreadable file is an empty workspace: nothing to reopen, the window where it starts.</summary>
    public Workspace Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Workspace>(File.ReadAllText(path), JsonOptions)?.Sanitized() ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.For("Storage").LogWarning("Ignoring workspace.json, which couldn't be read: {Message}", e.Message);
            return new();
        }
    }

    public void Save(Workspace workspace)
    {
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(workspace with { SchemaVersion = Workspace.CurrentSchemaVersion }, JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.For("Storage").LogWarning("Couldn't save workspace.json: {Message}", e.Message);
        }
    }

    /// <summary>Rewrites the file without its list of open tabs, keeping where the window was. No file, nothing to do.</summary>
    public void ForgetTabs()
    {
        if (File.Exists(path))
        {
            Save(Load().WithoutTabs());
        }
    }
}
