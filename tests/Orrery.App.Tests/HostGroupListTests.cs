using Orrery.App.Connections;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Hosts and guests in the connection list: drops, rows, filtering and the delete choices.</summary>
public class HostGroupListTests
{
    // Lab / hv-01 (guests vm-a, vm-b), web-01, Sub; then top-level other-01
    static (ConnectionTree Tree, ConnectionFolder Lab, ConnectionFolder Sub, ConnectionEntry Host, ConnectionEntry VmA, ConnectionEntry VmB, ConnectionEntry Web, ConnectionEntry Other) Sample()
    {
        var tree = ConnectionTree.Empty();
        var lab = new ConnectionFolder { Name = "Lab" };
        var sub = new ConnectionFolder { Name = "Sub" };
        var host = new ConnectionEntry { Name = "hv-01", Host = "hv-01.lab" };
        var vmA = new ConnectionEntry { Name = "vm-a", Host = "vm-a.lab" };
        var vmB = new ConnectionEntry { Name = "vm-b", Host = "vm-b.lab" };
        var web = new ConnectionEntry { Name = "web-01", Host = "web-01.lab" };
        var other = new ConnectionEntry { Name = "other-01", Host = "other-01.example" };
        tree.Add(lab, tree.Root);
        tree.Add(host, lab);
        tree.Add(web, lab);
        tree.Add(sub, lab);
        tree.Add(other, tree.Root);
        tree.AddGuest(vmA, host);
        tree.AddGuest(vmB, host);
        return (tree, lab, sub, host, vmA, vmB, web, other);
    }

    [Theory]
    [InlineData(0.1, "Before")]
    [InlineData(0.5, "Into")]
    [InlineData(0.9, "After")]
    public void A_connection_row_splits_in_three_for_a_connection_that_can_be_its_guest(double fraction, string position)
    {
        var expected = Enum.Parse<DropPosition>(position);
        var (tree, lab, _, _, _, _, web, other) = Sample();

        var plan = TreeDrop.Plan(tree.Root, other, web, fraction)!;

        Assert.Equal(expected, plan.Position);
        if (expected == DropPosition.Into)
        {
            Assert.Same(web, plan.Host);
        }
        else
        {
            Assert.Null(plan.Host);
            Assert.Same(lab, plan.Folder);
            Assert.Equal(expected == DropPosition.Before ? 1 : 2, plan.Index);
        }
    }

    [Fact]
    public void Folders_and_hosts_only_go_before_or_after_a_connection()
    {
        var (tree, _, sub, host, _, _, web, other) = Sample();

        Assert.Equal(DropPosition.Before, TreeDrop.Plan(tree.Root, sub, other, 0.4)!.Position);
        Assert.Equal(DropPosition.After, TreeDrop.Plan(tree.Root, host, other, 0.6)!.Position);
        Assert.Null(TreeDrop.Plan(tree.Root, host, web, 0.5)!.Host);
    }

    [Fact]
    public void An_open_hosts_lower_edge_is_before_its_first_guest_not_after_the_group()
    {
        var (tree, lab, sub, host, _, _, _, other) = Sample();

        var guest = TreeDrop.Plan(tree.Root, other, host, 0.9)!;
        Assert.Same(host, guest.Host);
        Assert.Equal(0, guest.Index);

        // Rows that can't be guests can only go before an open host.
        var folder = TreeDrop.Plan(tree.Root, sub, host, 0.9)!;
        Assert.Null(folder.Host);
        Assert.Equal(DropPosition.Before, folder.Position);
        Assert.Equal(0, folder.Index);

        host.GuestsCollapsed = true;
        var closed = TreeDrop.Plan(tree.Root, other, host, 0.9)!;
        Assert.Null(closed.Host);
        Assert.Same(lab, closed.Folder);
        Assert.Equal(1, closed.Index);
    }

