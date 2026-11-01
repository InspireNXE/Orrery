using System.Text;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>What a screen reader gets from a terminal: its text and cursor, and its new output read out.</summary>
public class TerminalAnnouncementTests
{
    static readonly DateTime Start = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    static TerminalScreenText Screen(long scrolled, int cursor, params string[] rows) =>
        new(rows, new bool[rows.Length], scrolled, cursor);

    static string? Say(TerminalAnnouncer announcer, TerminalScreenText screen, DateTime at)
    {
        announcer.Update(screen, at);
        return announcer.TakeIfDue(at + TerminalAnnouncer.Quiet);
    }

    [Fact]
    public void Reads_new_output_without_the_echo_of_what_was_typed()
    {
        var announcer = new TerminalAnnouncer();
        announcer.Reset(Screen(0, 0, "$", "", ""));
        announcer.Typed("ls", Start);
        announcer.Typed("\n", Start);

        Assert.Equal("a.txt  b.txt\n$", Say(announcer, Screen(0, 2, "$ ls", "a.txt  b.txt", "$"), Start));
    }

    [Fact]
    public void Echo_that_arrives_a_key_at_a_time_says_nothing()
    {
        var announcer = new TerminalAnnouncer();
        announcer.Reset(Screen(0, 0, "$"));
        announcer.Typed("e", Start);
        announcer.Update(Screen(0, 0, "$ e"), Start);
        announcer.Typed("c", Start);
        announcer.Update(Screen(0, 0, "$ ec"), Start);

        Assert.False(announcer.HasPending);
        Assert.Null(announcer.TakeIfDue(Start + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Output_after_keys_that_were_never_echoed_is_read_and_the_keys_are_forgotten()
    {
        // A password typed at a prompt that doesn't echo it.
        var announcer = new TerminalAnnouncer();
        announcer.Reset(Screen(0, 0, "Password:", ""));
        announcer.Typed("hunter2\n", Start);

        Assert.Equal("Welcome", Say(announcer, Screen(0, 1, "Password:", "Welcome"), Start));
        Assert.Equal("hunter2", Say(announcer, Screen(1, 1, "Welcome", "hunter2"), Start + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Lines_that_scrolled_up_are_not_read_again()
    {
        var announcer = new TerminalAnnouncer();
        announcer.Reset(Screen(0, 2, "one", "two", "three"));

        Assert.Equal("four\nfive", Say(announcer, Screen(2, 2, "three", "four", "five"), Start));
    }

    [Fact]
    public void A_line_that_stays_put_under_a_scrolling_pane_is_not_read()
    {
        var announcer = new TerminalAnnouncer();
        announcer.Reset(Screen(0, 1, "one", "two", "[tmux] 0:bash"));

        Assert.Equal("three", Say(announcer, Screen(1, 1, "two", "three", "[tmux] 0:bash"), Start));
    }

    [Fact]
    public void A_row_that_got_shorter_says_nothing_and_one_that_was_rewritten_says_all_of_itself()
    {
        var announcer = new TerminalAnnouncer();
        announcer.Reset(Screen(0, 0, "$ abc", "Progress 10%"));

        Assert.Null(Say(announcer, Screen(0, 0, "$ ab", "Progress 10%"), Start));
        Assert.Equal("Progress 20%", Say(announcer, Screen(0, 0, "$ ab", "Progress 20%"), Start + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_line_that_wraps_is_read_as_one()
    {
        var announcer = new TerminalAnnouncer();
        announcer.Reset(Screen(0, 0, "", "", ""));

        var screen = new TerminalScreenText(["hello wor", "ld", ""], [true, false, false], 0, 2);
        Assert.Equal("hello world", Say(announcer, screen, Start));
    }

    [Fact]
    public void Output_is_gathered_until_it_pauses_or_has_waited_long_enough()
    {
        var announcer = new TerminalAnnouncer();
        var rows = new string[20];
        Array.Fill(rows, string.Empty);
        announcer.Reset(Screen(0, 0, rows));

        rows = (string[])rows.Clone();
        rows[0] = "first";
        announcer.Update(Screen(0, 1, rows), Start);
        Assert.Null(announcer.TakeIfDue(Start + TimeSpan.FromMilliseconds(200)));

        // Output that keeps coming is still read once it has waited long enough.
        for (int i = 1; i < 16; i++)
        {
            rows = (string[])rows.Clone();
            rows[i] = $"line {i}";
            var at = Start + TimeSpan.FromMilliseconds(100 * i);
            announcer.Update(Screen(0, i + 1, rows), at);
            if (at - Start < TerminalAnnouncer.MaxWait)
            {
                Assert.Null(announcer.TakeIfDue(at));
            }
        }

        var said = announcer.TakeIfDue(Start + TerminalAnnouncer.MaxWait);
        Assert.Equal("Skipped 10 lines.\nline 10\nline 11\nline 12\nline 13\nline 14\nline 15", said);
        Assert.False(announcer.HasPending);
    }

    [Fact]
    public void A_very_long_line_is_cut_short()
    {
        var announcer = new TerminalAnnouncer();
        announcer.Reset(Screen(0, 0, ""));

        var said = Say(announcer, Screen(0, 0, new string('x', 2000)), Start)!;
        Assert.Equal(TerminalAnnouncer.MaxCharacters + 1, said.Length);
        Assert.EndsWith("…", said);
    }

    // ---- The view ----

    sealed class TextSession : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("box.example", 22, "admin", null);
        public SessionState State => SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event Action<byte[]>? DataReceived;
        public void Output(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static (Window Window, TerminalView View, TextSession Session) Show()
    {
        var session = new TextSession();
        var view = new TerminalView(session);
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, session);
    }

    [AvaloniaFact]
    public void Automation_gets_the_screen_text_and_where_the_cursor_is()
    {
        var (window, view, session) = Show();
        session.Output("hello\r\nworld");
        Dispatcher.UIThread.RunJobs();

        var peer = ControlAutomationPeer.CreatePeerForElement(view);
        var value = peer.GetProvider<IValueProvider>()!;
        Assert.Equal(AutomationControlType.Edit, peer.GetAutomationControlType());
        Assert.Equal("Terminal", peer.GetName());
        Assert.True(value.IsReadOnly);
        Assert.Equal("hello\nworld", value.Value);
        Assert.Equal("Line 2, column 6", peer.GetItemStatus());
        Assert.Throws<InvalidOperationException>(() => value.SetValue("rm -rf /"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_session_view_is_named_after_its_connection()
    {
        var session = new TextSession();
        var tab = new SessionTab(session, new TerminalView(session), new SessionOrigin("web01", Protocol.Ssh, null, () => Task.CompletedTask));

        Assert.Equal("Terminal: web01", ControlAutomationPeer.CreatePeerForElement(tab.View).GetName());
    }

    [AvaloniaFact]
    public async Task New_output_is_read_out_but_not_what_was_typed()
    {
        var (window, view, session) = Show();
        session.Output("$ ");
        Dispatcher.UIThread.RunJobs();
        view.Announcements = true;
        view.Focus();

        window.KeyTextInput("ls");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        session.Output("ls\r\nfile1  file2\r\n$ ");

        var until = DateTime.UtcNow.AddSeconds(5);
        while (view.Spoken.Length == 0 && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        Assert.Equal("file1  file2\n$", view.Spoken);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Nothing_is_read_out_while_announcements_are_off()
    {
        var (window, view, session) = Show();
        Assert.False(view.Announcements);
        session.Output("hello\r\n");

        var until = DateTime.UtcNow + TerminalAnnouncer.MaxWait + TimeSpan.FromMilliseconds(300);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        Assert.Equal(string.Empty, view.Spoken);
        window.Close();
    }
}
