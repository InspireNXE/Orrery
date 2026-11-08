using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using Orrery.Core.Storage;

namespace Orrery.Core.Connections;

/// <summary>
/// The fields of connections and folders that belong to the computer they're used on, for a data folder a sync app
/// shares between several: paths on this computer (a key file, shared folders, where a local shell starts), the shell or
/// serial port a LOCAL or serial connection opens, which displays full screen uses, and which folders are open in the tree.
/// <c>connections.json</c> keeps what the first computer to save a connection had; another computer's own values are
/// in its <see cref="ComputerValues"/> file. <see cref="ConnectionStore"/> puts the shared values in the tree while it
/// merges and saves, and this computer's back afterwards, so these fields never pass between computers once set.
/// </summary>
sealed class PerComputerFields(ComputerValues values)
{
    sealed record Field(string Name, Func<ConnectionNode, bool> Applies, Func<ConnectionNode, JsonNode?> Get, Action<ConnectionNode, JsonNode?> Set);

    static JsonNode? ToJson<T>(T value) => value is null ? null : JsonSerializer.SerializeToNode(value, ConnectionStore.JsonOptions);

    static T? FromJson<T>(JsonNode? node) => node is null ? default : node.Deserialize<T>(ConnectionStore.JsonOptions);

    static Field Setting<T>(string name, Func<InheritableSettings, T> get, Action<InheritableSettings, T?> set) =>
        new("settings." + name, _ => true, n => ToJson(get(n.Settings)), (n, v) => set(n.Settings, FromJson<T>(v)));

    static bool OpensOnThisComputer(ConnectionNode node) =>
        node is ConnectionEntry entry && Protocols.Get(SettingsResolver.Resolve(entry).Protocol.Value).Reach == ProtocolReach.ThisComputer;

    static readonly Field[] Fields =
    [
        Setting("keyFile", s => s.KeyFile, (s, v) => s.KeyFile = v),
        Setting("sharedFolders", s => s.SharedFolders, (s, v) => s.SharedFolders = v),
        Setting("chosenDisplays", s => s.ChosenDisplays, (s, v) => s.ChosenDisplays = v),
        Setting("useAllMonitors", s => s.UseAllMonitors, (s, v) => s.UseAllMonitors = v),
        new("startFolder", n => n is ConnectionEntry, n => ToJson(((ConnectionEntry)n).StartFolder), (n, v) => ((ConnectionEntry)n).StartFolder = FromJson<string>(v)),
        new("host", OpensOnThisComputer, n => ToJson(((ConnectionEntry)n).Host), (n, v) => ((ConnectionEntry)n).Host = FromJson<string>(v) ?? ""),
        new("expanded", n => n is ConnectionFolder, n => ToJson(((ConnectionFolder)n).Expanded), (n, v) => ((ConnectionFolder)n).Expanded = FromJson<bool?>(v) ?? true),
        new("guestsCollapsed", n => n is ConnectionEntry, n => ToJson(((ConnectionEntry)n).GuestsCollapsed), (n, v) => ((ConnectionEntry)n).GuestsCollapsed = FromJson<bool?>(v) ?? false),
    ];

    /// <summary>Names of the fields, as <see cref="ConnectionMerge"/> and the file have them.</summary>
    internal static IEnumerable<string> Names => Fields.Select(f => f.Name);

    // Each field's value in connections.json, by node.
    readonly Dictionary<(Guid, string), JsonNode?> _shared = [];

    // The root's values are under this key, not its id: a computer that started without the file made a root of its own,
    // which it keeps after taking the file in, while the file keeps the root it was first saved with.
    const string RootKey = "root";

    static IEnumerable<ConnectionNode> Nodes(ConnectionTree tree) => tree.Root.Descendants().Prepend(tree.Root);

    // Each node with the key its values are under here.
    static IEnumerable<(ConnectionNode Node, string Key)> Keyed(ConnectionTree tree) =>
        tree.Root.Descendants().Select(n => (n, n.Id.ToString("N"))).Prepend((tree.Root, RootKey));

    /// <summary>Notes the values the tree has now as connections.json's: just after reading it or merging with it.</summary>
    public void NoteShared(ConnectionTree tree)
    {
        _shared.Clear();
        foreach (var node in Nodes(tree))
        {
            foreach (var field in Fields.Where(f => f.Applies(node)))
            {
                _shared[(node.Id, field.Name)] = field.Get(node);
            }
        }
    }

    /// <summary>Puts this computer's own values in the tree, over the shared ones.</summary>
    public void PutOwn(ConnectionTree tree)
    {
        // A file from before RootKey has the root's values under its id.
        values.Rename(tree.Root.Id.ToString("N"), RootKey);
        foreach (var (node, key) in Keyed(tree))
        {
            var own = values.For(key);
            foreach (var field in Fields.Where(f => own.ContainsKey(f.Name) && f.Applies(node)))
            {
                field.Set(node, own[field.Name]);
            }
        }
    }

    /// <summary>
    /// Notes what the tree has now that differs from connections.json as this computer's own, and puts the shared values
    /// in its place, for a merge or a save. A node connections.json doesn't have yet gives the file its values.
    /// </summary>
    public void TakeOwn(ConnectionTree tree)
    {
        foreach (var (node, key) in Keyed(tree))
        {
            foreach (var field in Fields)
            {
                if (!field.Applies(node))
                {
                    values.Clear(key, field.Name);
                    continue;
                }

                var value = field.Get(node);
                if (!_shared.TryGetValue((node.Id, field.Name), out var shared))
                {
                    _shared[(node.Id, field.Name)] = value;
                    values.Clear(key, field.Name);
                }
                else if (JsonMerge.Same(value, shared))
                {
                    values.Clear(key, field.Name);
                }
                else
                {
                    values.Set(key, field.Name, value);
                    field.Set(node, shared);
                }
            }
        }
    }

    // Every node a tree here has held: only their values are dropped once they're gone. A tree that started empty (no
    // file yet, or one that couldn't be read) dropped every value here as the file came in, before.
    readonly HashSet<string> _seen = [];

    /// <summary>
    /// Drops this computer's values for connections <paramref name="tree"/> held and doesn't any more: deleted, here or
    /// by a merge. With <paramref name="all"/>, for every connection not in it, for a tree just read from the file.
    /// </summary>
    public void Prune(ConnectionTree tree, bool all = false)
    {
        var ids = Keyed(tree).Select(k => k.Key).ToHashSet();
        values.Keep(key => ids.Contains(key) || !all && !_seen.Contains(key));
        _seen.UnionWith(ids);
    }

    /// <summary>Writes this computer's file, if anything in it changed.</summary>
    public void Save()
    {
        try
        {
            values.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.For("Storage").LogWarning(e, "Couldn't save this computer's own connection settings");
        }
    }
}
