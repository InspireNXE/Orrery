using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Orrery.App.Sessions;
using Orrery.Core.Connections;
using Orrery.Core.Sessions;
using Xunit;

namespace Orrery.App.Tests;

/// <summary>Opening a connection that's already open goes to its session: which one, when there are several or none.</summary>
public class OpenSessionForTests
{
    sealed class FakeSession : ITerminalSession
    {
        public ConnectionTarget Target { get; } = new("box", 22, "user", null);
        public SessionState State => SessionState.Connected;
#pragma warning disable CS0067 // Part of the interface; this fake never raises them.
        public event EventHandler<SessionStateChangedEventArgs>? StateChanged;
        public event Action<byte[]>? DataReceived;
#pragma warning restore CS0067
        public void Send(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static SessionTab Tab(Guid? entryId, bool files = false, bool managementPage = false) =>
        new(new FakeSession(), new Border(), new SessionOrigin("box", Protocol.Ssh, entryId, () => Task.CompletedTask)
        {
            Files = files,
            ManagementPage = managementPage,
        });

    [AvaloniaFact]
    public void A_connection_with_nothing_open_has_no_session_to_go_to()
    {
        var id = Guid.NewGuid();

        Assert.Null(SessionTab.OpenFor([Tab(Guid.NewGuid()), Tab(null)], id));
    }

    [AvaloniaFact]
    public void Of_several_sessions_to_one_connection_the_one_shown_last_is_chosen()
    {
        var id = Guid.NewGuid();
        var first = Tab(id);
        var second = Tab(id);
        second.MarkShown();
        first.MarkShown();

        Assert.Same(first, SessionTab.OpenFor([first, second, Tab(Guid.NewGuid())], id));

        second.MarkShown();
        Assert.Same(second, SessionTab.OpenFor([first, second], id));
    }

    [AvaloniaFact]
    public void Its_file_browser_and_management_page_are_not_the_session_it_opens()
    {
        var id = Guid.NewGuid();
        var files = Tab(id, files: true);
        var page = Tab(id, managementPage: true);
        files.MarkShown();
        page.MarkShown();

        Assert.Null(SessionTab.OpenFor([files, page], id));

        var terminal = Tab(id);
        Assert.Same(terminal, SessionTab.OpenFor([files, page, terminal], id));
    }
}
