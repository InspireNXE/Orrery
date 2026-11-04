using System.Text.Json;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.Core.Tests.Connections;

/// <summary>One command saved on several places: linked copies, edited and deleted together.</summary>
public class LinkedCommandTests
{
    static (ConnectionTree Tree, ConnectionFolder Prod, ConnectionEntry Web, ConnectionEntry Db) Sample()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Prod" };
        var web = new ConnectionEntry { Name = "web-01", Host = "web-01.example", Commands = [new SavedCommand { Name = "first", Text = "id" }] };
        var db = new ConnectionEntry { Name = "db-01", Host = "db-01.example" };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        tree.Add(db, prod);
        return (tree, prod, web, db);
    }

    [Fact]
    public void Saved_on_several_places_the_copies_share_a_link_and_are_listed_as_one()
    {
        var (tree, _, web, db) = Sample();
        var saved = SavedCommands.Save([], new SavedCommand { Name = "uptime", Text = "uptime" }, [web, db]);

        Assert.Equal(2, saved.Count);
        Assert.NotNull(saved[0].Command.LinkId);
        Assert.Equal(saved[0].Command.LinkId, saved[1].Command.LinkId);
        Assert.NotSame(saved[0].Command, saved[1].Command);
        Assert.Equal(["first", "uptime"], web.Commands!.Select(c => c.Name));

        var all = SavedCommands.All(tree.Root);
        Assert.Equal(2, all.Count);
        Assert.Equal([web, db], all[1].Select(c => c.Owner));
        Assert.Equal([web, db], SavedCommands.Copies(tree.Root, db, db.Commands![0]).Select(c => c.Owner));

        // Each connection still sends its own copy.
        Assert.Contains(SavedCommands.For(db), c => c.Command.Name == "uptime");
    }

    [Fact]
    public void Saving_again_changes_every_copy_in_place_moves_off_places_unticked_and_unlinks_a_single_one()
    {
        var (tree, prod, web, db) = Sample();
        var saved = SavedCommands.Save([], new SavedCommand { Name = "uptime", Text = "uptime" }, [web, db]);
        web.Commands!.Add(new SavedCommand { Name = "last", Text = "w" });

        // Changed on both, and still listed where it was on web-01 (before "last").
        saved = SavedCommands.Save(saved, new SavedCommand { Name = "uptime", Text = "uptime -p", LinkId = saved[0].Command.LinkId }, [web, db]);
        Assert.Equal(["first", "uptime", "last"], web.Commands.Select(c => c.Name));
        Assert.All(saved, c => Assert.Equal("uptime -p", c.Command.Text));

        // Off db-01 and onto the folder.
        saved = SavedCommands.Save(saved, new SavedCommand { Name = "uptime", Text = "uptime -p" }, [web, prod]);
        Assert.Null(db.Commands);
        Assert.Single(prod.Commands!);
        Assert.Equal(saved[0].Command.LinkId, prod.Commands![0].LinkId);

        // On one place only, it isn't linked any more.
        saved = SavedCommands.Save(saved, new SavedCommand { Name = "uptime", Text = "uptime -p" }, [prod]);
        Assert.Null(Assert.Single(saved).Command.LinkId);
        Assert.Equal(["first", "last"], web.Commands.Select(c => c.Name));

        SavedCommands.Delete(saved);
        Assert.Null(prod.Commands);
        Assert.Equal(2, SavedCommands.All(tree.Root).Count);
    }

    [Fact]
    public void The_link_is_kept_in_the_file_and_a_reader_that_does_not_know_it_sees_plain_copies()
    {
        var (tree, _, web, db) = Sample();
        SavedCommands.Save([], new SavedCommand { Name = "uptime", Text = "uptime" }, [web, db]);
        var json = JsonSerializer.Serialize(tree.Root, ConnectionStore.JsonOptions);
        Assert.Contains("\"linkId\"", json);

        var back = JsonSerializer.Deserialize<ConnectionFolder>(json, ConnectionStore.JsonOptions)!;
        var copies = back.Descendants().SelectMany(n => n.Commands ?? []).Where(c => c.Name == "uptime").ToList();
        Assert.Equal(2, copies.Count);
        Assert.Equal(copies[0].LinkId, copies[1].LinkId);

        // A link alone needs no newer schema: an older Orrery ignores the field and keeps the copies.
        Assert.Equal(4, ConnectionStore.SchemaFor(tree.Root));
    }
}
