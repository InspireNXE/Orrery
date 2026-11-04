using System.Text;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Export;
using Orrery.Core.Import;
using Xunit;

namespace Orrery.Core.Tests.Connections;

public class SavedCommandTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    static SavedCommand Command(string name, string text, bool confirm = false) => new() { Name = name, Text = text, Confirm = confirm };

    static (ConnectionTree Tree, ConnectionFolder Prod, ConnectionEntry Web) Sample()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Prod", Commands = [Command("Tail log", "tail -f /var/log/app.log"), Command("Uptime", "uptime")] };
        var web = new ConnectionEntry { Name = "web-01", Host = "web-01.example", Settings = { Protocol = Protocol.Ssh, Username = "demo" } };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        return (tree, prod, web);
    }

    [Fact]
    public void A_connection_gets_its_own_commands_then_its_folders_and_the_nearest_name_wins()
    {
        var (tree, _, web) = Sample();
        tree.Root.Commands = [Command("Disk", "df -h")];
        web.Commands = [Command("tail LOG", "journalctl -fu web")];

        var commands = SavedCommands.For(web);

        Assert.Equal(["tail LOG", "Uptime", "Disk"], commands.Select(c => c.Command.Name));
        Assert.Equal("journalctl -fu web", commands[0].Command.Text);
        Assert.Same(web, commands[0].From);
    }

    [Fact]
    public void A_terminal_without_a_saved_connection_gets_the_defaults_commands_only()
    {
        var (tree, _, _) = Sample();
        tree.Root.Commands = [Command("Disk", "df -h"), Command("Wipe", "rm -rf ./build")];

        var commands = SavedCommands.ForDefaults(tree.Root);
        Assert.Equal(["Disk", "Wipe"], commands.Select(c => c.Command.Name));
        Assert.All(commands, c => Assert.False(c.Confirm));

        tree.Root.Settings.ConfirmCommands = true;
        Assert.All(SavedCommands.ForDefaults(tree.Root), c => Assert.True(c.Confirm));
    }

    [Fact]
    public void Placeholders_fill_in_from_the_address_a_terminal_was_opened_to()
    {
        Assert.Equal("ssh-copy-id root@10.0.0.5 # quick", SavedCommands.Expand("ssh-copy-id {username}@{host} # {name}", "10.0.0.5", "root", "quick"));
        Assert.Equal("echo @PC ${host}", SavedCommands.Expand("echo {username}@{host} ${host}", "PC", null, "Local"));
    }

    [Fact]
    public void A_guest_takes_its_folders_commands_not_its_hosts()
    {
        var (tree, prod, web) = Sample();
        web.Commands = [Command("Host only", "virsh list")];
        var guest = new ConnectionEntry { Name = "vm", Host = "vm.example" };
        tree.AddGuest(guest, web);

        var names = SavedCommands.For(guest).Select(c => c.Command.Name).ToList();

        Assert.DoesNotContain("Host only", names);
        Assert.Contains("Uptime", names);
        Assert.Same(prod, SavedCommands.For(guest).First().From);
    }

    [Fact]
    public void Confirm_every_command_on_a_folder_makes_each_one_ask()
    {
        var (_, prod, web) = Sample();
        Assert.All(SavedCommands.For(web), c => Assert.False(c.Confirm));

        prod.Settings.ConfirmCommands = true;
        Assert.All(SavedCommands.For(web), c => Assert.True(c.Confirm));

        // A connection can turn it off for itself, but a command marked to ask still asks.
        web.Settings.ConfirmCommands = false;
        prod.Commands![1].Confirm = true;
        Assert.Equal([false, true], SavedCommands.For(web).Select(c => c.Confirm));
    }

    [Fact]
    public void Placeholders_are_filled_and_a_shells_own_braces_are_left_alone()
    {
        var (_, _, web) = Sample();
        Assert.Equal("ssh demo@web-01.example # web-01 ${host} {a,b} {other}",
            SavedCommands.Expand("ssh {username}@{HOST} # {name} ${host} {a,b} {other}", web, "demo"));
        Assert.Equal("as ", SavedCommands.Expand("as {username}", web, null));
    }

    [Theory]
    [InlineData("rm -rf /srv/www/old", true)]
    [InlineData("sudo systemctl restart nginx", true)]
    [InlineData("DROP TABLE users;", true)]
    [InlineData("git push --force origin main", true)]
    [InlineData("Restart-Computer -Force", true)]
    [InlineData("sudo reboot", true)]
    [InlineData("tail -f /var/log/nginx/error.log", false)]
    [InlineData("systemctl status nginx", false)]
    [InlineData("ls -la ~/rmdir-notes", false)]
    public void Looks_destructive_flags_commands_that_delete_stop_or_overwrite(string text, bool expected) =>
        Assert.Equal(expected, SavedCommands.LooksDestructive(text));

    [Fact]
    public void A_tree_with_commands_or_confirm_every_command_is_written_with_schema_4_and_one_without_stays_older()
    {
        var (tree, prod, _) = Sample();
        Assert.Equal(4, ConnectionStore.SchemaFor(tree.Root));

        prod.Commands = null;
        Assert.True(ConnectionStore.SchemaFor(tree.Root) < 4);

        prod.Settings.ConfirmCommands = true;
        Assert.Equal(4, ConnectionStore.SchemaFor(tree.Root));
    }

    [Fact]
    public void A_command_sent_when_connected_needs_schema_5_so_a_build_that_would_drop_it_refuses_the_file()
    {
        var (tree, prod, _) = Sample();
        prod.Commands![1].WhenConnected = true;
        Assert.Equal(5, ConnectionStore.SchemaFor(tree.Root));

        prod.Commands[1].WhenConnected = false;
        Assert.Equal(4, ConnectionStore.SchemaFor(tree.Root));

        prod.Commands[1].Password = CommandPassword.Login;
        Assert.Equal(5, ConnectionStore.SchemaFor(tree.Root));
    }

    [Fact]
    public void When_connected_the_defaults_go_first_then_each_folder_inwards_then_the_connections_own()
    {
        var (tree, prod, web) = Sample();
        tree.Root.Commands = [new SavedCommand { Name = "Prompt", Text = "export PS1='> '", WhenConnected = true }];
        prod.Commands![1].WhenConnected = true;
        web.Commands =
        [
            new SavedCommand { Name = "Into app", Text = "cd /srv/app", WhenConnected = true },
            new SavedCommand { Name = "By hand", Text = "htop" },
            new SavedCommand { Name = "Attach", Text = "tmux attach", WhenConnected = true },
        ];

        Assert.Equal(["Prompt", "Uptime", "Into app", "Attach"], SavedCommands.WhenConnected(web).Select(c => c.Command.Name));

        // A nearer command of the same name hides the folder's, and isn't sent unless it says so itself.
        web.Commands.Add(new SavedCommand { Name = "uptime", Text = "uptime -p" });
        Assert.Equal(["Prompt", "Into app", "Attach"], SavedCommands.WhenConnected(web).Select(c => c.Command.Name));
    }

    [Fact]
    public void Commands_survive_saving_and_loading()
    {
        var (tree, prod, _) = Sample();
        prod.Commands![0].Confirm = true;
        prod.Commands[0].PressEnter = false;
        prod.Commands[0].WhenConnected = true;
        var root = Guid.NewGuid();
        prod.Commands[1].Password = CommandPassword.Credential;
        prod.Commands[1].PasswordCredentialId = root;
        var folder = Path.Combine(Path.GetTempPath(), "orrery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ConnectionStore(Path.Combine(folder, "connections.json"), Path.Combine(folder, "backups"));
            store.Save(tree);
            var loaded = store.Load().Root.Descendants().OfType<ConnectionFolder>().Single(f => f.Name == "Prod");

            var command = loaded.Commands![0];
            Assert.Equal(("Tail log", "tail -f /var/log/app.log", false, true, true), (command.Name, command.Text, command.PressEnter, command.Confirm, command.WhenConnected));
            Assert.Equal((CommandPassword.Credential, root), (loaded.Commands[1].Password, loaded.Commands[1].PasswordCredentialId));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Copying_a_folder_copies_its_commands_without_sharing_them()
    {
        var (tree, prod, _) = Sample();
        var copy = (ConnectionFolder)tree.Duplicate(prod);

        copy.Commands![0].Text = "changed";
        Assert.Equal("tail -f /var/log/app.log", prod.Commands![0].Text);
    }

    [Fact]
    public void An_orrery_export_carries_commands_and_the_import_keeps_only_usable_ones()
    {
        var (tree, prod, _) = Sample();
        prod.Commands!.Add(Command("Bad", "echo \u001b[31m"));

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, new Dictionary<Guid, CredentialSummary>(), Now);
        var result = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "export.json");

        var imported = ImportReview.Commands(result.Root);
        Assert.Equal(["Tail log", "Uptime"], imported.Select(c => c.Command.Name));
        Assert.Contains(result.Report.Warnings, w => w.Contains("\"Bad\""));

        ImportReview.DropCommands(result.Root);
        Assert.Empty(ImportReview.Commands(result.Root));
    }

    [Fact]
    public void An_imported_command_never_runs_when_connected_until_turned_on_here()
    {
        var (tree, prod, _) = Sample();
        prod.Commands![0].WhenConnected = true;

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, new Dictionary<Guid, CredentialSummary>(), Now);
        var result = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "export.json");

        var tail = ImportReview.Commands(result.Root).Single(c => c.Command.Name == "Tail log").Command;
        Assert.False(tail.WhenConnected);
        Assert.Contains(result.Report.Warnings, w => w.Contains("\"Tail log\"") && w.Contains("when connected"));
    }

    [Fact]
    public void An_imported_command_never_types_a_password_until_set_here()
    {
        var (tree, prod, _) = Sample();
        prod.Commands![1].Password = CommandPassword.Credential;
        prod.Commands[1].PasswordCredentialId = Guid.NewGuid();

        var export = ConnectionExporter.Export(tree.Root, ExportFormat.Orrery, new Dictionary<Guid, CredentialSummary>(), Now);
        var result = OrreryImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(export.Content)), "export.json");

        var uptime = ImportReview.Commands(result.Root).Single(c => c.Command.Name == "Uptime").Command;
        Assert.Equal((CommandPassword.None, (Guid?)null), (uptime.Password, uptime.PasswordCredentialId));
        Assert.False(uptime.AnswersPassword);
        Assert.Contains(result.Report.Warnings, w => w.Contains("\"Uptime\"") && w.Contains("password"));
    }

    [Fact]
    public void Only_a_saved_connection_gets_a_password_and_a_chosen_one_never_from_the_defaults()
    {
        var (tree, prod, web) = Sample();
        var login = new SavedCommand { Name = "Root", Text = "sudo -i", Password = CommandPassword.Login };
        var root = new SavedCommand { Name = "su", Text = "su -", Password = CommandPassword.Credential, PasswordCredentialId = Guid.NewGuid() };

        Assert.Null(SavedCommands.WhyNoPassword(new ResolvedCommand(login, tree.Root, false), web));
        Assert.Null(SavedCommands.WhyNoPassword(new ResolvedCommand(root, prod, false), web));
        Assert.Contains("saved connection", SavedCommands.WhyNoPassword(new ResolvedCommand(login, tree.Root, false), null));
        Assert.Contains("saved connection", SavedCommands.WhyNoPassword(new ResolvedCommand(root, prod, false), null));
        Assert.Contains("defaults", SavedCommands.WhyNoPassword(new ResolvedCommand(root, tree.Root, false), web));

        Assert.Null(SavedCommands.PasswordReach(web));
        Assert.Equal("every connection in Prod", SavedCommands.PasswordReach(prod));
        Assert.Equal("every saved connection", SavedCommands.PasswordReach(tree.Root));
    }

    [Fact]
    public void The_login_password_goes_only_to_that_login_and_a_prompt_asking_for_it()
    {
        Assert.Null(SavedCommands.WhyNotLogin("riley", "riley", "riley"));
        Assert.Null(SavedCommands.WhyNotLogin("riley", "riley", null));
        Assert.Null(SavedCommands.WhyNotLogin(null, "riley", "riley"));
        Assert.Contains("logged in as deploy", SavedCommands.WhyNotLogin("riley", "deploy", null));
        Assert.Contains("root's password", SavedCommands.WhyNotLogin("riley", "riley", "root"));
        Assert.Contains("root's password", SavedCommands.WhyNotLogin("riley", null, "root"));
    }

    [Fact]
    public void A_folder_with_a_telnet_or_serial_connection_reaches_an_unencrypted_line()
    {
        var (tree, prod, web) = Sample();
        Assert.False(SavedCommands.ReachesUnencrypted(prod));

        var console = new ConnectionEntry { Name = "switch", Host = "COM3", Settings = { Protocol = Protocol.Serial } };
        tree.Add(console, prod);
        Assert.True(SavedCommands.ReachesUnencrypted(prod));
        Assert.True(SavedCommands.ReachesUnencrypted(console));
        Assert.False(SavedCommands.ReachesUnencrypted(web));
    }

    [Fact]
    public void Only_a_command_that_presses_enter_answers_a_password_prompt()
    {
        Assert.True(new SavedCommand { Name = "Root", Text = "sudo -i", Password = CommandPassword.Login }.AnswersPassword);
        Assert.False(new SavedCommand { Name = "Root", Text = "sudo -i", Password = CommandPassword.Login, PressEnter = false }.AnswersPassword);

        // A credential that's to be chosen but isn't answers nothing.
        Assert.False(new SavedCommand { Name = "Root", Text = "su -", Password = CommandPassword.Credential }.AnswersPassword);
    }

    [Fact]
    public void An_mremoteng_export_says_commands_are_lost()
    {
        var (tree, _, _) = Sample();
        var export = ConnectionExporter.Export(tree.Root, ExportFormat.MRemoteNg, new Dictionary<Guid, CredentialSummary>(), Now);
        Assert.Contains(export.Warnings, w => w.Contains("saved commands"));
    }
}
