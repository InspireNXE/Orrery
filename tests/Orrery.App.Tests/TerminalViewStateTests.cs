using System.Text;
using Avalonia.Headless.XUnit;
using Orrery.App.Sessions;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class TerminalViewStateTests
{
    sealed class FakeSession : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State { get; private set; } = SessionState.Connected;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event Action<byte[]>? DataReceived;
        public void Output(string text) => DataReceived?.Invoke(Encoding.UTF8.GetBytes(text));
        public void Change(SessionState state, string? message)
        {
            State = state;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
        }

        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public void A_failure_message_cannot_send_escape_sequences()
    {
        var session = new FakeSession();
        var view = new TerminalView(session);
        session.Output("before\r\n");

        session.Change(SessionState.Failed, "x\u001b[2Jy\u009b");

        var text = view.AllText();
        Assert.Contains("before", text);                         // ESC[2J would have cleared it.
        Assert.Contains("Connection failed: x?[2Jy?", text);
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\u009b', text);
    }

    sealed class FakeLocalSession(string name) : ITerminalSession, ILocalSession
    {
        public ConnectionTarget Target { get; } = new("pc", 0, "user", null);
        public SessionState State { get; private set; } = SessionState.Connecting;
        public string ProgramName => name;
        public int? ExitCode { get; set; }
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event Action<byte[]>? DataReceived { add { } remove { } }
        public void Change(SessionState state, string? message = null)
        {
            State = state;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
        }

        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [AvaloniaFact]
    public void A_local_shell_says_it_is_starting_and_how_it_ended()
    {
        var session = new FakeLocalSession("PowerShell 7");
        var view = new TerminalView(session);
        Assert.Contains("Starting PowerShell 7…", view.AllText());
        Assert.DoesNotContain("pc:0", view.AllText());

        session.ExitCode = 3;
        session.Change(SessionState.Connected);
        session.Change(SessionState.Disconnected, "The shell exited with code 3.");

        Assert.Contains("[Exited with code 3]", view.AllText());
    }

    [AvaloniaFact]
    public void A_local_shell_that_cannot_start_says_so_in_plain_text()
    {
        var session = new FakeLocalSession("cmd\u001b[2J");
        var view = new TerminalView(session);

        session.Change(SessionState.Failed, "The system cannot find the file specified.");

        Assert.Contains("Couldn't start cmd?[2J: The system cannot find the file specified.", view.AllText());
    }

    [AvaloniaFact]
    public void A_failure_message_is_capped()
    {
        var session = new FakeSession();
        var view = new TerminalView(session);

        session.Change(SessionState.Failed, new string('a', 1000));

        int shown = view.AllText().Count(c => c == 'a');
        Assert.InRange(shown, 200, 300);
    }
}
