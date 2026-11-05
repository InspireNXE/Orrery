using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Orrery.Core.Storage;

namespace Orrery.Core.Connections;

/// <summary>What a merge of the connection tree with the file on disk changed here, and what both computers changed.</summary>
/// <param name="Added">Connections and folders that arrived from the file.</param>
/// <param name="Removed">Ones the other computer deleted, deleted here too.</param>
/// <param name="Updated">Ones whose fields or place changed to the file's.</param>
/// <param name="Conflicts">Plain sentences, one per thing both computers changed, saying which change was kept.</param>
public sealed record ConnectionMergeReport(int Added, int Removed, int Updated, IReadOnlyList<string> Conflicts)
{
    public bool ChangedAnything => Added + Removed + Updated > 0;
}

/// <summary>
/// Brings changes another computer saved to <c>connections.json</c> (through a sync app sharing the data folder) into
/// the tree open here, keeping this computer's own changes: a three-way merge of each connection and folder by id,
/// against the file as this computer last read or wrote it. Applied to the open tree in place, so sessions and the
/// properties pane keep the objects they hold.
/// <list type="bullet">
/// <item>A field only one computer changed takes that change; one both changed keeps this computer's, as a conflict.</item>
/// <item>Where a node sits, and the order in a folder, take the side that changed them.</item>
/// <item>Added on either side: kept. Deleted on one side: deleted, unless the other side changed it, which keeps it.</item>
/// </list>
/// </summary>
public static class ConnectionMerge
{
    const string RootKey = "root";
    const string GuestPrefix = "guest:";

    sealed record Flat(ConnectionNode Node, string Parent, JsonObject Props)
    {
        public bool IsFolder => Node is ConnectionFolder;
    }

    sealed class Side
    {
        public Dictionary<string, Flat> Nodes { get; } = [];

        // Each folder's children, and each host's guests (under GuestPrefix + its key), in order.
        public Dictionary<string, List<string>> Order { get; } = [];

        public static Side Of(ConnectionFolder? root)
        {
            var side = new Side();
            if (root is not null)
            {
                side.Nodes[RootKey] = new Flat(root, "", PropsOf(root, isRoot: true));
                side.Walk(root, RootKey);
            }

            return side;
        }

        void Walk(ConnectionFolder folder, string folderKey)
        {
            var order = Order[folderKey] = [];
            foreach (var child in folder.ChildList)
            {
                var key = KeyOf(child);
                order.Add(key);
                Nodes[key] = new Flat(child, folderKey, PropsOf(child, isRoot: false));
                if (child is ConnectionFolder nested)
                {
                    Walk(nested, key);
                }
                else if (child is ConnectionEntry { GuestList: { } guests })
                {
                    var guestOrder = Order[GuestPrefix + key] = [];
                    foreach (var guest in guests)
                    {
                        var guestKey = KeyOf(guest);
                        guestOrder.Add(guestKey);
                        Nodes[guestKey] = new Flat(guest, GuestPrefix + key, PropsOf(guest, isRoot: false));
                    }
                }
            }
        }
    }

    static string KeyOf(ConnectionNode node) => node.Id.ToString("N");

    /// <summary>A node's own fields as JSON, without what's in it.</summary>
    static JsonObject PropsOf(ConnectionNode node, bool isRoot)
    {
        var props = JsonSerializer.SerializeToNode(node, ConnectionStore.JsonOptions)!.AsObject();
        props.Remove("children");
        props.Remove("guests");

        // Each computer's empty tree started with its own root id; the root is the root.
        if (isRoot)
        {
            props.Remove("id");
        }

        return props;
    }

