using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>How a drop shows on <see cref="DropPlan.Mark"/>'s row: a line above or below it, or the row lit up.</summary>
enum DropPosition
{
    Before,
    After,
    Into,
}

/// <summary>
/// Where a dragged row goes: into <see cref="Folder"/> at <see cref="Index"/>, or, when <see cref="Host"/> is
/// set, among that host's guests (end when <see cref="Index"/> is null). <see cref="Mark"/> is the row that
/// shows it, as <see cref="Position"/> says; null shows nothing.
/// </summary>
sealed record DropPlan(ConnectionFolder Folder, ConnectionEntry? Host, int? Index, DropPosition Position, ConnectionNode? Mark);

/// <summary>
/// Decides drops in the connection list. A folder takes the rows into it. A connection splits its row in
/// three when every dragged row could be its guest (top quarter before, middle half as guests, bottom
/// quarter after), and in two otherwise. Several rows move together, in list order. See
/// docs/design-host-groups.md §4.
///
/// Each gap between two rows is one place with one line, drawn on the lower row. The gap below a folder's or
/// host's last row is outside that group, right before the next row; to add at the end of a group, drop onto
/// the group's own row.
/// </summary>
static class TreeDrop
{
    public static DropPlan? Plan(ConnectionFolder root, ConnectionNode dragged, ConnectionNode? target, double fraction) =>
        Plan(root, [dragged], target, fraction);

    /// <param name="dragged">The rows being dragged, as <see cref="Normalize"/> gives them.</param>
    /// <param name="target">The row under the pointer; null for the empty space below the rows.</param>
    /// <param name="fraction">How far down that row the pointer is, 0 to 1.</param>
    public static DropPlan? Plan(ConnectionFolder root, IReadOnlyList<ConnectionNode> dragged, ConnectionNode? target, double fraction)
    {
        if (dragged.Count == 0)
        {
            return null;
        }

        if (target is null)
        {
            return new DropPlan(root, null, null, DropPosition.Into, null);
        }

        if (dragged.Contains(target))
        {
            return null;
        }

        if (target is ConnectionFolder folder)
        {
            return IsInside(folder, dragged) ? null : new DropPlan(folder, null, null, DropPosition.Into, folder);
        }

        var entry = (ConnectionEntry)target;
        bool after = fraction > 0.5;
        bool canBeGuests = dragged.All(CanBeGuest);
        if (entry.GuestOf is { } host)
        {
            if (after && host.Guests.Last() == entry)
            {
                return BelowGroup(root, dragged, entry);
            }

            // Among a host's guests only connections that can be guests fit, and not the host itself.
            if (!canBeGuests || dragged.Contains(host))
            {
                return null;
            }

            return Insert(host.Parent!, host, host.Guests.ToList().IndexOf(entry) + (after ? 1 : 0));
        }

        var parent = entry.Parent!;
        if (IsInside(parent, dragged))
        {
            return null;
        }

        if (canBeGuests && fraction is >= 0.25 and <= 0.75)
        {
            return new DropPlan(parent, entry, null, DropPosition.Into, entry);
        }

        // An open host's guests sit right below its row, so the row's lower edge is before the first guest.
        // After the whole group is below the last guest; rows that can't be guests can only go before it here.
        if (after && entry is { IsHost: true, GuestsCollapsed: false })
        {
            return canBeGuests ? Insert(parent, entry, 0) : Insert(parent, null, parent.Children.ToList().IndexOf(entry));
        }

        if (after && parent != root && parent.Children.Last() == entry)
        {
            return BelowGroup(root, dragged, entry);
        }

        return Insert(parent, null, parent.Children.ToList().IndexOf(entry) + (after ? 1 : 0));
    }

