using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.Core.Connections;
using Xunit;

namespace Orrery.App.Tests;

public class PaletteCommandTests
{
    static PaletteCommand Command(string title, string? subject = null, params string[] keywords) =>
        new(title, "Test", () => Task.CompletedTask, subject) { Keywords = keywords };

    [Fact]
    public void Every_word_typed_has_to_start_a_word_of_the_title_or_its_keywords()
    {
        var disconnect = Command("Disconnect all sessions (3)", null, "everything");
        Assert.True(disconnect.Score("disc all") > 0);
        Assert.True(disconnect.Score("everything") > 0);
        Assert.Equal(0, disconnect.Score("disc nothing"));
        Assert.Equal(0, disconnect.Score("connect"));
        Assert.Equal(0, disconnect.Score(""));
    }

    [Fact]
    public void A_command_about_a_connection_needs_more_than_its_name()
    {
        var copy = Command("Copy password for web-01", "web-01", "secret");
        // Typing only the name finds the connection itself, not every command about it.
        Assert.Equal(0, copy.Score("web-01"));
        Assert.True(copy.Score("copy web") > 0);
        Assert.True(copy.Score("secret web-01") > 0);
    }

    [Fact]
    public void The_title_starting_with_the_first_word_ranks_higher()
    {
        Assert.True(Command("Settings").Score("set") > Command("Defaults for all connections…", null, "settings").Score("set"));
    }

    static (Task<QuickConnectChoice?> Result, QuickConnectWindow Window) Open(string query, params PaletteCommand[] commands)
    {
        var tree = ConnectionTree.Empty();
        tree.Add(new ConnectionEntry { Name = "web-01", Host = "web-01.corp" }, tree.Root);
        var owner = new Window();
        owner.Show();
        var window = new QuickConnectWindow(tree, [], commands);
        var result = window.ShowOver(owner);
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<TextBox>().Single().Text = query;
        Dispatcher.UIThread.RunJobs();
        return (result, window);
    }

    static List<string?> Texts(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    [AvaloniaFact]
    public void A_matching_command_is_listed_under_commands_and_enter_on_it_returns_it()
    {
        var settings = Command("Settings", null, "preferences");
        var (result, window) = Open("prefer", settings);
        Assert.Contains("COMMANDS", Texts(window));

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(settings, result.Result!.Command);
    }

    [AvaloniaFact]
    public void A_query_starting_with_the_prompt_lists_only_commands_under_their_headings()
    {
        var (_, window) = Open(">", Command("Settings"), new PaletteCommand("Disconnect everything in Lab (2)", "Folders", () => Task.CompletedTask, "Lab"));
        var texts = Texts(window);
        Assert.Contains("TEST", texts);
        Assert.Contains("FOLDERS", texts);
        Assert.DoesNotContain("SAVED CONNECTIONS", texts);
        Assert.Contains("Disconnect everything in Lab (2)", texts);
    }
}

public class SessionMenuCommandTests
{
    [AvaloniaFact]
    public async Task The_session_menu_becomes_commands_submenus_flattened_and_disabled_items_left_out()
    {
        int sent = 0;
        var keys = new MenuItem { Header = "Send keys" };
        var ctrlAltDel = new MenuItem { Header = "Ctrl+Alt+Del" };
        ctrlAltDel.Click += (_, _) => sent++;
        keys.Items.Add(ctrlAltDel);
        var menu = new Control[] { keys, new Separator(), new MenuItem { Header = "Save screenshot…", IsEnabled = false } };

        var commands = menu.SelectMany(c => MainWindow.FromMenu(c, null, "This session")).ToList();

        var only = Assert.Single(commands);
        Assert.Equal("Send keys: Ctrl+Alt+Del", only.Title);
        await only.Run();
        Assert.Equal(1, sent);
    }
}