    /// <summary>
    /// Merges <paramref name="theirs"/> (the file now) into <paramref name="tree"/> (open here), both measured against
    /// <paramref name="baseline"/> (the file as this computer last read or wrote it; null if there was none).
    /// </summary>
    public static ConnectionMergeReport Merge(ConnectionTree tree, ConnectionFolder? baseline, ConnectionFolder theirs)
    {
        var b = Side.Of(baseline);
        var m = Side.Of(tree.Root);
        var t = Side.Of(theirs);
        var conflicts = new List<string>();

        // Which nodes stay, with their fields and parent.
        var kept = new Dictionary<string, (Flat From, string Parent, JsonObject Props)>();
        foreach (var key in m.Nodes.Keys.Concat(t.Nodes.Keys).Distinct().ToList())
        {
            b.Nodes.TryGetValue(key, out var bf);
            m.Nodes.TryGetValue(key, out var mf);
            t.Nodes.TryGetValue(key, out var tf);
            if (mf is not null && tf is not null)
            {
                if (mf.IsFolder != tf.IsFolder)
                {
                    kept[key] = (mf, mf.Parent, mf.Props);
                    continue;
                }

                var fields = new List<string>();
                var props = JsonMerge.Merge(bf?.Props, mf.Props, tf.Props, fields);
                if (fields.Count > 0)
                {
                    conflicts.Add($"\"{mf.Node.Name}\": {string.Join(", ", fields.Select(Label))} changed on both computers; this computer's kept");
                }

                var parent = bf is not null && mf.Parent == bf.Parent ? tf.Parent : mf.Parent;
                kept[key] = (mf, parent, props);
            }
            else if (mf is not null)
            {
                if (bf is null)
                {
                    kept[key] = (mf, mf.Parent, mf.Props); // Added here.
                }
                else if (Changed(bf, mf))
                {
                    kept[key] = (mf, mf.Parent, mf.Props);
                    conflicts.Add($"\"{mf.Node.Name}\" was deleted on the other computer but changed on this one, so it's kept");
                }
            }
            else if (tf is not null)
            {
                if (bf is null)
                {
                    kept[key] = (tf, tf.Parent, tf.Props); // Added there.
                }
                else if (Changed(bf, tf))
                {
                    kept[key] = (tf, tf.Parent, tf.Props);
                    conflicts.Add($"\"{tf.Node.Name}\" was deleted on this computer but changed on the other, so it's back");
                }
            }
        }

        // The root always stays, even if a damaged side lacked it.
        if (!kept.ContainsKey(RootKey))
        {
            kept[RootKey] = (m.Nodes[RootKey], "", m.Nodes[RootKey].Props);
        }

        FixParents(kept, m, t);

        // The node objects: this tree's where it has them, new ones from the file's fields otherwise.
        var nodes = new Dictionary<string, ConnectionNode>();
        int added = 0, updated = 0;
        foreach (var (key, k) in kept)
        {
            if (m.Nodes.TryGetValue(key, out var mine))
            {
                nodes[key] = mine.Node;
                bool fieldsChanged = !JsonMerge.Same(mine.Props, k.Props);
                if (fieldsChanged)
                {
                    CopyFields(FromProps(k.Props), mine.Node);
                }

                if (fieldsChanged || mine.Parent != k.Parent)
                {
                    updated++;
                }
            }
            else
            {
                nodes[key] = FromProps(k.Props);
                added++;
            }
        }

        var removed = m.Nodes.Where(n => !kept.ContainsKey(n.Key)).Select(n => n.Value.Node).ToList();

        // Then the shape: each folder's children and each host's guests, in order.
        var byParent = kept.Where(k => k.Key != RootKey).GroupBy(k => k.Value.Parent).ToDictionary(g => g.Key, g => g.Select(k => k.Key).ToList());
        foreach (var node in nodes.Values)
        {
            if (node is ConnectionFolder folder)
            {
                var folderKey = folder == tree.Root ? RootKey : KeyOf(folder);
                var children = Ordered(byParent.GetValueOrDefault(folderKey) ?? [], b, m, t, folderKey);
                var before = folder.ChildList.Select(KeyOf).ToList();
                folder.ChildList = children.Select(c => nodes[c]).ToList();
                if (!before.SequenceEqual(folder.ChildList.Select(KeyOf)) && m.Nodes.ContainsKey(folderKey))
                {
                    updated++;
                }
            }
            else if (node is ConnectionEntry entry)
            {
                var guestKey = GuestPrefix + KeyOf(entry);
                var guests = Ordered(byParent.GetValueOrDefault(guestKey) ?? [], b, m, t, guestKey);
                entry.GuestList = guests.Count == 0 ? null : guests.Select(g => (ConnectionEntry)nodes[g]).ToList();
                entry.GuestOf = null;
            }
        }

        foreach (var node in removed)
        {
            node.Parent = null;
            if (node is ConnectionEntry entry)
            {
                entry.GuestOf = null;
            }
        }

        tree.Merged(removed);
        return new ConnectionMergeReport(added, removed.Count, updated, conflicts);
    }

    static bool Changed(Flat before, Flat after) => before.Parent != after.Parent || !JsonMerge.Same(before.Props, after.Props);

    static string Label(string field) => field.Replace("settings.", "", StringComparison.Ordinal);