    /// <summary>
    /// The gap below <paramref name="row"/>, the last row of its folder or host: right after the outermost
    /// group it ends, before whatever row comes next.
    /// </summary>
    static DropPlan? BelowGroup(ConnectionFolder root, IReadOnlyList<ConnectionNode> dragged, ConnectionNode row)
    {
        var group = row;
        while (Owner(group) is { } owner && owner != root && LastIn(owner) == group)
        {
            group = owner;
        }

        var parent = group.Parent!;
        if (dragged.Contains(group) || IsInside(parent, dragged))
        {
            return null;
        }

        return Insert(parent, null, parent.Children.ToList().IndexOf(group) + 1);
    }

    /// <summary>A drop at <paramref name="index"/> among a folder's children or a host's guests, marked with a line.</summary>
    static DropPlan Insert(ConnectionFolder folder, ConnectionEntry? host, int index)
    {
        var list = host is not null ? host.Guests.Cast<ConnectionNode>().ToList() : folder.Children.ToList();
        return index < list.Count
            ? new DropPlan(folder, host, index, DropPosition.Before, list[index])
            : new DropPlan(folder, host, index, DropPosition.After, LastRow(list[^1]));
    }

    /// <summary>The folder or host a row sits in.</summary>
    static ConnectionNode? Owner(ConnectionNode node) => node is ConnectionEntry { GuestOf: { } host } ? host : node.Parent;

    static ConnectionNode? LastIn(ConnectionNode owner) => owner switch
    {
        ConnectionFolder folder => folder.Children.LastOrDefault(),
        ConnectionEntry host => host.Guests.LastOrDefault(),
        _ => null,
    };

    /// <summary>The bottom row shown for <paramref name="node"/>: its last child or guest when it's open.</summary>
    static ConnectionNode LastRow(ConnectionNode node) => node switch
    {
        ConnectionFolder { Expanded: true } folder when folder.Children.Count > 0 => LastRow(folder.Children.Last()),
        ConnectionEntry { IsHost: true, GuestsCollapsed: false } host => host.Guests.Last(),
        _ => node,
    };

    /// <summary>
    /// The selected nodes that move on their own, in list order: anything inside a selected folder, or a
    /// guest of a selected host, already moves with it.
    /// </summary>
    public static List<ConnectionNode> Normalize(ConnectionFolder root, IEnumerable<ConnectionNode> nodes)
    {
        var selected = nodes.ToHashSet();
        return root.Descendants()
            .Where(selected.Contains)
            .Where(n => !n.Ancestors().Any(selected.Contains) && !(n is ConnectionEntry { GuestOf: { } host } && selected.Contains(host)))
            .ToList();
    }

    /// <summary>Moves <paramref name="nodes"/> as <paramref name="plan"/> says, keeping their order, each right after the one before.</summary>
    public static void Apply(ConnectionTree tree, IReadOnlyList<ConnectionNode> nodes, DropPlan plan)
    {
        ConnectionNode? previous = null;
        foreach (var node in nodes)
        {
            int? index = previous is null ? plan.Index : IndexIn(plan, previous) + 1;
            if (plan.Host is { } host)
            {
                tree.MoveToHost((ConnectionEntry)node, host, index);
            }
            else
            {
                tree.Move(node, plan.Folder, index);
            }

            previous = node;
        }
    }

    static int IndexIn(DropPlan plan, ConnectionNode node) =>
        plan.Host is { } host ? host.Guests.ToList().IndexOf((ConnectionEntry)node) : plan.Folder.Children.ToList().IndexOf(node);

    /// <summary>A connection without guests of its own; one level only.</summary>
    static bool CanBeGuest(ConnectionNode node) => node is ConnectionEntry { IsHost: false };

    /// <summary>True if <paramref name="folder"/> is one of the dragged folders or inside one.</summary>
    static bool IsInside(ConnectionFolder folder, IReadOnlyList<ConnectionNode> dragged) =>
        dragged.Any(d => d is ConnectionFolder draggedFolder && (folder == draggedFolder || folder.Ancestors().Contains(draggedFolder)));
}
