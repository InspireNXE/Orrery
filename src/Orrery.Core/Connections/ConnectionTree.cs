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

    /// <summary>Raised, before <see cref="Changed"/>, with the ids of everything deleted: the node, what's in it and its guests.</summary>
    public event EventHandler<NodesRemovedEventArgs>? Removed;

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
        bool wasGuest = (node as ConnectionEntry)?.GuestOf is not null;
        int oldIndex = Detach(node);

        // Removing first shifts later siblings up by one.
        if (index is { } i && !wasGuest && oldParent == parent && oldIndex < i)
        {
            index = i - 1;
        }

        Insert(node, parent, index);
        NotifyChanged();
    }

    /// <summary>Adds a new connection as a guest of <paramref name="host"/>, at <paramref name="index"/> among its guests (end when null).</summary>
    public void AddGuest(ConnectionEntry guest, ConnectionEntry host, int? index = null)
    {
        if (guest.Parent is not null || guest.GuestOf is not null)
        {
            throw new InvalidOperationException("The connection is already in a tree; use MoveToHost.");
        }

        CheckGuest(guest, host);
        InsertGuest(guest, host, index);
        NotifyChanged();
    }

    /// <summary>Makes a connection already in the tree a guest of <paramref name="host"/>, or moves it among the host's guests.</summary>
    public void MoveToHost(ConnectionEntry guest, ConnectionEntry host, int? index = null)
    {
        CheckGuest(guest, host);
        bool sameHost = guest.GuestOf == host;
        int oldIndex = Detach(guest);
        if (index is { } i && sameHost && oldIndex < i)
        {
            index = i - 1;
        }

        InsertGuest(guest, host, index);
        NotifyChanged();
    }

    /// <summary>Turns a guest back into a plain connection, right after its host in the host's folder.</summary>
    public void MoveOutOfHost(ConnectionEntry guest)
    {
        var host = guest.GuestOf ?? throw new InvalidOperationException("The connection isn't a guest.");
        var folder = host.Parent!;
        Move(guest, folder, folder.ChildList.IndexOf(host) + 1);
    }

    /// <summary>
    /// Removes a host but keeps its guests, as plain connections in <paramref name="destination"/>: where the
    /// host was when that's the host's own folder, else at the end.
    /// </summary>
    public void RemoveKeepingGuests(ConnectionEntry host, ConnectionFolder destination)
    {
        if (host.GuestOf is not null || host.Parent is not { } folder)
        {
            throw new InvalidOperationException("Only a host in the tree can be removed this way.");
        }

        int? index = destination == folder ? folder.ChildList.IndexOf(host) : null;
        var guests = host.Guests.ToList();
        foreach (var guest in guests)
        {
            Detach(guest);
        }

        Detach(host);
        foreach (var guest in guests)
        {
            Insert(guest, destination, index);
            index++;
        }

        Removed?.Invoke(this, new NodesRemovedEventArgs([host.Id]));
        NotifyChanged();
    }

    static void CheckGuest(ConnectionEntry guest, ConnectionEntry host)
    {
        if (guest == host)
        {
            throw new InvalidOperationException("A connection can't be its own guest.");
        }

        if (host.GuestOf is not null)
        {
            throw new InvalidOperationException("A guest can't have guests of its own.");
        }

        if (guest.IsHost)
        {
            throw new InvalidOperationException("A host can't become a guest; move its guests out first.");
        }

        if (host.Parent is null)
        {
            throw new InvalidOperationException("The host isn't in a tree.");
        }
    }

    public void Remove(ConnectionNode node)
    {
        if (node == Root)
        {
            throw new InvalidOperationException("The root folder can't be removed.");
        }

        if (node.Parent is not null)
        {
            var ids = node is ConnectionFolder folder
                ? folder.Descendants().Select(n => n.Id).Prepend(node.Id)
                : (node as ConnectionEntry)?.Guests.Select(g => g.Id).Prepend(node.Id) ?? [node.Id];
            Detach(node);
            Removed?.Invoke(this, new NodesRemovedEventArgs(ids.ToList()));
        }

        NotifyChanged();
    }

    /// <summary>
    /// Brings in a detached folder from elsewhere (an import): the folder itself, or just its contents,
    /// under <paramref name="parent"/>. Contents brought in alone take the settings they inherited from the
    /// source folder, guests of a host among them included. Raises <see cref="Changed"/> once, however many nodes arrive.
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
                child.Settings.FillFrom(source.Settings);

                // A guest skips its host for what it keeps, so it takes those from the source folder itself.
                foreach (var guest in (child as ConnectionEntry)?.Guests ?? [])
                {
                    guest.Settings.FillFrom(source.Settings, InheritableSettings.KeptByGuests);
                }

                Insert(child, parent, null);
            }
        }

        NotifyChanged();
    }

    /// <summary>
    /// Copies a connection or a whole folder, placing the copy right after the original with " (copy)" on
    /// its name; a host's copy has copies of its guests, and a guest's copy stays with the same host. Copies
    /// get new ids and lose their import source, so a later re-import doesn't take them for the originals.
    /// Saved credentials are shared by id, not copied.
    /// </summary>
    public ConnectionNode Duplicate(ConnectionNode node)
    {
        if (node == Root)
        {
            throw new InvalidOperationException("The root folder can't be duplicated.");
        }

        var copy = Copy(node);
        copy.Name = $"{node.Name} (copy)";
        if (node is ConnectionEntry { GuestOf: { } host } guest)
        {
            InsertGuest((ConnectionEntry)copy, host, host.GuestList!.IndexOf(guest) + 1);
        }
        else
        {
            var parent = node.Parent!;
            Insert(copy, parent, parent.ChildList.IndexOf(node) + 1);
        }

        NotifyChanged();
        return copy;
    }

    static ConnectionNode Copy(ConnectionNode node)
    {
        ConnectionNode copy = node switch
        {
            ConnectionEntry entry => new ConnectionEntry { Host = entry.Host, MacAddress = entry.MacAddress, WakeBroadcast = entry.WakeBroadcast, ManagementUrl = entry.ManagementUrl, StartFolder = entry.StartFolder,
                RunAsAdministrator = entry.RunAsAdministrator, PortForwards = entry.PortForwards?.ToList() },
            ConnectionFolder folder => new ConnectionFolder { Expanded = folder.Expanded },
            _ => throw new NotSupportedException(node.GetType().Name),
        };
        copy.Name = node.Name;
        copy.Notes = node.Notes;
        copy.Settings = node.Settings.Clone();
        copy.Commands = node.Commands?.Select(c => c.Clone()).ToList();

        if (node is ConnectionFolder source && copy is ConnectionFolder target)
        {
            foreach (var child in source.ChildList)
            {
                var childCopy = Copy(child);
                target.ChildList.Add(childCopy);
                childCopy.Parent = target;

                // A guest sits in its host's folder.
                foreach (var guest in (childCopy as ConnectionEntry)?.Guests ?? [])
                {
                    guest.Parent = target;
                }
            }
        }
        else if (node is ConnectionEntry { GuestList: { } guests } original && copy is ConnectionEntry host)
        {
            host.GuestsCollapsed = original.GuestsCollapsed;
            host.GuestList = [];
            foreach (var guest in guests)
            {
                var guestCopy = (ConnectionEntry)Copy(guest);
                host.GuestList.Add(guestCopy);
                guestCopy.GuestOf = host;
            }
        }

        return copy;
    }

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>After <see cref="ConnectionMerge"/> has set the children and guests: the links put right, then the usual events.</summary>
    internal void Merged(IReadOnlyList<ConnectionNode> removed)
    {
        Relink(Root);
        if (removed.Count > 0)
        {
            Removed?.Invoke(this, new NodesRemovedEventArgs(removed.Select(n => n.Id).ToList()));
        }

        NotifyChanged();
    }

    static void Insert(ConnectionNode node, ConnectionFolder parent, int? index)
    {
        var children = parent.ChildList;
        children.Insert(Math.Clamp(index ?? children.Count, 0, children.Count), node);
        node.Parent = parent;
        foreach (var guest in (node as ConnectionEntry)?.Guests ?? [])
        {
            guest.Parent = parent;
        }
    }

    static void InsertGuest(ConnectionEntry guest, ConnectionEntry host, int? index)
    {
        var guests = host.GuestList ??= [];
        guests.Insert(Math.Clamp(index ?? guests.Count, 0, guests.Count), guest);
        guest.Parent = host.Parent;
        guest.GuestOf = host;
    }

    /// <summary>
    /// Takes a node out of its folder, or out of its host's guests (a host left with none stops being one).
    /// Returns where it was in that list.
    /// </summary>
    static int Detach(ConnectionNode node)
    {
        int index;
        if (node is ConnectionEntry { GuestOf: { } host } guest)
        {
            index = host.GuestList!.IndexOf(guest);
            host.GuestList.RemoveAt(index);
            if (host.GuestList.Count == 0)
            {
                host.GuestList = null;
            }

            guest.GuestOf = null;
        }
        else
        {
            index = node.Parent!.ChildList.IndexOf(node);
            node.Parent.ChildList.RemoveAt(index);
        }

        node.Parent = null;
        return index;
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
            else if (child is ConnectionEntry { GuestList: { } guests } host)
            {
                if (guests.Count == 0)
                {
                    host.GuestList = null;
                }

                foreach (var guest in guests)
                {
                    if (guest is null)
                    {
                        throw new InvalidDataException($"\"{host.Name}\" has an empty guest, so the file may be damaged.");
                    }

                    if (guest.IsHost)
                    {
                        throw new InvalidDataException($"\"{guest.Name}\", a guest of \"{host.Name}\", has guests of its own. Orrery keeps one level.");
                    }

                    guest.Parent = folder;
                    guest.GuestOf = host;
                }
            }
        }
    }
}

public sealed class NodesRemovedEventArgs(IReadOnlyList<Guid> ids) : EventArgs
{
    public IReadOnlyList<Guid> Ids { get; } = ids;
}