    /// <summary>
    /// Every kept node gets a parent that's kept and can hold it: a deleted folder something still sits in comes back,
    /// a guest whose host went or became a guest moves into the host's folder, and a loop is broken at the root.
    /// </summary>
    static void FixParents(Dictionary<string, (Flat From, string Parent, JsonObject Props)> kept, Side m, Side t)
    {
        for (bool again = true; again;)
        {
            again = false;
            foreach (var (key, k) in kept.ToList())
            {
                if (key == RootKey)
                {
                    continue;
                }

                var parent = k.Parent;
                if (parent.StartsWith(GuestPrefix, StringComparison.Ordinal))
                {
                    var hostKey = parent[GuestPrefix.Length..];
                    bool hostOk = k.From.Node is ConnectionEntry && kept.TryGetValue(hostKey, out var host) && host.From.Node is ConnectionEntry
                        && !host.Parent.StartsWith(GuestPrefix, StringComparison.Ordinal) && !kept.Any(o => o.Value.Parent == GuestPrefix + key);
                    if (!hostOk)
                    {
                        var hostFolder = kept.TryGetValue(hostKey, out var h) && !h.Parent.StartsWith(GuestPrefix, StringComparison.Ordinal) ? h.Parent : RootKey;
                        kept[key] = k with { Parent = hostFolder };
                        again = true;
                    }

                    continue;
                }

                if (kept.TryGetValue(parent, out var folder))
                {
                    if (!folder.From.IsFolder)
                    {
                        kept[key] = k with { Parent = RootKey };
                        again = true;
                    }

                    continue;
                }

                // The folder was deleted on one side while the other put something in it: it comes back.
                var back = m.Nodes.GetValueOrDefault(parent) ?? t.Nodes.GetValueOrDefault(parent);
                if (back is { IsFolder: true })
                {
                    kept[parent] = (back, back.Parent, back.Props);
                }
                else
                {
                    kept[key] = k with { Parent = RootKey };
                }

                again = true;
            }

            // A folder moved into its own contents, one side each way: out to the root.
            foreach (var (key, _) in kept.ToList())
            {
                var seen = new HashSet<string>();
                for (var at = key; at != RootKey && kept.TryGetValue(at, out var node); at = Folder(node.Parent))
                {
                    if (!seen.Add(at))
                    {
                        kept[key] = kept[key] with { Parent = RootKey };
                        again = true;
                        break;
                    }
                }
            }
        }

        static string Folder(string parent) =>
            parent.StartsWith(GuestPrefix, StringComparison.Ordinal) ? parent[GuestPrefix.Length..] : parent;
    }

    /// <summary>
    /// <paramref name="members"/> in the order of the side that reordered this folder (this computer's, if both did),
    /// with what only the other side has placed after the sibling it follows there.
    /// </summary>
    static List<string> Ordered(List<string> members, Side b, Side m, Side t, string parentKey)
    {
        var bo = b.Order.GetValueOrDefault(parentKey) ?? [];
        var mo = m.Order.GetValueOrDefault(parentKey) ?? [];
        var to = t.Order.GetValueOrDefault(parentKey) ?? [];
        // Reordered: the entries it shares with the base in another order. Adding or deleting one isn't reordering.
        bool mineReordered = !mo.Where(bo.Contains).SequenceEqual(bo.Where(mo.Contains));
        var (chosen, other) = mineReordered ? (mo, to) : (to, mo);
        var set = members.ToHashSet();
        var result = chosen.Where(set.Contains).ToList();
        foreach (var key in other.Concat(members))
        {
            if (!set.Contains(key) || result.Contains(key))
            {
                continue;
            }

            int i = other.IndexOf(key);
            if (i < 0)
            {
                result.Add(key);
                continue;
            }

            // After the sibling it follows on its own side, and after what the chosen side added there too.
            var before = other.Take(i).LastOrDefault(result.Contains);
            int at = before is null ? 0 : result.IndexOf(before) + 1;
            while (at < result.Count && !other.Contains(result[at]))
            {
                at++;
            }

            result.Insert(at, key);
        }

        return result;
    }

    /// <summary>A detached node with these fields and nothing in it.</summary>
    static ConnectionNode FromProps(JsonObject props) => props.Deserialize<ConnectionNode>(ConnectionStore.JsonOptions)!;

    static readonly Dictionary<Type, PropertyInfo[]> Fields = [];

    // A node's own fields, those the file holds, copied onto the node open here.
    static void CopyFields(ConnectionNode from, ConnectionNode to)
    {
        PropertyInfo[] fields;
        lock (Fields)
        {
            if (!Fields.TryGetValue(to.GetType(), out fields!))
            {
                fields = Fields[to.GetType()] = to.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.Name != nameof(ConnectionNode.Id) && p.CanRead && p.SetMethod is { IsPublic: true }
                        && p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
                    .ToArray();
            }
        }

        foreach (var field in fields)
        {
            field.SetValue(to, field.GetValue(from));
        }
    }
}
