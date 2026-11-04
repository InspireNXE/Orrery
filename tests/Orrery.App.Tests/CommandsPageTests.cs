using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>The side panel's Commands page, with its editor in the right-hand pane.</summary>
public class CommandsPageTests
{
    static (ConnectionTree Tree, ConnectionFolder Folder, ConnectionEntry Entry) Tree()
    {
        var tree = ConnectionTree.Empty();
        var folder = new ConnectionFolder { Name = "Servers" };
        var entry = new ConnectionEntry { Name = "web-01", Host = "web-01.example" };
        tree.Add(folder, tree.Root);
        tree.Add(entry, folder);
        tree.Root.Commands = [new SavedCommand { Name = "uptime", Text = "uptime" }];
        entry.Commands = [new SavedCommand { Name = "restart", Text = "sudo systemctl restart nginx", Confirm = true }];
        return (tree, folder, entry);
    }

    static List<CommandAt> Listed(CommandsPanel panel) =>
        panel.Listed.ToList();

    [AvaloniaFact]
    public void Every_command_is_listed_by_where_it_is_saved_and_the_filter_narrows_it()
    {
        var (tree, _, entry) = Tree();
        var panel = new CommandsPanel(tree);
        var window = new Window { Content = panel, Width = 300, Height = 600 };
        window.Show();

        Assert.Equal(["uptime", "restart"], Listed(panel).Select(c => c.Command.Name));
        Assert.Same(entry, Listed(panel)[1].Owner);
        Assert.Contains("asks first", CommandsPanel.Marks(entry.Commands![0]));

        // By name, by text, or by where it's saved.
        panel.FilterBox.Text = "nginx";
        Assert.Equal(["restart"], Listed(panel).Select(c => c.Command.Name));
        panel.FilterBox.Text = "defaults";
        Assert.Equal(["uptime"], Listed(panel).Select(c => c.Command.Name));
        panel.FilterBox.Text = "nothing like it";
        Assert.Empty(Listed(panel));
        window.Close();
    }

    [AvaloniaFact]
    public void Saving_on_another_place_moves_the_command_there()
    {
        var (tree, folder, entry) = Tree();
        var panel = new CommandsPanel(tree);
        var pane = new CommandEditorPane(tree, new PropertiesPanelTests.EmptyCredentialStore(), _ => null);
        var window = new Window { Content = new DockPanel { Children = { panel, pane } }, Width = 900, Height = 700 };
        window.Show();

        var restart = entry.Commands![0];
        pane.Show(new CommandAt(entry, restart));
        Dispatcher.UIThread.RunJobs();
        var editor = pane.Editor!;
        editor.NameBox.Text = "restart web";
        Assert.Equal([entry], editor.ChosenPlaces);
        editor.PlacePicker!.Choose(entry, false);
        editor.PlacePicker.Choose(folder, true);
        Assert.Equal([folder], editor.ChosenPlaces);

        Assert.True(pane.Save());
        Assert.Null(entry.Commands);
        Assert.Equal("restart web", Assert.Single(folder.Commands!).Name);
        Assert.Same(folder, pane.Shown!.Owner);
        Assert.Contains(Listed(panel), c => c.Owner == folder && c.Command.Name == "restart web");
        window.Close();
    }

    [AvaloniaFact]
    public void A_new_command_is_added_on_the_place_it_was_started_for()
    {
        var (tree, _, entry) = Tree();
        var pane = new CommandEditorPane(tree, new PropertiesPanelTests.EmptyCredentialStore(), _ => null);
        var window = new Window { Content = pane, Width = 600, Height = 700 };
        window.Show();

        pane.Show(null, entry);
        Dispatcher.UIThread.RunJobs();
        pane.Editor!.NameBox.Text = "logs";
        pane.Editor.TextBox.Text = "journalctl -f";

        Assert.True(pane.Save());
        Assert.Equal(["restart", "logs"], entry.Commands!.Select(c => c.Name));
        window.Close();
    }

    [AvaloniaFact]
    public void Send_shows_only_when_the_current_terminal_has_the_command()
    {
        var (tree, _, entry) = Tree();
        CommandSendTarget? target = null;
        var pane = new CommandEditorPane(tree, new PropertiesPanelTests.EmptyCredentialStore(), _ => target);
        var window = new Window { Content = pane, Width = 600, Height = 700 };
        window.Show();

        pane.Show(new CommandAt(entry, entry.Commands![0]));
        Dispatcher.UIThread.RunJobs();
        Button Send() => pane.SendButton!;
        Assert.False(Send().IsVisible);

        target = new CommandSendTarget("Send to web-01", () => Task.CompletedTask);
        pane.UpdateSend();
        Assert.True(Send().IsVisible);
        Assert.Equal("Send to web-01", Send().Content);
        window.Close();
    }

