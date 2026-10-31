using Orrery.Core.Connections;

namespace Orrery.App.Connections;

/// <summary>How a drop shows on the row under the pointer.</summary>
enum DropPosition
{
    Before,
    After,
    Into,
}

/// <summary>
/// Where a dragged row goes: into <see cref="Folder"/> at <see cref="Index"/>, or, when <see cref="Host"/> is
/// set, among that host's guests (end when <see cref="Index"/> is null).
/// </summary>
sealed record DropPlan(ConnectionFolder Folder, ConnectionEntry? Host, int? Index, DropPosition Position);

/// <summary>
/// Decides drops in the connection list. A folder takes the row into it. A connection splits its row in
/// three when the dragged row could be its guest (top quarter before, middle half as a guest, bottom
/// quarter after), and in two otherwise. See docs/design-host-groups.md §4.
/// </summary>
static class TreeDrop
{
    /// <param name="target">The row under the pointer; null for the empty space below the rows.</param>
    /// <param name="fraction">How far down that row the pointer is, 0 to 1.</param>
    public static DropPlan? Plan(ConnectionFolder root, ConnectionNode dragged, ConnectionNode? target, double fraction)
    {
        if (target is null)
        {
            return new DropPlan(root, null, null, DropPosition.Into);
        }

        if (target == dragged)
        {
            return null;
        }

        if (target is ConnectionFolder folder)
        {
            return IsInside(folder, dragged) ? null : new DropPlan(folder, null, null, DropPosition.Into);
        }

        var entry = (ConnectionEntry)target;
        bool after = fraction > 0.5;
        if (entry.GuestOf is { } host)
        {
            // Among a host's guests only connections that can be guests fit, and not the host itself.
            if (!CanBeGuest(dragged) || dragged == host)
            {
                return null;
            }

            return new DropPlan(host.Parent!, host, host.Guests.ToList().IndexOf(entry) + (after ? 1 : 0), after ? DropPosition.After : DropPosition.Before);
        }

        var parent = entry.Parent!;
        if (IsInside(parent, dragged))
        {
            return null;
        }

        if (CanBeGuest(dragged) && fraction is >= 0.25 and <= 0.75)
        {
            return new DropPlan(parent, entry, null, DropPosition.Into);
        }

        return new DropPlan(parent, null, parent.Children.ToList().IndexOf(entry) + (after ? 1 : 0), after ? DropPosition.After : DropPosition.Before);
    }

    /// <summary>A connection without guests of its own; one level only.</summary>
    static bool CanBeGuest(ConnectionNode node) => node is ConnectionEntry { IsHost: false };

    /// <summary>True if <paramref name="folder"/> is <paramref name="dragged"/> or inside it.</summary>
    static bool IsInside(ConnectionFolder folder, ConnectionNode dragged) =>
        dragged is ConnectionFolder draggedFolder && (folder == draggedFolder || folder.Ancestors().Contains(draggedFolder));
}
