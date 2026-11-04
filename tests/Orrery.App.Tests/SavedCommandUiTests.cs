using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Orrery.App.Connections;
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
    public async Task A_new_command_from_a_terminal_can_go_on_the_connection_a_folder_or_every_connection()
    {
        var tree = ConnectionTree.Empty();
        var prod = new ConnectionFolder { Name = "Production" };
        var web = new ConnectionEntry { Name = "web-01", Host = "web-01.example" };
        tree.Add(prod, tree.Root);
        tree.Add(web, prod);
        var owner = new Window();
        owner.Show();
        var window = new SavedCommandWindow(null,
            [new CommandPlace("web-01 only", web), new CommandPlace("Everything in Production", prod), new CommandPlace("Every connection", tree.Root)]);
        var result = window.ShowDialog<SavedCommand?>(owner);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(web, window.ChosenPlace?.Node);
        Assert.Equal(3, window.PlaceBox.ItemCount);
        window.PlaceBox.SelectedIndex = 2;
        window.TextBox.Text = "uptime";
        window.Save();

        Assert.Equal("uptime", (await result)?.Text);
        Assert.Same(tree.Root, window.ChosenPlace?.Node);
        owner.Close();
    }

    [AvaloniaFact]
    public void With_only_one_place_the_editor_just_names_it()
    {
        var tree = ConnectionTree.Empty();
        var window = new SavedCommandWindow(null, [new CommandPlace("Every connection", tree.Root)]);

        Assert.Same(tree.Root, window.ChosenPlace?.Node);
        Assert.Contains(window.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "On Every connection");
        Assert.DoesNotContain(window.PlaceBox, window.GetLogicalDescendants());
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
}