    [AvaloniaFact]
    public void Ticking_several_places_saves_linked_copies_listed_once_under_several_places()
    {
        var (tree, folder, entry) = Tree();
        var other = new ConnectionEntry { Name = "db-01", Host = "db-01.example" };
        tree.Add(other, tree.Root);
        var panel = new CommandsPanel(tree);
        var pane = new CommandEditorPane(tree, new PropertiesPanelTests.EmptyCredentialStore(), _ => null);
        panel.Editor = pane;
        var window = new Window { Content = new DockPanel { Children = { panel, pane } }, Width = 900, Height = 700 };
        window.Show();

        pane.Show(new CommandAt(entry, entry.Commands![0]));
        Dispatcher.UIThread.RunJobs();
        pane.Editor!.PlacePicker!.Choose(other, true);
        Assert.True(pane.Save());

        Assert.Single(entry.Commands);
        Assert.Single(other.Commands!);
        Assert.Equal(entry.Commands[0].LinkId, other.Commands![0].LinkId);
        var linked = Assert.Single(panel.Listed, c => c.Copies.Count == 2);
        Assert.Equal([entry, other], linked.Owners);

        // Edited once, both change.
        pane.Editor!.TextBox.Text = "sudo systemctl reload nginx";
        Assert.True(pane.Save());
        Assert.Equal("sudo systemctl reload nginx", other.Commands![0].Text);

        // A linked command has no one order to move in, and Delete says it takes every copy.
        panel.ShowMenuFor(panel.Listed.Single(c => c.Copies.Count == 2));
        Assert.False(panel.UpItem.IsVisible);
        Assert.Equal("Delete from all 2 places…", panel.DeleteItem.Header);
        Assert.Equal("New command…", panel.NewItem.Header);
        window.Close();
    }

    [AvaloniaFact]
    public void The_menu_on_a_command_offers_what_fits_it_and_on_a_group_a_new_command_there()
    {
        var (tree, folder, entry) = Tree();
        entry.Commands!.Add(new SavedCommand { Name = "logs", Text = "journalctl -f" });
        var panel = new CommandsPanel(tree);
        var pane = new CommandEditorPane(tree, new PropertiesPanelTests.EmptyCredentialStore(),
            at => at.Command.Name == "logs" ? new CommandSendTarget("Send to web-01", () => Task.CompletedTask) : null);
        panel.Editor = pane;
        var window = new Window { Content = new DockPanel { Children = { panel, pane } }, Width = 900, Height = 700 };
        window.Show();
        var restart = panel.Listed.Single(c => c.Command.Name == "restart");
        var logs = panel.Listed.Single(c => c.Command.Name == "logs");

        panel.ShowMenuFor(restart);
        Assert.False(panel.SendItem.IsVisible);
        Assert.False(panel.UpItem.IsEnabled);
        Assert.True(panel.DownItem.IsEnabled);
        Assert.Equal("New command on web-01…", panel.NewItem.Header);

        panel.ShowMenuFor(logs);
        Assert.True(panel.SendItem.IsVisible);
        Assert.Equal("Send to web-01", panel.SendItem.Header);
        Assert.False(panel.DownItem.IsEnabled);

        // On a group's header, or the space under the list.
        panel.ShowMenuFor(folder);
        Assert.False(panel.DeleteItem.IsVisible);
        Assert.Equal("New command on Servers…", panel.NewItem.Header);
        panel.ShowMenuFor(tree.Root);
        Assert.Equal("New command on the defaults…", panel.NewItem.Header);
        panel.ShowMenuFor(null);
        Assert.Equal("New command…", panel.NewItem.Header);

        ConnectionNode? asked = null;
        panel.NewRequested += (_, place) => asked = place;
        panel.ShowMenuFor(folder);
        panel.NewItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Same(folder, asked);

        // Move and Duplicate.
        pane.Move(logs, -1);
        Assert.Equal(["logs", "restart"], entry.Commands.Select(c => c.Name));
        pane.Duplicate(restart);
        Assert.Equal(["logs", "restart", "restart copy"], entry.Commands.Select(c => c.Name));
        Assert.Equal("restart copy", pane.Shown!.Command.Name);
        window.Close();
    }
}
