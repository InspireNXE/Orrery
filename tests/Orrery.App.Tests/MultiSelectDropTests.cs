using Orrery.App.Connections;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Several selected rows dragged together in the connection list.</summary>
public class MultiSelectDropTests
{
    // Lab / hv-01 (guests vm-a, vm-b), web-01, Sub; then top-level a, b, c
    static (ConnectionTree Tree, ConnectionFolder Lab, ConnectionFolder Sub, ConnectionEntry Host, ConnectionEntry VmA, ConnectionEntry VmB, ConnectionEntry Web, ConnectionEntry[] Top) Sample()
    {
        var tree = ConnectionTree.Empty();
        var lab = new ConnectionFolder { Name = "Lab" };
        var sub = new ConnectionFolder { Name = "Sub" };
        var host = new ConnectionEntry { Name = "hv-01", Host = "hv-01.lab" };
        var vmA = new ConnectionEntry { Name = "vm-a", Host = "vm-a.lab" };
        var vmB = new ConnectionEntry { Name = "vm-b", Host = "vm-b.lab" };
        var web = new ConnectionEntry { Name = "web-01", Host = "web-01.lab" };
        var top = new[] { "a", "b", "c" }.Select(n => new ConnectionEntry { Name = n, Host = n + ".example" }).ToArray();
        tree.Add(lab, tree.Root);
        tree.Add(host, lab);
        tree.Add(web, lab);
        tree.Add(sub, lab);
        foreach (var entry in top)
        {
            tree.Add(entry, tree.Root);
        }

        tree.AddGuest(vmA, host);
        tree.AddGuest(vmB, host);
        return (tree, lab, sub, host, vmA, vmB, web, top);
    }

    static string Names(IEnumerable<ConnectionNode> nodes) => string.Join(",", nodes.Select(n => n.Name));

    [Fact]
    public void Normalize_keeps_list_order_and_drops_rows_that_already_move_with_a_selected_folder_or_host()
    {
        var (tree, lab, sub, host, vmA, _, web, top) = Sample();

        Assert.Equal("web-01,b", Names(TreeDrop.Normalize(tree.Root, [top[1], web])));
        Assert.Equal("Lab,a", Names(TreeDrop.Normalize(tree.Root, [top[0], sub, web, lab])));
        Assert.Equal("hv-01", Names(TreeDrop.Normalize(tree.Root, [vmA, host])));
    }

    [Fact]
    public void Several_rows_go_in_as_guests_only_when_every_one_can_be_a_guest()
    {
        var (tree, _, sub, host, _, _, web, top) = Sample();

        Assert.Same(web, TreeDrop.Plan(tree.Root, [top[0], top[1]], web, 0.5)!.Host);
        Assert.Null(TreeDrop.Plan(tree.Root, [top[0], sub], web, 0.5)!.Host);
        Assert.Null(TreeDrop.Plan(tree.Root, [top[0], host], web, 0.5)!.Host);
        Assert.Null(TreeDrop.Plan(tree.Root, [top[0], sub], host.Guests.First(), 0.5));
    }

    [Fact]
    public void A_drop_on_one_of_the_dragged_rows_or_inside_a_dragged_folder_is_refused()
    {
        var (tree, lab, sub, _, _, _, web, top) = Sample();

        Assert.Null(TreeDrop.Plan(tree.Root, [top[0], web], web, 0.2));
        Assert.Null(TreeDrop.Plan(tree.Root, [top[0], lab], sub, 0.5));
    }

    [Fact]
    public void Apply_moves_the_rows_together_in_order_after_the_target()
    {
        var (tree, _, _, _, _, _, _, top) = Sample();

        var nodes = TreeDrop.Normalize(tree.Root, [top[0], top[1]]);
        var plan = TreeDrop.Plan(tree.Root, nodes, top[2], 0.9)!;
        TreeDrop.Apply(tree, nodes, plan);

        Assert.Equal("Lab,c,a,b", Names(tree.Root.Children));
    }

    [Fact]
    public void Apply_moves_rows_from_different_places_into_a_folder()
    {
        var (tree, lab, sub, _, vmA, _, web, top) = Sample();

        var nodes = TreeDrop.Normalize(tree.Root, [top[2], vmA, web]);
        var plan = TreeDrop.Plan(tree.Root, nodes, sub, 0.5)!;
        TreeDrop.Apply(tree, nodes, plan);

        Assert.Equal("vm-a,web-01,c", Names(sub.Children));
        Assert.Null(vmA.GuestOf);
        Assert.Equal("hv-01,Sub", Names(lab.Children));
    }

    [Fact]
    public void Apply_makes_the_rows_guests_of_a_host_in_order()
    {
        var (tree, _, _, host, _, vmB, _, top) = Sample();

        var nodes = TreeDrop.Normalize(tree.Root, [top[0], top[1]]);
        var plan = TreeDrop.Plan(tree.Root, nodes, vmB, 0.2)!;
        TreeDrop.Apply(tree, nodes, plan);

        Assert.Equal("vm-a,a,b,vm-b", Names(host.Guests));
        Assert.Equal("Lab,c", Names(tree.Root.Children));
    }
}
