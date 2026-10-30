namespace Orrery.Core.Connections;

/// <summary>
/// The connection tree and every change to its shape. Edits to a node's own fields are made directly on
/// the node, followed by <see cref="NotifyChanged"/> so listeners (the saver, the UI) pick them up.
/// </summary>
public sealed class ConnectionTree
{
    public ConnectionTree(ConnectionFolder root)
    {
        Root = root;
        root.Parent = null;
        Relink(root);
    }

    /// <summary>
    /// Most folders on the way from the root to any node, the root not counted. Anything that adds or moves a
    /// folder checks <see cref="CanPlace"/> first; imports are held to it too.
    /// </summary>
    public const int MaxFolderDepth = 60;

    public ConnectionFolder Root { get; }

    public event EventHandler? Changed;

    /// <summary>How many folders deep <paramref name="folder"/> is: 0 for the root, 1 for a top-level folder.</summary>
    public static int DepthOf(ConnectionFolder folder) => folder.Ancestors().Count();

    /// <summary>True if <paramref name="node"/> and everything in it fits under <paramref name="parent"/> within <see cref="MaxFolderDepth"/>.</summary>
    public static bool CanPlace(ConnectionNode node, ConnectionFolder parent) => DepthOf(parent) + FolderLevels(node) <= MaxFolderDepth;

    /// <summary>Folder levels a node brings with it: 0 for a connection, 1 for an empty folder.</summary>
    static int FolderLevels(ConnectionNode node)
    {
        int deepest = 0;
        var pending = new Stack<(ConnectionNode Node, int Levels)>([(node, 0)]);
        while (pending.TryPop(out var item))
        {
            if (item.Node is not ConnectionFolder folder)
            {
                continue;
            }

            deepest = Math.Max(deepest, item.Levels + 1);
            foreach (var child in folder.Children)
            {
                pending.Push((child, item.Levels + 1));
            }
        }

        return deepest;
    }

    public static ConnectionTree Empty() => new(new ConnectionFolder { Name = "Connections" });

    public ConnectionNode? Find(Guid id) => Root.Id == id ? Root : Root.Descendants().FirstOrDefault(n => n.Id == id);

    public IEnumerable<ConnectionEntry> AllConnections() => Root.Descendants().OfType<ConnectionEntry>();

    public void Add(ConnectionNode node, ConnectionFolder parent, int? index = null)
    {
        if (node.Parent is not null)
        {
            throw new InvalidOperationException("The node is already in a tree; use Move.");
        }

        Insert(node, parent, index);
        NotifyChanged();
    }

    /// <summary>Moves a node to <paramref name="parent"/> at <paramref name="index"/> (end when null).</summary>
    public void Move(ConnectionNode node, ConnectionFolder parent, int? index = null)
    {
        if (node == Root)
        {
            throw new InvalidOperationException("The root folder can't be moved.");
        }

        if (node == parent || (node is ConnectionFolder folder && parent.Ancestors().Contains(folder)))
        {
            throw new InvalidOperationException("A folder can't be moved into itself.");
        }

        var oldParent = node.Parent!;
        int oldIndex = oldParent.ChildList.IndexOf(node);
        oldParent.ChildList.RemoveAt(oldIndex);

        // Removing first shifts later siblings up by one.
        if (index is { } i && oldParent == parent && oldIndex < i)
        {
            index = i - 1;
        }

        node.Parent = null;
        Insert(node, parent, index);
        NotifyChanged();
    }

    public void Remove(ConnectionNode node)
    {
        if (node == Root)
        {
            throw new InvalidOperationException("The root folder can't be removed.");
        }

        node.Parent?.ChildList.Remove(node);
        node.Parent = null;
        NotifyChanged();
    }

    /// <summary>
    /// Brings in a detached folder from elsewhere (an import): the folder itself, or just its contents,
    /// under <paramref name="parent"/>. Raises <see cref="Changed"/> once, however many nodes arrive.
    /// </summary>
    public void AddFrom(ConnectionFolder source, ConnectionFolder parent, bool includeSourceFolder)
    {
        if (source.Parent is not null)
        {
            throw new InvalidOperationException("The source folder must not be in a tree.");
        }

        if (includeSourceFolder)
        {
            Insert(source, parent, null);
        }
        else
        {
            foreach (var child in source.ChildList.ToList())
            {
                source.ChildList.Remove(child);
                Insert(child, parent, null);
            }
        }

        NotifyChanged();
    }

    /// <summary>
    /// Copies a connection or a whole folder, placing the copy right after the original with " (copy)" on
    /// its name. Copies get new ids and lose their import source, so a later re-import doesn't take them
    /// for the originals. Saved credentials are shared by id, not copied.
    /// </summary>
    public ConnectionNode Duplicate(ConnectionNode node)
    {
        if (node == Root)
        {
            throw new InvalidOperationException("The root folder can't be duplicated.");
        }

        var copy = Copy(node);
        copy.Name = $"{node.Name} (copy)";
        var parent = node.Parent!;
        Insert(copy, parent, parent.ChildList.IndexOf(node) + 1);
        NotifyChanged();
        return copy;
    }

    static ConnectionNode Copy(ConnectionNode node)
    {
        ConnectionNode copy = node switch
        {
            ConnectionEntry entry => new ConnectionEntry { Host = entry.Host },
            ConnectionFolder folder => new ConnectionFolder { Expanded = folder.Expanded },
            _ => throw new NotSupportedException(node.GetType().Name),
        };
        copy.Name = node.Name;
        copy.Notes = node.Notes;
        copy.Settings = node.Settings.Clone();

        if (node is ConnectionFolder source && copy is ConnectionFolder target)
        {
            foreach (var child in source.ChildList)
            {
                var childCopy = Copy(child);
                target.ChildList.Add(childCopy);
                childCopy.Parent = target;
            }
        }

        return copy;
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    static void Insert(ConnectionNode node, ConnectionFolder parent, int? index)
    {
        var children = parent.ChildList;
        children.Insert(Math.Clamp(index ?? children.Count, 0, children.Count), node);
        node.Parent = parent;
    }

    static void Relink(ConnectionFolder folder)
    {
        foreach (var child in folder.ChildList)
        {
            child.Parent = folder;
            if (child is ConnectionFolder nested)
            {
                Relink(nested);
            }
        }
    }
}
