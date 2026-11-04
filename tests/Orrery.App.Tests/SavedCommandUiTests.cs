using System.Text;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
using Orrery.App.Controls;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Credentials;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class SavedCommandUiTests
{
    sealed class RecordingSession : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived;
        public readonly StringBuilder Sent = new();
        public void Output(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        public void Send(ReadOnlySpan<byte> data) => Sent.Append(Encoding.UTF8.GetString(data));
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class NoCredentials : ICredentialStore
    {
        public bool IsLocked => false;
        public bool Exists => true;
        public Task UnlockAsync(string masterPassword) => Task.CompletedTask;
        public void Lock() { }
        public Task<IReadOnlyList<CredentialSummary>> ListAsync() => Task.FromResult<IReadOnlyList<CredentialSummary>>([]);
        public Task<Credential?> GetAsync(Guid id) => Task.FromResult<Credential?>(null);
        public Task SaveAsync(Credential credential) => Task.CompletedTask;
        public Task SaveAllAsync(IReadOnlyList<Credential> credentials) => Task.CompletedTask;
        public Task DeleteAsync(Guid id) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public void A_command_is_pasted_as_one_and_enter_pressed_after_it()
    {
        var session = new RecordingSession();
        var view = new TerminalView(session);

        view.SendCommand("cd /srv\necho \u001b[2Jdone", pressEnter: true);
        Assert.Equal("cd /srv\recho [2Jdone\r", session.Sent.ToString());

        // With bracketed paste on, the lines arrive inside the brackets, and Enter comes after them.
        session.Sent.Clear();
        session.Output("\u001b[?2004h");
        view.SendCommand("one\ntwo", pressEnter: false);
        Assert.Equal("\u001b[200~one\rtwo\u001b[201~", session.Sent.ToString());
    }

    [AvaloniaFact]
    public void The_editor_ticks_ask_first_for_a_destructive_command_unless_it_was_set_by_hand()
    {
        var window = new SavedCommandWindow(null, "web-01");
        window.Show();
        window.TextBox.Text = "sudo systemctl restart nginx";
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.ConfirmBox.IsChecked);

        window.ConfirmBox.IsChecked = false;
        window.TextBox.Text = "sudo systemctl restart nginx && rm -rf /tmp/cache";
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.ConfirmBox.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_editor_names_a_command_from_its_first_line_and_wont_save_an_empty_one()
    {
        var owner = new Window();
        owner.Show();
        var window = new SavedCommandWindow(null, "web-01");
        var result = window.ShowDialog<SavedCommand?>(owner);
        Dispatcher.UIThread.RunJobs();

        window.Save();
        Assert.True(window.IsVisible);

        window.TextBox.Text = "uptime\nfree -h";
        window.PressEnterBox.IsChecked = false;
        window.Save();
        var saved = await result;

        Assert.NotNull(saved);
        Assert.Equal(("uptime", "uptime\nfree -h", false, false), (saved!.Name, saved.Text, saved.PressEnter, saved.Confirm));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task Become_root_fills_in_sudo_answered_with_the_login_password()
    {
        var owner = new Window();
        owner.Show();
        var window = new SavedCommandWindow(null, "web-01", new NoCredentials());
        var result = window.ShowDialog<SavedCommand?>(owner);
        Dispatcher.UIThread.RunJobs();
        window.PressEnterBox.IsChecked = false;

        window.BecomeRoot();
        window.Save();
        var saved = await result;

        Assert.NotNull(saved);
        Assert.Equal(("Become root", "sudo -i", true, CommandPassword.Login), (saved!.Name, saved.Text, saved.PressEnter, saved.Password));
        owner.Close();
    }

    [AvaloniaFact]
    public async Task A_command_without_enter_answers_no_password_and_a_credential_has_to_be_chosen()
    {
        var owner = new Window();
        owner.Show();
        var window = new SavedCommandWindow(null, "web-01", new NoCredentials());
        var result = window.ShowDialog<SavedCommand?>(owner);
        Dispatcher.UIThread.RunJobs();
        window.TextBox.Text = "su -";

        window.PasswordChoice = CommandPassword.Credential;
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.PasswordCredentialPicker!.IsVisible);
        window.Save();
        Assert.True(window.IsVisible);

        window.PressEnterBox.IsChecked = false;
        Assert.False(window.PasswordBox.IsEnabled);
        window.Save();
        var saved = await result;

        Assert.Equal((CommandPassword.None, (Guid?)null), (saved!.Password, saved.PasswordCredentialId));
        owner.Close();
    }

    [AvaloniaFact]
    public void Without_the_vault_the_editor_offers_only_the_login_password()
    {
        var window = new SavedCommandWindow(new SavedCommand { Name = "Root", Text = "su -", Password = CommandPassword.Credential, PasswordCredentialId = Guid.NewGuid() }, "web-01");

        Assert.Equal(2, window.PasswordBox.ItemCount);
        Assert.Null(window.PasswordCredentialPicker);
        Assert.Equal(CommandPassword.None, window.PasswordChoice);
    }

    [AvaloniaFact]
    public async Task A_new_command_from_a_terminal_can_go_on_the_connection_a_folder_or_every_connection()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Production" };
        var web = new ConnectionEntry { Name = "web-01", Host = "web-01.example" };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        var owner = new Window();
        owner.Show();
        var window = new SavedCommandWindow(null, tree, [web]);
        var result = window.ShowDialog<SavedCommand?>(owner);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([web], window.ChosenPlaces);
        var picker = window.PlacePicker!;
        picker.Choose(tree.Root, true);
        window.TextBox.Text = "uptime";
        window.Save();

        // The defaults cover web-01, so it isn't saved on it again.
        Assert.Equal("uptime", (await result)?.Text);
        Assert.Equal([tree.Root], window.ChosenPlaces);
        owner.Close();
    }

    [AvaloniaFact]
    public void With_no_connections_the_editor_just_names_the_defaults()
    {
        var tree = ConnectionTree.Empty();
        var window = new SavedCommandWindow(null, tree, [tree.Root]);

        Assert.Equal([tree.Root], window.ChosenPlaces);
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "On the defaults for all connections");
        Assert.Null(window.PlacePicker);
    }

    [AvaloniaFact]
    public void Several_places_can_be_ticked_and_a_folder_covers_what_is_in_it_but_a_host_not_its_guests()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Production" };
        var web = new ConnectionEntry { Name = "web-01", Host = "web-01.example" };
        var db = new ConnectionEntry { Name = "db-01", Host = "db-01.example" };
        var vm = new ConnectionEntry { Name = "vm-01", Host = "vm-01.example" };
        var other = new ConnectionEntry { Name = "other", Host = "other.example" };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        tree.Add(db, prod);
        tree.AddGuest(vm, web);
        tree.Add(other, tree.Root);
        var window = new SavedCommandWindow(null, tree, [web]);
        window.Show();
        var picker = window.PlacePicker!;

        // A guest is listed under its host, and ticking the host doesn't tick it: it's another machine.
        Assert.Contains("guest of web-01", AutomationProperties.GetName(picker.BoxFor(vm)!));
        Assert.True(picker.BoxFor(vm)!.IsEnabled);
        Assert.False(picker.BoxFor(vm)!.IsChecked);
        picker.Choose(vm, true);
        picker.Choose(other, true);
        Assert.Equal([web, vm, other], window.ChosenPlaces);
        Assert.Equal("It reaches 3 saved connections.", picker.ReachText.Text);

        // The folder covers everything in it, guests too: those show ticked, can't be changed, and aren't saved again.
        picker.Choose(prod, true);
        Assert.Equal([prod, other], window.ChosenPlaces);
        Assert.False(picker.BoxFor(db)!.IsEnabled);
        Assert.True(picker.BoxFor(db)!.IsChecked);
        Assert.False(picker.BoxFor(vm)!.IsEnabled);

        // Unticked, what was ticked before it comes back.
        picker.Choose(prod, false);
        Assert.Equal([web, vm, other], window.ChosenPlaces);
        Assert.False(picker.BoxFor(db)!.IsChecked);

        // Nothing ticked: it says so and doesn't save.
        foreach (var node in new ConnectionNode[] { web, vm, other })
        {
            picker.Choose(node, false);
        }

        window.TextBox.Text = "uptime";
        window.Save();
        Assert.True(window.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_prompt_shows_the_command_and_target_and_enter_cancels()
    {
        var owner = new Window();
        owner.Show();
        var prompt = new ConfirmCommandWindow("Restart nginx", "sudo systemctl restart nginx", "web-01", "web-01.prod.example · Production", pressEnter: true);
        var answer = prompt.ShowDialog<bool>(owner);
        Dispatcher.UIThread.RunJobs();

        var texts = prompt.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("web-01", texts);
        Assert.Contains("sudo systemctl restart nginx", texts);
        Assert.Contains("web-01.prod.example · Production", texts);

        var cancel = prompt.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel");
        Assert.True(cancel.IsDefault);
        Assert.True(cancel.IsFocused);
        prompt.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(await answer);
        owner.Close();
    }

    [AvaloniaFact]
    public void The_prompt_says_which_password_a_command_types()
    {
        var prompt = new ConfirmCommandWindow("Become root", "sudo -i", "web-01", null, pressEnter: true,
            password: "If it asks for a password, Orrery types web-01's login password.");
        prompt.Show();

        Assert.Contains(prompt.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "If it asks for a password, Orrery types web-01's login password.");
        Assert.EndsWith("web-01's login password.", AutomationProperties.GetHelpText(prompt));
        prompt.Close();
    }

    [AvaloniaFact]
    public async Task A_chosen_credentials_password_warns_on_a_folder_and_isnt_saved_on_the_defaults()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Production" };
        var web = new ConnectionEntry { Name = "web-01", Host = "web-01.example" };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        var owner = new Window();
        owner.Show();
        var window = new SavedCommandWindow(null, tree, [web], new NoCredentials());
        var result = window.ShowDialog<SavedCommand?>(owner);
        Dispatcher.UIThread.RunJobs();
        window.TextBox.Text = "su -";
        window.PasswordChoice = CommandPassword.Credential;
        Dispatcher.UIThread.RunJobs();

        string? Warning() => window.GetVisualDescendants().OfType<Notice>()
            .FirstOrDefault(n => n.IsEffectivelyVisible && (n.Text?.Contains("broken into") == true || n.Text == SavedCommandWindow.NotFromDefaults))?.Text;
        bool Shows(string text) => window.GetVisualDescendants().OfType<Notice>().Any(n => n.IsEffectivelyVisible && n.Text == text);

        // The risk is always said, and the safer way; on the connection alone, nothing about reach.
        Assert.True(Shows(SavedCommandWindow.PasswordRisk));
        Assert.True(Shows(SavedCommandWindow.Safer));
        Assert.Null(Warning());
        window.PlacePicker!.Choose(web, false);
        window.PlacePicker.Choose(prod, true);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("every connection in it gets this password", Warning());

        window.PlacePicker.Choose(tree.Root, true);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SavedCommandWindow.NotFromDefaults, Warning());
        window.Save();
        Assert.True(window.IsVisible);

        // The login password is each connection's own, so it can go on the defaults.
        window.PasswordChoice = CommandPassword.Login;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(Warning());
        Assert.True(Shows(SavedCommandWindow.PasswordRisk));
        window.Save();
        Assert.Equal(CommandPassword.Login, (await result)?.Password);
        owner.Close();
    }

    [AvaloniaFact]
    public void Typing_a_password_over_telnet_or_serial_says_its_unencrypted()
    {
        var tree = ConnectionTree.Empty();
        var console = new ConnectionEntry { Name = "switch", Host = "COM3", Settings = { Protocol = Protocol.Serial } };
        tree.Add(console, tree.Root);
        var window = new SavedCommandWindow(null, "switch", new NoCredentials(), [console]);
        window.Show();
        bool Plain() => window.GetVisualDescendants().OfType<Notice>().Any(n => n.IsEffectivelyVisible && n.Text == SavedCommandWindow.PlainText);

        Assert.False(Plain());
        window.PasswordChoice = CommandPassword.Login;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Plain());
        window.Close();
    }

    [AvaloniaFact]
    public void Shift_enter_on_a_command_with_another_action_picks_that_action()
    {
        var command = new PaletteCommand("Tail log", "Saved commands · web-01", () => Task.CompletedTask)
        {
            Detail = "tail -f /var/log/app.log",
            AltRun = () => Task.CompletedTask,
            AltHint = "type only",
        };
        var tree = ConnectionTree.Empty();
        var owner = new Window();
        owner.Show();
        var window = new QuickConnectWindow(tree, [], [command]);
        var result = window.ShowOver(owner);
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<TextBox>().Single().Text = ">tail";
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("tail -f /var/log/app.log", texts);
        // The row says what Enter does, as every row does; Shift+Enter's action is with the other keys in the footer.
        Assert.Contains("Run  ↵", texts);
        Assert.True(window.AltHint.IsVisible);
        Assert.Equal("Shift+↵ type only", string.Concat(window.AltHint.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)));
        window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(result.Result!.Alt);
        Assert.Same(command, result.Result.Command);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task The_pane_lists_its_own_commands_then_its_folders_and_confirm_every_command_saves()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Production", Commands = [new SavedCommand { Name = "Uptime", Text = "uptime" }] };
        var web = new ConnectionEntry
        {
            Name = "web-01", Host = "web-01.example", Settings = { Protocol = Protocol.Ssh },
            Commands = [new SavedCommand { Name = "Restart", Text = "sudo systemctl restart web", Confirm = true }],
        };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        var panel = new PropertiesPanel(tree, new NoCredentials());
        var window = new Window { Content = panel, Width = 400, Height = 900 };
        window.Show();

        await panel.ShowAsync(web);
        Dispatcher.UIThread.RunJobs();
        var texts = panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.True(texts.IndexOf("Restart") < texts.IndexOf("Uptime"));
        Assert.Contains("asks first", texts);
        Assert.Contains("from Production", texts);

        await panel.ShowAsync(prod);
        Dispatcher.UIThread.RunJobs();
        panel.GetVisualDescendants().OfType<ToggleSwitch>().Single(s => s.Name == "ConfirmCommands").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.TrySave());
        Assert.True(prod.Settings.ConfirmCommands);
        Assert.All(SavedCommands.For(web), c => Assert.True(c.Confirm));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Own_commands_move_up_and_down_and_inherited_ones_have_no_arrows()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Production", Commands = [new SavedCommand { Name = "Uptime", Text = "uptime" }] };
        var web = new ConnectionEntry
        {
            Name = "web-01", Host = "web-01.example", Settings = { Protocol = Protocol.Ssh },
            Commands = [new SavedCommand { Name = "First", Text = "a" }, new SavedCommand { Name = "Second", Text = "b" }, new SavedCommand { Name = "Third", Text = "c" }],
        };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        int changes = 0;
        tree.Changed += (_, _) => changes++;
        var panel = new PropertiesPanel(tree, new NoCredentials());
        var window = new Window { Content = panel, Width = 400, Height = 900 };
        window.Show();
        await panel.ShowAsync(web);
        Dispatcher.UIThread.RunJobs();

        Button Arrow(string name) => panel.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
        Assert.False(Arrow("Move First up").IsEnabled);
        Assert.False(Arrow("Move Third down").IsEnabled);
        Assert.DoesNotContain(panel.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b)?.StartsWith("Move Uptime") == true);

        Arrow("Move Third up").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["First", "Third", "Second"], web.Commands!.Select(c => c.Name));
        Assert.True(Arrow("Move Third up").IsFocused);

        panel.MoveCommand(web.Commands[0], -1); // Already first: nothing.
        Assert.Equal(["First", "Third", "Second"], web.Commands.Select(c => c.Name));
        Assert.Equal(1, changes);
        window.Close();
    }
}
