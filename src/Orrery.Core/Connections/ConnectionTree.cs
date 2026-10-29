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

    public ConnectionFolder Root { get; }

    public event EventHandler? Changed;

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
