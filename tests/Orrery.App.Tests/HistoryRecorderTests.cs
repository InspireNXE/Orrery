using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Orrery.App.Connections;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

public class HistoryRecorderTests : IDisposable
{
    static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "orrery-tests-" + Guid.NewGuid().ToString("N"));
    readonly ConnectionTree _tree = ConnectionTree.Empty();
    readonly ConnectionEntry _entry = new() { Name = "box", Host = "box.example" };
    readonly ConnectionHistory _history;
    readonly FakeSession _session = new();
    DateTimeOffset _clock = Start;

    public HistoryRecorderTests()
    {
        _history = new ConnectionHistory(Path.Combine(_dir, ConnectionHistory.FileName));
        _tree.Add(_entry, _tree.Root);
    }

    [AvaloniaFact]
    public void The_time_is_recorded_when_the_session_connects_not_when_it_is_opened()
    {
        Open(_entry.Id);

        Assert.Equal(ConnectionUse.None, _history.Get(_entry.Id));

        _clock = Start.AddSeconds(3);
        Change(SessionState.Connected);

        Assert.Equal(new ConnectionUse(Start.AddSeconds(3), null), _history.Get(_entry.Id));
    }

    [AvaloniaFact]
    public void A_session_that_never_gets_in_records_a_failed_attempt_and_no_success()
    {
        Open(_entry.Id);

        Change(SessionState.Failed, "Login refused");

        Assert.Equal(new ConnectionUse(null, Start), _history.Get(_entry.Id));
    }

    [AvaloniaFact]
    public void A_session_that_drops_after_connecting_is_not_a_failed_attempt()
    {
        Open(_entry.Id);
        Change(SessionState.Connected);

        _clock = Start.AddHours(1);
        Change(SessionState.Failed, "The network went away");

        Assert.Equal(new ConnectionUse(Start, null), _history.Get(_entry.Id));
    }

    [AvaloniaFact]
    public void A_quick_connect_has_no_record()
    {
        Open(null);

        Change(SessionState.Connected);

        Assert.False(File.Exists(Path.Combine(_dir, ConnectionHistory.FileName)));
    }

    [AvaloniaFact]
    public void A_connection_deleted_while_its_session_is_open_does_not_get_a_record_back()
    {
        var id = _entry.Id;
        Open(id);
        _tree.Remove(_entry);

        Change(SessionState.Connected);

        Assert.Equal(ConnectionUse.None, _history.Get(id));
    }

    void Open(Guid? entryId)
    {
        var tab = new SessionTab(_session, new Border(), new SessionOrigin("box", Protocol.Ssh, entryId, () => Task.CompletedTask));
        HistoryRecorder.Attach(tab, _tree, _history, () => _clock);
    }

    void Change(SessionState state, string? message = null)
    {
        _session.Set(state, message);
        Dispatcher.UIThread.RunJobs();
    }

    sealed class FakeSession : IConnectionSession
    {
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State { get; private set; } = SessionState.Connecting;
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Set(SessionState state, string? message = null)
        {
            State = state;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, message));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
