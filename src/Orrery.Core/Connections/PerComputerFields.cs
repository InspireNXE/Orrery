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
        node is ConnectionEntry entry && SettingsResolver.Resolve(entry).Protocol.Value is Protocol.Local or Protocol.Serial;

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

    static string KeyOf(ConnectionNode node) => node.Id.ToString("N");

    static IEnumerable<ConnectionNode> Nodes(ConnectionTree tree) => tree.Root.Descendants().Prepend(tree.Root);

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
        foreach (var node in Nodes(tree))
        {
            var own = values.For(KeyOf(node));
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
        var ids = new HashSet<string>();
        foreach (var node in Nodes(tree))
        {
            var key = KeyOf(node);
            ids.Add(key);
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

        values.Keep(ids.Contains);
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