    [Fact]
    public void A_guest_row_takes_connections_among_the_hosts_guests()
    {
        var (tree, lab, sub, host, vmA, vmB, _, other) = Sample();

        var before = TreeDrop.Plan(tree.Root, other, vmB, 0.2)!;
        Assert.Same(host, before.Host);
        Assert.Equal(1, before.Index);
        Assert.Equal(DropPosition.Before, before.Position);

        var after = TreeDrop.Plan(tree.Root, vmA, vmB, 0.8)!;
        Assert.Same(host, after.Host);
        Assert.Equal(2, after.Index);

        Assert.Null(TreeDrop.Plan(tree.Root, sub, vmA, 0.5));
        Assert.Null(TreeDrop.Plan(tree.Root, host, vmA, 0.5));
    }

    [Fact]
    public void Folders_take_rows_into_them_but_not_into_themselves_and_the_space_below_means_the_top_level()
    {
        var (tree, lab, sub, _, vmA, _, _, _) = Sample();

        var plan = TreeDrop.Plan(tree.Root, vmA, sub, 0.5)!;
        Assert.Same(sub, plan.Folder);
        Assert.Null(plan.Host);
        Assert.Equal(DropPosition.Into, plan.Position);

        Assert.Null(TreeDrop.Plan(tree.Root, lab, sub, 0.5));
        Assert.Null(TreeDrop.Plan(tree.Root, lab, lab, 0.5));
        Assert.Same(tree.Root, TreeDrop.Plan(tree.Root, vmA, null, 0)!.Folder);
    }

    [Fact]
    public void A_host_row_holds_its_guests_and_remembers_being_collapsed()
    {
        var (tree, _, _, host, _, _, _, _) = Sample();
        bool saved = false;

        var lab = NodeViewModel.Build(tree.Root, null, () => saved = true).Single(r => r.Node.Name == "Lab");
        var hostRow = lab.Children.Single(r => r.Node == host);
        Assert.True(hostRow.IsHost);
        Assert.Equal("2", hostRow.GuestCount);
        Assert.Equal(["vm-a", "vm-b"], hostRow.Children.Select(r => r.Name));
        Assert.True(hostRow.IsExpanded);
        Assert.Equal("4", lab.Count);

        hostRow.IsExpanded = false;
        Assert.True(host.GuestsCollapsed);
        Assert.True(saved);
        Assert.False(NodeViewModel.Build(tree.Root, null, () => { }).Single(r => r.Node.Name == "Lab").Children.Single(r => r.Node == host).IsExpanded);
    }

    [Fact]
    public void Filtering_shows_a_matching_guest_under_its_host_and_a_matching_host_with_all_its_guests()
    {
        var (tree, _, _, host, _, _, _, _) = Sample();

        var byGuest = NodeViewModel.Build(tree.Root, "vm-b", () => { });
        var hostRow = Assert.Single(Assert.Single(byGuest).Children);
        Assert.Same(host, hostRow.Node);
        Assert.Equal(["vm-b"], hostRow.Children.Select(r => r.Name));

        var byHost = NodeViewModel.Build(tree.Root, "hv-01", () => { });
        Assert.Equal(["vm-a", "vm-b"], Assert.Single(Assert.Single(byHost).Children).Children.Select(r => r.Name));

        Assert.Empty(NodeViewModel.Build(tree.Root, "nothing like this", () => { }));
    }

    [Fact]
    public void Deleting_a_host_offers_its_own_folder_first_then_the_top_level_and_every_other_folder()
    {
        var (tree, lab, sub, host, _, _, _, _) = Sample();

        Assert.Equal([lab, tree.Root, sub], HostDeleteWindow.Folders(host, tree));
    }

    [Fact]
    public void A_host_deleted_keeping_its_guests_leaves_them_where_it_was_or_at_the_end_of_another_folder()
    {
        var (tree, lab, _, host, vmA, vmB, web, _) = Sample();
        tree.RemoveKeepingGuests(host, lab);
        Assert.Null(tree.Find(host.Id));
        Assert.Equal(["vm-a", "vm-b", "web-01", "Sub"], lab.Children.Select(c => c.Name));
        Assert.Null(vmA.GuestOf);

        var (tree2, _, sub2, host2, vmA2, vmB2, _, _) = Sample();
        tree2.RemoveKeepingGuests(host2, tree2.Root);
        Assert.Equal(["Lab", "other-01", "vm-a", "vm-b"], tree2.Root.Children.Select(c => c.Name));
        Assert.Same(tree2.Root, vmB2.Parent);
    }
}
