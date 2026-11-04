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
        // The places are the defaults, then the tree in order: Servers, then web-01.
        Assert.Equal(2, editor.PlaceBox.SelectedIndex);
        editor.PlaceBox.SelectedIndex = 1;
        Assert.Same(folder, editor.ChosenPlace!.Node);

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
}
